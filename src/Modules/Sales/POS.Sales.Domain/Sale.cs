using POS.SharedKernel;

namespace POS.Sales.Domain;

/// <summary>
/// The core transaction. Highest-traffic, most correctness-critical aggregate in the
/// product.
/// </summary>
/// <remarks>
/// <para>
/// Immutable once completed, per ADR 007. A void is a new document referencing this
/// one; a refund is a new document referencing this one. There is no code path that
/// edits a completed sale, and the API exposes none.
/// </para>
/// <para>
/// Every line SNAPSHOTS its pricing inputs — description, unit price, tax rate,
/// discounts, price list version, and cost at the moment of sale. It does not
/// reference live catalog data. A receipt reprinted in eighteen months must show what
/// was actually charged, and margin reporting must use the cost that applied on the
/// day, not today's average. A foreign key to a mutable price would silently rewrite
/// history every time someone edited a price list.
/// </para>
/// </remarks>
public sealed class Sale : AggregateRoot<Guid>, ITenantScoped, IBranchScoped
{
    private readonly List<SaleLine> _lines = [];
    private readonly List<Tender> _tenders = [];

    private Sale() { }

    public static Sale Open(
        Guid tenantId,
        Guid companyId,
        Guid branchId,
        Guid terminalId,
        Guid shiftId,
        Guid cashierId,
        string currency,
        ReceiptNumber receiptNumber,
        DateOnly businessDate,
        DateTimeOffset openedAt,
        Guid? saleId = null) => new()
    {
        // The caller may supply the identity. That is not a testing convenience: under
        // ADR 005 a sale's id is assigned by the TERMINAL, offline, at the moment the
        // sale is opened — the server is often not reachable and must never be on the
        // critical path of ringing up a customer.
        //
        // It matters most on the way back in. When a terminal uploads a sale the server
        // has to be able to tell a replay from a new sale, and to attribute the stock
        // movement to the same document both times; regenerating the id here would make
        // every retry look like a fresh sale and deplete stock twice.
        Id = saleId ?? SequentialId.NewAt(openedAt),
        TenantId = tenantId,
        CompanyId = companyId,
        BranchId = branchId,
        TerminalId = terminalId,
        ShiftId = shiftId,
        CashierId = cashierId,
        Currency = currency.ToUpperInvariant(),
        ReceiptNumber = receiptNumber,
        BusinessDate = businessDate,
        OpenedAt = openedAt,
        Status = SaleStatus.Open,

        // Zeroed rather than left default(Money): both are only meaningful after
        // Complete(), but Money's complex-property mapping requires a non-null
        // Currency column at insert time, and default(Money) has none (see Money's
        // own remarks on IsUninitialised). This is the same fix Shift.Open needed for
        // CountedCash/ExpectedCash/Variance, found the same way — a code path
        // (Suspend, saving an Open/Suspended sale without ever calling Complete) that
        // had never actually been run against a real database before.
        AmountTendered = Money.Zero(currency.ToUpperInvariant()),
        ChangeGiven = Money.Zero(currency.ToUpperInvariant())
    };

    public Guid TenantId { get; private set; }
    public Guid CompanyId { get; private set; }
    public Guid BranchId { get; private set; }
    public Guid TerminalId { get; private set; }
    public Guid ShiftId { get; private set; }

    /// <summary>The operator who opened the sale. A manager override records a second principal on the line.</summary>
    public Guid CashierId { get; private set; }

    public string Currency { get; private set; } = null!;
    public ReceiptNumber ReceiptNumber { get; private set; } = null!;
    public DateOnly BusinessDate { get; private set; }

    public DateTimeOffset OpenedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public SaleStatus Status { get; private set; }

    public IReadOnlyList<SaleLine> Lines => _lines.AsReadOnly();
    public IReadOnlyList<Tender> Tenders => _tenders.AsReadOnly();

    /// <summary>Set when this sale reverses or refunds an earlier one.</summary>
    public SaleReference? Reverses { get; private set; }

    public Money TotalExclusiveTax { get; private set; }
    public Money TotalTax { get; private set; }
    public Money TotalInclusiveTax { get; private set; }
    public Money TotalDiscount { get; private set; }

    /// <summary>
    /// Rounding applied to the payable total where the currency has no small
    /// denomination, e.g. Swiss or Swedish 0.05 cash rounding.
    /// </summary>
    /// <remarks>
    /// Held as its own field rather than folded into the total. It is a distinct line
    /// on the receipt, is often reported separately for tax, and hiding it inside
    /// another figure makes the sale impossible to reconcile.
    /// </remarks>
    public Money RoundingAdjustment { get; private set; }

    public Money AmountTendered { get; private set; }
    public Money ChangeGiven { get; private set; }

    /// <summary>
    /// Terminal that currently owns this sale.
    /// </summary>
    /// <remarks>
    /// Only meaningful while suspended. Ownership is exclusive and transferred
    /// explicitly, never shared — see ADR 037. Two terminals holding a live copy of
    /// one sale is the merge problem the whole sync design exists to avoid.
    /// </remarks>
    public Guid OwningTerminalId { get; private set; }

    /// <summary>
    /// Optimistic concurrency token, mapped to a SQL Server <c>rowversion</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Required by <see cref="Resume"/>. Two terminals can both read a sale in
    /// <see cref="SaleStatus.Suspended"/>, both pass the status check, and both claim
    /// ownership — last write wins, and one cashier's basket silently disappears
    /// while the other is served a basket someone else is standing in front of.
    /// The status check alone is a time-of-check/time-of-use race; only a conditional
    /// update closes it.
    /// </para>
    /// <para>
    /// Mapped in the SQL Server context only. Terminal-local SQLite has no
    /// <c>rowversion</c> type and needs none: a sale in the local store has exactly
    /// one writer by construction. The race exists solely on the shared server where
    /// cross-terminal resume happens.
    /// </para>
    /// <para>
    /// Mirrors the token already proven on <c>StockBalance</c> in Phase 4 rather than
    /// introducing a second concurrency idiom.
    /// </para>
    /// </remarks>
    public byte[] RowVersion { get; private set; } = [];

    // ---------------------------------------------------------------------
    // Line management
    // ---------------------------------------------------------------------

    public Result AddLine(SaleLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (Status != SaleStatus.Open)
        {
            return Result.Failure(SalesErrors.SaleNotOpen(Status));
        }

        if (line.Currency != Currency)
        {
            return Result.Failure(SalesErrors.CurrencyMismatch(Currency, line.Currency));
        }

        // A grocery basket legitimately reaches 200 lines; beyond a few hundred the
        // aggregate stops being loadable within the checkout latency budget. The cap
        // is a guard against a scanner stuck in repeat, not a business rule.
        if (_lines.Count >= MaxLines)
        {
            return Result.Failure(SalesErrors.TooManyLines(MaxLines));
        }

        line.AssignLineNumber(_lines.Count + 1);
        _lines.Add(line);
        return Result.Success();
    }

    public const int MaxLines = 500;

    /// <summary>
    /// Removes a line from an OPEN sale.
    /// </summary>
    /// <remarks>
    /// Permitted only before completion, when nothing has been reported to anyone and
    /// no stock has moved. After completion the equivalent operation is a refund,
    /// which is a new document. Line numbers are then resequenced so the receipt has
    /// no gaps — a gap looks like a removed line to an auditor.
    /// </remarks>
    public Result RemoveLine(int lineNumber)
    {
        if (Status != SaleStatus.Open)
        {
            return Result.Failure(SalesErrors.SaleNotOpen(Status));
        }

        var line = _lines.FirstOrDefault(l => l.LineNumber == lineNumber);
        if (line is null)
        {
            return Result.Failure(SalesErrors.LineNotFound(lineNumber));
        }

        _lines.Remove(line);

        for (var i = 0; i < _lines.Count; i++)
        {
            _lines[i].AssignLineNumber(i + 1);
        }

        return Result.Success();
    }

    // ---------------------------------------------------------------------
    // Totals
    // ---------------------------------------------------------------------

    /// <summary>
    /// Recomputes totals from the lines.
    /// </summary>
    /// <remarks>
    /// The reconciliation invariant this system is built around: the sum of the lines
    /// equals the sale total, exactly, with no residual cent. Totals are always
    /// DERIVED here and never accepted from a caller, so there is exactly one place
    /// the arithmetic can be wrong. See ADR 036.
    /// </remarks>
    public void RecalculateTotals(Money orderRounding)
    {
        var zero = Money.Zero(Currency);

        TotalExclusiveTax = _lines.Aggregate(zero, (sum, l) => sum + l.NetAmount);
        TotalTax = _lines.Aggregate(zero, (sum, l) => sum + l.TaxAmount);
        TotalDiscount = _lines.Aggregate(zero, (sum, l) => sum + l.DiscountAmount);
        RoundingAdjustment = orderRounding;
        TotalInclusiveTax = TotalExclusiveTax + TotalTax + orderRounding;
    }

    /// <summary>
    /// Applies a pricing result to the lines and recomputes the totals.
    /// </summary>
    /// <remarks>
    /// The single entry point by which line amounts may be set. <c>SaleLine</c> keeps
    /// its mutators internal, so nothing outside the domain can assign a line total
    /// directly — an arbitrary total set by a caller would bypass the pricing pipeline
    /// and the reconciliation invariant it guarantees.
    /// <para>
    /// Takes a plain <see cref="LinePricing"/> record rather than the pricing engine's
    /// own type, so the domain does not depend on the application layer.
    /// </para>
    /// </remarks>
    public Result ApplyPricing(IReadOnlyList<LinePricing> pricing, Money orderRounding)
    {
        ArgumentNullException.ThrowIfNull(pricing);

        if (Status != SaleStatus.Open)
        {
            return Result.Failure(SalesErrors.SaleNotOpen(Status));
        }

        foreach (var priced in pricing)
        {
            var line = _lines.FirstOrDefault(l => l.LineNumber == priced.LineNumber);
            if (line is null)
            {
                return Result.Failure(SalesErrors.LineNotFound(priced.LineNumber));
            }

            line.ApplyTotals(priced.Discount, priced.Net, priced.Tax, priced.Gross);
        }

        RecalculateTotals(orderRounding);
        return Result.Success();
    }

    /// <summary>The amount still to be paid.</summary>
    public Money BalanceDue =>
        TotalInclusiveTax - _tenders.Aggregate(Money.Zero(Currency), (sum, t) => sum + t.Amount);

    // ---------------------------------------------------------------------
    // Tender
    // ---------------------------------------------------------------------

    /// <summary>Applies a tender to the sale, settling it if the balance reaches zero.</summary>
    /// <param name="tender">The tender being offered, in the sale's currency.</param>
    /// <param name="terminalIsOnline">
    /// Whether the terminal currently has connectivity.
    /// </param>
    /// <remarks>
    /// Connectivity is a REQUIRED argument rather than an optional one with a
    /// convenient default. ADR 038 refuses balance-bearing instruments offline to
    /// prevent double-spend across terminals, and that rule previously existed only
    /// as an unused helper on <see cref="TenderMethod"/> — documented, tested by
    /// nobody, and enforced nowhere. A default value would have quietly reintroduced
    /// the same hole: whichever value we chose would be wrong half the time, and
    /// silently so. Making it required forces every call site to answer a question
    /// whose wrong answer costs real money.
    /// </remarks>
    public Result AddTender(Tender tender, bool terminalIsOnline)
    {
        ArgumentNullException.ThrowIfNull(tender);

        if (Status != SaleStatus.Open)
        {
            return Result.Failure(SalesErrors.SaleNotOpen(Status));
        }

        if (tender.Amount.Currency != Currency)
        {
            return Result.Failure(SalesErrors.CurrencyMismatch(Currency, tender.Amount.Currency));
        }

        // A gift card redeemed on two disconnected terminals is spent twice, and the
        // shortfall is only discovered at sync — by which point both customers have
        // left with their goods.
        if (!terminalIsOnline && tender.Method.RequiresConnectivity())
        {
            return Result.Failure(SalesErrors.TenderRequiresConnectivity(tender.Method));
        }

        // Overtender is legitimate for cash — the customer hands over a note and
        // receives change. It is NOT legitimate for card or gift card, where taking
        // more than the balance and returning cash is both a fraud vector and, for
        // card, a scheme rules violation.
        if (!tender.Method.AllowsOvertender() && tender.Amount > BalanceDue)
        {
            return Result.Failure(SalesErrors.OvertenderNotAllowed(tender.Method));
        }

        _tenders.Add(tender);
        return Result.Success();
    }

    // ---------------------------------------------------------------------
    // State transitions
    // ---------------------------------------------------------------------

    public Result Complete(DateTimeOffset completedAt)
    {
        if (Status != SaleStatus.Open)
        {
            return Result.Failure(SalesErrors.SaleNotOpen(Status));
        }

        if (_lines.Count == 0)
        {
            return Result.Failure(SalesErrors.EmptySale);
        }

        var balance = BalanceDue;

        if (balance > Money.Zero(Currency))
        {
            return Result.Failure(SalesErrors.UnderTendered(balance));
        }

        // A negative balance is change owed. Only cash can produce it, which
        // AddTender already guarantees.
        ChangeGiven = -balance;
        AmountTendered = _tenders.Aggregate(Money.Zero(Currency), (sum, t) => sum + t.Amount);

        Status = SaleStatus.Completed;
        CompletedAt = completedAt;

        Raise(new SaleCompleted
        {
            OccurredAt = completedAt,
            SaleId = Id,
            TenantId = TenantId,
            BranchId = BranchId,
            TerminalId = TerminalId,
            BusinessDate = BusinessDate,
            Total = TotalInclusiveTax
        });

        return Result.Success();
    }

    /// <summary>
    /// Abandons an OPEN sale. Not a reversal — nothing happened yet.
    /// </summary>
    /// <remarks>
    /// Distinct from voiding a completed sale, which is a financial event requiring a
    /// reversing document. Collapsing the two would either create phantom reversals
    /// for abandoned baskets or allow completed sales to be erased.
    /// </remarks>
    public Result Cancel(DateTimeOffset cancelledAt, Guid cancelledBy, string reason)
    {
        if (Status != SaleStatus.Open && Status != SaleStatus.Suspended)
        {
            return Result.Failure(SalesErrors.SaleNotOpen(Status));
        }

        Status = SaleStatus.Cancelled;
        CompletedAt = cancelledAt;

        Raise(new SaleCancelled
        {
            OccurredAt = cancelledAt,
            SaleId = Id,
            CancelledBy = cancelledBy,
            Reason = reason
        });

        return Result.Success();
    }

    /// <summary>Parks the sale so the till can serve the next customer.</summary>
    public Result Suspend(DateTimeOffset suspendedAt)
    {
        if (Status != SaleStatus.Open)
        {
            return Result.Failure(SalesErrors.SaleNotOpen(Status));
        }

        if (_tenders.Count > 0)
        {
            // Money has already been taken. Parking it would leave a payment attached
            // to a transaction nobody owns.
            return Result.Failure(SalesErrors.CannotSuspendWithTenders);
        }

        Status = SaleStatus.Suspended;
        OwningTerminalId = TerminalId;
        return Result.Success();
    }

    /// <summary>
    /// Resumes a suspended sale, transferring ownership to the resuming terminal.
    /// </summary>
    /// <remarks>
    /// Ownership transfer, not replication. Exactly one terminal owns a suspended sale
    /// at any moment, so two tills can never hold divergent copies that later need
    /// merging — the problem the sync asymmetry in ADR 004 exists to design out.
    /// Cross-terminal resume therefore requires connectivity; same-terminal resume
    /// works offline. See ADR 037.
    /// </remarks>
    public Result Resume(Guid resumingTerminalId, DateTimeOffset resumedAt)
    {
        if (Status != SaleStatus.Suspended)
        {
            return Result.Failure(SalesErrors.SaleNotSuspended(Status));
        }

        Status = SaleStatus.Open;
        OwningTerminalId = resumingTerminalId;
        return Result.Success();
    }

    /// <summary>Records that this sale reverses an earlier one.</summary>
    public void MarkAsReversalOf(SaleReference original) => Reverses = original;

    /// <summary>
    /// Marks a completed sale as reversed by a later document.
    /// </summary>
    /// <remarks>
    /// Guarded. This type's own documentation states that Voided means a COMPLETED
    /// sale was reversed, but the method previously assigned the status
    /// unconditionally — an open basket or an already-cancelled sale could be voided,
    /// producing a financial record that never had a financial event behind it.
    /// The distinction between Cancelled and Voided is only meaningful if the
    /// transition is enforced.
    /// </remarks>
    public Result MarkVoided()
    {
        if (Status != SaleStatus.Completed)
        {
            return Result.Failure(SalesErrors.OnlyCompletedSalesCanBeVoided(Status));
        }

        Status = SaleStatus.Voided;
        return Result.Success();
    }
}

/// <remarks>
/// <c>Cancelled</c> and <c>Voided</c> are deliberately separate. Cancelled means an
/// open basket was abandoned before any financial event; Voided means a COMPLETED
/// sale was reversed by a subsequent document. Merging them loses the distinction
/// between "never happened" and "happened and was undone", which is exactly the
/// distinction an auditor asks about.
/// </remarks>
public enum SaleStatus
{
    Open = 0,
    Suspended = 1,
    Completed = 2,
    Cancelled = 3,
    Voided = 4
}

public sealed record SaleReference(Guid SaleId, string ReceiptNumber, DateOnly BusinessDate);

public sealed record SaleCompleted : DomainEvent
{
    public required Guid SaleId { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid BranchId { get; init; }
    public required Guid TerminalId { get; init; }
    public required DateOnly BusinessDate { get; init; }
    public required Money Total { get; init; }
}

public sealed record SaleCancelled : DomainEvent
{
    public required Guid SaleId { get; init; }
    public required Guid CancelledBy { get; init; }
    public required string Reason { get; init; }
}

/// <summary>Priced amounts for one line, handed to the aggregate by the pricing engine.</summary>
/// <remarks>
/// A domain-owned DTO so <c>POS.Sales.Domain</c> never references the pricing
/// pipeline in the application layer. The dependency points inward, as it must.
/// </remarks>
public sealed record LinePricing(
    int LineNumber,
    Money Discount,
    Money Net,
    Money Tax,
    Money Gross);
