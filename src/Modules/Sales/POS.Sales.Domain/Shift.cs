using POS.SharedKernel;

namespace POS.Sales.Domain;

/// <summary>
/// A cashier's trading session on a terminal, and the cash accountability that goes
/// with it.
/// </summary>
/// <remarks>
/// The shift is where <see cref="BusinessDate"/> is fixed for every sale it contains.
/// Deriving the business date per sale from the wall clock is the defect that
/// silently corrupts daily reporting in bars, late-night stores, and anywhere trading
/// crosses midnight — see ADR 017. Fixing it once, at shift open, means every sale in
/// the session books to the same trading day by construction.
/// </remarks>
public sealed class Shift : AggregateRoot<Guid>, ITenantScoped, IBranchScoped
{
    private readonly List<CashMovement> _cashMovements = [];

    private Shift() { }

    public static Shift Open(
        Guid tenantId,
        Guid branchId,
        Guid terminalId,
        Guid cashierId,
        Money openingFloat,
        DateOnly businessDate,
        DateTimeOffset openedAt) => new()
    {
        Id = SequentialId.NewAt(openedAt),
        TenantId = tenantId,
        BranchId = branchId,
        TerminalId = terminalId,
        CashierId = cashierId,
        OpeningFloat = openingFloat,
        Currency = openingFloat.Currency,
        BusinessDate = businessDate,
        OpenedAt = openedAt,
        Status = ShiftStatus.Open,

        // Zeroed rather than left default(Money): these three are only meaningful
        // after Close(), but Money's complex-property mapping requires a non-null
        // Currency column at insert time, and default(Money) has none (see Money's
        // own remarks on IsUninitialised). An open shift genuinely has zero counted/
        // expected/variance — Close() overwrites all three with the real figures.
        CountedCash = Money.Zero(openingFloat.Currency),
        ExpectedCash = Money.Zero(openingFloat.Currency),
        Variance = Money.Zero(openingFloat.Currency)
    };

    public Guid TenantId { get; private set; }
    public Guid BranchId { get; private set; }
    public Guid TerminalId { get; private set; }
    public Guid CashierId { get; private set; }

    public string Currency { get; private set; } = null!;
    public Money OpeningFloat { get; private set; }
    public DateOnly BusinessDate { get; private set; }

    public DateTimeOffset OpenedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public ShiftStatus Status { get; private set; }

    public Money CountedCash { get; private set; }
    public Money ExpectedCash { get; private set; }

    /// <summary>Counted minus expected. Positive is over, negative is short.</summary>
    public Money Variance { get; private set; }

    public IReadOnlyList<CashMovement> CashMovements => _cashMovements.AsReadOnly();

    /// <summary>Optimistic concurrency token.</summary>
    /// <remarks>
    /// A shift is touched by more than one actor: a cashier recording a cash drop, a
    /// supervisor recording a pickup, and the close routine reading the total. Without
    /// a token, two concurrent movements last-write-wins and the drawer silently fails
    /// to balance — the exact discrepancy the blind-close design exists to surface.
    /// </remarks>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>
    /// Removes cash from the drawer mid-shift, typically to a safe.
    /// </summary>
    /// <remarks>
    /// Drops and pickups must be first-class events rather than adjustments made at
    /// close. A drawer holding more than the float limit is a robbery risk, and
    /// reconstructing the day's cash position afterwards is impossible if the
    /// movements were never recorded.
    /// </remarks>
    public Result RecordCashDrop(Money amount, Guid performedBy, DateTimeOffset at, string? reference)
    {
        if (Status != ShiftStatus.Open)
        {
            return Result.Failure(ShiftErrors.ShiftNotOpen(Status));
        }

        if (amount.IsNegative || amount.IsZero)
        {
            return Result.Failure(ShiftErrors.InvalidCashAmount);
        }

        _cashMovements.Add(CashMovement.Create(CashMovementKind.Drop, -amount, performedBy, at, reference));
        return Result.Success();
    }

    public Result RecordCashPickup(Money amount, Guid performedBy, DateTimeOffset at, string? reference)
    {
        if (Status != ShiftStatus.Open)
        {
            return Result.Failure(ShiftErrors.ShiftNotOpen(Status));
        }

        if (amount.IsNegative || amount.IsZero)
        {
            return Result.Failure(ShiftErrors.InvalidCashAmount);
        }

        _cashMovements.Add(CashMovement.Create(CashMovementKind.Pickup, amount, performedBy, at, reference));
        return Result.Success();
    }

    /// <summary>
    /// Expected drawer contents: float, plus cash taken, less cash refunded and dropped.
    /// </summary>
    public Money CalculateExpectedCash(Money cashSales, Money cashRefunds)
    {
        var movements = _cashMovements.Aggregate(
            Money.Zero(Currency), (sum, m) => sum + m.Amount);

        return OpeningFloat + cashSales - cashRefunds + movements;
    }

    /// <summary>
    /// Closes the shift against a physical count.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Blind close by default: the cashier enters the counted amount WITHOUT being
    /// shown the expected figure. Displaying it first produces counts that agree with
    /// the system rather than with the drawer, which destroys the control entirely —
    /// the same reasoning as blind stocktake counting in ADR 029.
    /// </para>
    /// <para>
    /// Variance is recorded, never silently corrected. It is a primary loss-prevention
    /// signal, and variance by operator over time is one of the most reliable
    /// indicators of till fraud.
    /// </para>
    /// </remarks>
    public Result Close(Money countedCash, Money cashSales, Money cashRefunds, DateTimeOffset closedAt)
    {
        if (Status != ShiftStatus.Open)
        {
            return Result.Failure(ShiftErrors.ShiftNotOpen(Status));
        }

        if (countedCash.Currency != Currency)
        {
            return Result.Failure(ShiftErrors.CurrencyMismatch(Currency, countedCash.Currency));
        }

        ExpectedCash = CalculateExpectedCash(cashSales, cashRefunds);
        CountedCash = countedCash;
        Variance = countedCash - ExpectedCash;

        Status = ShiftStatus.Closed;
        ClosedAt = closedAt;

        Raise(new ShiftClosed
        {
            OccurredAt = closedAt,
            ShiftId = Id,
            TerminalId = TerminalId,
            CashierId = CashierId,
            BusinessDate = BusinessDate,
            Expected = ExpectedCash,
            Counted = countedCash,
            Variance = Variance
        });

        return Result.Success();
    }

    public bool HasVariance => !Variance.IsZero;
}

public enum ShiftStatus { Open = 0, Closed = 1 }

/// <summary>Cash entering or leaving the drawer other than through a sale.</summary>
public sealed class CashMovement : Entity<Guid>
{
    private CashMovement() { }

    public static CashMovement Create(
        CashMovementKind kind,
        Money amount,
        Guid performedBy,
        DateTimeOffset at,
        string? reference) => new()
    {
        Id = SequentialId.NewAt(at),
        Kind = kind,
        Amount = amount,
        PerformedBy = performedBy,
        OccurredAt = at,
        Reference = reference
    };

    public CashMovementKind Kind { get; private set; }

    /// <summary>Signed: negative removes cash from the drawer.</summary>
    /// <remarks>
    /// Signed deltas for the same reason the stock ledger uses them (ADR 025):
    /// summing a list of signed movements is order-independent and cannot disagree
    /// with itself, whereas storing absolute drawer states invites two figures that
    /// contradict each other.
    /// </remarks>
    public Money Amount { get; private set; }

    public Guid PerformedBy { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public string? Reference { get; private set; }
}

public enum CashMovementKind { Drop = 0, Pickup = 1, PettyCash = 2, Correction = 3 }

public sealed record ShiftClosed : DomainEvent
{
    public required Guid ShiftId { get; init; }
    public required Guid TerminalId { get; init; }
    public required Guid CashierId { get; init; }
    public required DateOnly BusinessDate { get; init; }
    public required Money Expected { get; init; }
    public required Money Counted { get; init; }
    public required Money Variance { get; init; }
}

public static class ShiftErrors
{
    public static Error ShiftNotOpen(ShiftStatus status) => Error.BusinessRule(
        "shifts.not_open", $"This shift is {status}.");

    public static Error InvalidCashAmount => Error.Validation(
        "shifts.invalid_cash_amount", "A cash movement must be a positive amount.");

    public static Error CurrencyMismatch(string expected, string actual) => Error.Validation(
        "shifts.currency_mismatch", $"This shift is in {expected}; received {actual}.");
}
