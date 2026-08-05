using Microsoft.EntityFrameworkCore;
using POS.Fiscal.Domain;
using POS.Fiscal.Generic;
using POS.Fiscal.Pipeline;
using POS.SharedKernel;

namespace POS.Fiscal.Persistence;

/// <summary>EF Core implementation of the fiscal document store.</summary>
public sealed class EfFiscalDocumentStore(FiscalDbContext db) : IFiscalDocumentStore
{
    public async Task AddAsync(FiscalDocument document, CancellationToken ct = default)
    {
        db.Documents.Add(document);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The previous link in the hash chain.
    /// </summary>
    /// <remarks>
    /// Ordered by Sequence, not by IssuedAt. The sequence is the authority: two
    /// documents issued in the same tick, or a terminal whose clock has been corrected
    /// backwards, would give a timestamp ordering that disagrees with the chain — and
    /// a chain built on the wrong predecessor fails validation for every document
    /// after it, permanently.
    /// </remarks>
    public Task<string?> GetLastCanonicalHashAsync(
        Guid companyId,
        Guid terminalId,
        string series,
        CancellationToken ct = default) =>
        db.Documents
          .Where(d => d.CompanyId == companyId && d.TerminalId == terminalId && d.Series == series)
          .OrderByDescending(d => d.Sequence)
          .Select(d => d.CanonicalHash)
          .FirstOrDefaultAsync(ct)!;

    /// <summary>Documents awaiting transmission, oldest first.</summary>
    /// <remarks>
    /// "Awaiting transmission" is <c>Issued</c> with a deadline set — NOT
    /// <c>PendingAuthority</c>. A pending document has already been sent and the
    /// authority has not yet decided; the instruction there is to poll, never to
    /// resubmit, because resubmitting produces a duplicate declaration.
    /// </remarks>
    public async Task<IReadOnlyList<FiscalDocument>> GetPendingTransmissionAsync(
        Guid companyId,
        int maxCount,
        CancellationToken ct = default) =>
        await db.Documents
                .Where(d => d.CompanyId == companyId
                         && d.Status == FiscalDocumentStatus.Issued
                         && d.TransmissionDueBy != null)
                .OrderBy(d => d.IssuedAt)
                .Take(maxCount)
                .ToListAsync(ct);

    /// <summary>
    /// Documents past their statutory deadline — an operational alarm, not a queue.
    /// </summary>
    /// <remarks>
    /// The predicate mirrors <c>FiscalDocument.IsOverdue</c> exactly, including that a
    /// PendingAuthority document can be overdue: sent-but-undecided past the deadline
    /// is still a breach, and it is the case an operator most needs to see because
    /// nothing is retrying it.
    ///
    /// Deliberately NOT filtered by tenant. An overdue fiscal document is a regulatory
    /// exposure for the platform operator, and the sweep that reads this runs as a
    /// system operation across every tenant.
    /// </remarks>
    public async Task<IReadOnlyList<FiscalDocument>> GetOverdueAsync(
        DateTimeOffset now,
        CancellationToken ct = default) =>
        await db.Documents
                .IgnoreQueryFilters()
                .Where(d => d.TransmissionDueBy != null
                         && d.TransmissionDueBy < now
                         && (d.Status == FiscalDocumentStatus.Issued
                          || d.Status == FiscalDocumentStatus.PendingAuthority))
                .OrderBy(d => d.TransmissionDueBy)
                .ToListAsync(ct);
}

/// <summary>Allocates gap-free fiscal numbers from a per-series counter row.</summary>
/// <remarks>
/// GAP-FREE IS THE REQUIREMENT, and it is why this is a table row rather than a SQL
/// Server SEQUENCE or an IDENTITY column. Both of those allocate outside the
/// transaction and therefore leak numbers on rollback; a missing number in a fiscal
/// series is a finding an auditor raises, and "the transaction failed" is not an
/// accepted answer in most regimes.
///
/// The cost is a row lock per issuance, serialising numbering per (company, terminal,
/// series). That is acceptable precisely because the series is per TERMINAL: two tills
/// never contend with each other, so the lock is only ever held against that till's own
/// next sale.
/// </remarks>
public sealed class EfFiscalSequenceAllocator(FiscalDbContext db) : IFiscalSequenceAllocator
{
    public async Task<Result<long>> NextAsync(
        Guid companyId,
        Guid terminalId,
        string series,
        CancellationToken ct = default)
    {
        // JOINS THE CALLER'S TRANSACTION when there is one, and this is the whole
        // correctness argument rather than a tidy-up.
        //
        // Allocating in a transaction of its own and committing immediately makes the
        // number durable BEFORE the document that uses it is written. Two concurrent
        // issuances then interleave — each releases the row lock the moment it has its
        // number, so nothing stops the second from reading the row again before the
        // first document exists — and, worse, a failure after allocation leaves a number
        // consumed by no document. That is a GAP, which is the exact finding this whole
        // table-row design exists to prevent (a SQL SEQUENCE would have been simpler and
        // leaks numbers for the same reason).
        //
        // Holding the caller's transaction keeps the UPDLOCK from the read all the way
        // through the document insert: the series is serialised for the duration, and a
        // rollback returns the number instead of burning it.
        if (db.Database.CurrentTransaction is not null)
            return await AllocateAsync(companyId, terminalId, series, ct);

        var strategy = db.Database.CreateExecutionStrategy();

        // No ambient transaction — a caller allocating a number on its own. The retrying
        // execution strategy will not accept a user-initiated transaction unless it owns
        // the whole retryable unit, and it must own this one: retrying half of a
        // read-modify-write would allocate the same number twice.
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var result = await AllocateAsync(companyId, terminalId, series, ct);

            await transaction.CommitAsync(ct);
            return result;
        });
    }

    private async Task<Result<long>> AllocateAsync(
        Guid companyId,
        Guid terminalId,
        string series,
        CancellationToken ct)
    {
        {
            var counter = await db.Sequences
                .FromSql($"""
                          SELECT * FROM fiscal.FiscalSequences WITH (UPDLOCK, HOLDLOCK)
                           WHERE CompanyId  = {companyId}
                             AND TerminalId = {terminalId}
                             AND Series     = {series}
                          """)
                .FirstOrDefaultAsync(ct);

            if (counter is null)
            {
                counter = FiscalSequence.Start(companyId, terminalId, series);
                db.Sequences.Add(counter);
            }

            var allocated = counter.Next();

            await db.SaveChangesAsync(ct);

            return Result<long>.Success(allocated);
        }
    }
}
