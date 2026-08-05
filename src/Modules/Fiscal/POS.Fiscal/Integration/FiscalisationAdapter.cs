using Microsoft.EntityFrameworkCore;
using POS.Common.Persistence;
using POS.Contracts.Fiscal;
using POS.Contracts.Identity;
using POS.Fiscal.Abstractions;
using POS.Fiscal.Persistence;
using POS.Fiscal.Pipeline;
using POS.SharedKernel;

namespace POS.Fiscal.Integration;

/// <summary>
/// Turns a sale into a fiscal document, behind <see cref="IFiscalisationPort"/>.
/// </summary>
/// <remarks>
/// THE ANTI-CORRUPTION LAYER for fiscalisation. Everything jurisdiction-shaped —
/// profile codes, document types, the seller's tax identity, the ordering of validate /
/// number / build / sign / transmit — is decided on this side of the port. The caller
/// hands over a sale and gets back a number to print.
///
/// There is deliberately no country branching here, exactly as there is none in the
/// pipeline. The profile comes from the company's own record and the pipeline drives
/// whichever seams that profile implements; a <c>if (country == …)</c> appearing in this
/// file would mean the abstraction had failed (ADR 031), and the architecture suite
/// fails the build if one does.
/// </remarks>
public sealed class FiscalisationAdapter(
    FiscalDbContext db,
    FiscalisationPipeline pipeline,
    ICompanyDirectory companies) : IFiscalisationPort
{
    /// <summary>
    /// How many times to re-take a number when another issuance beat us to it.
    /// </summary>
    /// <remarks>
    /// Contention is bounded in practice because a fiscal series is per TERMINAL — two
    /// tills never compete for the same series, so the only racers are duplicate uploads
    /// of the same terminal's batch. Three attempts is generous for that; exhausting
    /// them means something is wrong that a fourth would not fix.
    /// </remarks>
    private const int MaxNumberingAttempts = 3;

    public async Task<Result<FiscalisationOutcome>> FiscaliseSaleAsync(
        FiscaliseSaleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // IDEMPOTENCY FIRST, and it matters more here than almost anywhere else: fiscal
        // series are gap-free and sequential, so issuing a second document for the same
        // sale does not just duplicate a row, it consumes a number. Both a gap and a
        // duplicate are audit findings.
        var existing = await db.Documents
            .AsNoTracking()
            .Where(d => d.SaleId == request.SaleId)
            .Select(d => new FiscalisationOutcome(d.Id, d.FormattedNumber, d.QrPayload))
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
            return Result<FiscalisationOutcome>.Success(existing);

        var company = await companies.FindFiscalIdentityAsync(request.CompanyId, cancellationToken);

        if (company is null)
        {
            return Result<FiscalisationOutcome>.Failure(Error.NotFound(
                "fiscal.company_unknown",
                "The sale names a company that does not exist, so no fiscal identity can be established."));
        }

        var context = new FiscalContext
        {
            CompanyId = request.CompanyId,
            BranchId = request.BranchId,
            TerminalId = request.TerminalId,
            CountryCode = company.CountryCode,
            SellerTaxRegistration = company.TaxRegistrationNumber,

            SaleId = request.SaleId,
            IssuedAt = request.IssuedAt,
            BusinessDate = request.BusinessDate,
            Currency = request.Currency,

            Lines = [.. request.Lines.Select(l => new FiscalLine(
                l.LineNumber,
                l.Description,
                ItemCode: null,
                l.Quantity,
                l.UnitOfMeasure,
                l.UnitPriceExclusiveTax,
                l.DiscountAmount,
                l.TaxCode,
                l.TaxRate,
                l.TaxAmount,
                l.LineTotalInclusiveTax))],

            TotalExclusiveTax = request.TotalExclusiveTax,
            TotalTax = request.TotalTax,
            TotalInclusiveTax = request.TotalInclusiveTax,

            // Null buyer is what makes this a simplified invoice in most regimes — the
            // ordinary case for a walk-in customer at a till.
            Buyer = null,

            IsOffline = request.IssuedOffline
        };

        // ONE TRANSACTION AROUND NUMBERING AND ISSUANCE. The allocator takes a row lock
        // on the series and joins this transaction rather than committing its own, so
        // the lock is held from "what is the next number" all the way through "the
        // document using it exists". Without that, two concurrent sales on the same
        // terminal read the same counter and issue the same number — and a failure
        // between the two steps would burn a number, leaving a gap.
        //
        // Both are audit findings, which is why this is worth a transaction rather than
        // a retry.
        for (var attempt = 1; attempt <= MaxNumberingAttempts; attempt++)
        {
            var issued = await TryIssueAsync(context, company.FiscalProfileCode, cancellationToken);

            if (issued is not null)
                return issued.Value;

            // Lost the race for a number. Another terminal issuance on this same series
            // committed between our read of the counter and our insert, so the number we
            // were given is now taken.
            //
            // NO GAP RESULTS, and that is only true because allocation and issuance
            // share one transaction: our rollback returned our number to the series
            // rather than burning it. Retrying re-reads the counter, which has since
            // advanced, and takes the next one.
            db.ChangeTracker.Clear();

            // A document may have appeared for this sale while we were racing — two
            // uploads of the same batch do exactly that. Whoever won has issued it, and
            // issuing a second would consume another number for no reason.
            var raced = await db.Documents
                .AsNoTracking()
                .Where(d => d.SaleId == request.SaleId)
                .Select(d => new FiscalisationOutcome(d.Id, d.FormattedNumber, d.QrPayload))
                .FirstOrDefaultAsync(cancellationToken);

            if (raced is not null)
                return Result<FiscalisationOutcome>.Success(raced);
        }

        return Result<FiscalisationOutcome>.Failure(Error.Conflict(
            "fiscal.numbering_contended",
            $"Could not obtain a fiscal number after {MaxNumberingAttempts} attempts."));
    }

    /// <summary>
    /// One attempt at numbering and issuing, in a single transaction.
    /// </summary>
    /// <returns>The outcome, or null when the allocated number was taken by a racer.</returns>
    /// <remarks>
    /// ONE TRANSACTION AROUND NUMBERING AND ISSUANCE. The allocator takes a row lock on
    /// the series and joins this transaction rather than committing its own, so a
    /// failure anywhere in the pipeline rolls the number back instead of consuming it.
    /// Both a gap and a duplicate in a fiscal series are audit findings, which is why
    /// this is worth a transaction rather than a best-effort retry.
    /// </remarks>
    private async Task<Result<FiscalisationOutcome>?> TryIssueAsync(
        FiscalContext context,
        string profileCode,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                var issued = await pipeline.FiscaliseAsync(
                    context, profileCode, db.CurrentTenantId, cancellationToken);

                if (issued.IsFailure)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result<FiscalisationOutcome>.Failure(issued.Error);
                }

                await transaction.CommitAsync(cancellationToken);

                return Result<FiscalisationOutcome>.Success(new FiscalisationOutcome(
                    issued.Value.Id,
                    issued.Value.FormattedNumber,
                    issued.Value.QrPayload));
            }
            catch (DbUpdateException ex) when (UniqueViolation.Matches(ex))
            {
                await transaction.RollbackAsync(cancellationToken);
                return (Result<FiscalisationOutcome>?)null;
            }
        });
    }
}
