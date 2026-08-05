using POS.SharedKernel;

namespace POS.Inventory.Domain;

/// <summary>
/// A physical count. Records what was counted; never overwrites the balance.
/// </summary>
/// <remarks>
/// <para>
/// Counting 5 where the system says 7 produces an adjustment movement of −2 with a
/// reason code. It does not set the balance to 5. Overwriting would destroy the
/// variance, and the variance is the entire commercial point of counting: it is how
/// shrinkage is measured, and shrinkage typically runs at 1–2% of retail revenue. A
/// stocktake that silently corrects balances tells you nothing and hides theft.
/// See ADR 029.
/// </para>
/// <para>
/// Counting while trading is normal in retail, and the delta-based ledger handles it
/// without a freeze: sales that occur during the count are simply later movements. A
/// snapshot-and-replace design would swallow them.
/// </para>
/// </remarks>
public sealed class Stocktake : AggregateRoot<Guid>, ITenantScoped
{
    private readonly List<StocktakeLine> _lines = [];

    private Stocktake() { }

    public static Stocktake Start(
        Guid tenantId,
        Guid warehouseId,
        string stocktakeNumber,
        StocktakeScope scope,
        bool isBlind,
        Guid startedByUserId,
        DateTimeOffset now,
        DateOnly businessDate) => new()
        {
            Id = SequentialId.New(),
            TenantId = tenantId,
            WarehouseId = warehouseId,
            StocktakeNumber = stocktakeNumber,
            Scope = scope,
            IsBlind = isBlind,
            Status = StocktakeStatus.Counting,
            StartedByUserId = startedByUserId,
            StartedAt = now,
            BusinessDate = businessDate
        };

    public Guid TenantId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public string StocktakeNumber { get; private set; } = null!;

    public StocktakeScope Scope { get; private set; }

    /// <summary>
    /// When true the counter cannot see the expected quantity.
    /// </summary>
    /// <remarks>
    /// Strongly recommended, and the reason is human rather than technical: shown the
    /// expected number, a counter under time pressure confirms it rather than counts.
    /// A blind count measures reality; a visible count measures agreement.
    /// </remarks>
    public bool IsBlind { get; private set; }

    public StocktakeStatus Status { get; private set; }
    public DateOnly BusinessDate { get; private set; }

    public Guid StartedByUserId { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }

    public Guid? PostedByUserId { get; private set; }
    public DateTimeOffset? PostedAt { get; private set; }

    public IReadOnlyList<StocktakeLine> Lines => _lines.AsReadOnly();

    /// <summary>Optimistic concurrency token.</summary>
    /// <remarks>
    /// A stocktake is counted by several people at once by design. Concurrent recounts of
    /// the same line must not silently discard one another, because the adjustment that
    /// follows posts directly to the ledger and to cost of goods.
    /// </remarks>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>
    /// Records a counted quantity against the expected quantity at the time of entry.
    /// </summary>
    public Result RecordCount(Guid variantId, decimal countedQuantity, decimal expectedQuantity, Guid countedByUserId, DateTimeOffset now)
    {
        if (Status != StocktakeStatus.Counting)
            return Result.Failure(InventoryErrors.StocktakeNotCounting);

        if (countedQuantity < 0m)
            return Result.Failure(InventoryErrors.NegativeCount);

        var existing = _lines.SingleOrDefault(l => l.VariantId == variantId);

        if (existing is not null)
        {
            // Recounts are normal and expected; the last count wins, and the history of
            // recounts is preserved in the audit log rather than here.
            existing.Recount(countedQuantity, expectedQuantity, countedByUserId, now);
            return Result.Success();
        }

        _lines.Add(StocktakeLine.Create(Id, variantId, countedQuantity, expectedQuantity, countedByUserId, now));
        return Result.Success();
    }

    /// <summary>
    /// Closes counting. Variances above the threshold must be reviewed before posting.
    /// </summary>
    public Result CompleteCounting()
    {
        if (Status != StocktakeStatus.Counting)
            return Result.Failure(InventoryErrors.StocktakeNotCounting);

        if (_lines.Count == 0)
            return Result.Failure(InventoryErrors.StocktakeHasNoLines);

        Status = StocktakeStatus.PendingReview;
        return Result.Success();
    }

    /// <summary>
    /// Posts the stocktake, yielding the adjustment movements the ledger should record.
    /// </summary>
    /// <remarks>
    /// Returns the intended movements rather than writing them, so that the domain stays
    /// free of persistence (ADR 001) and the caller can apply them in one transaction
    /// alongside the status change.
    /// </remarks>
    public Result<IReadOnlyList<StocktakeAdjustment>> Post(Guid userId, DateTimeOffset now)
    {
        if (Status != StocktakeStatus.PendingReview)
            return Result<IReadOnlyList<StocktakeAdjustment>>.Failure(InventoryErrors.StocktakeNotPendingReview);

        var adjustments = _lines
            .Where(l => l.Variance != 0m)
            .Select(l => new StocktakeAdjustment(l.VariantId, l.Variance))
            .ToList();

        Status = StocktakeStatus.Posted;
        PostedByUserId = userId;
        PostedAt = now;

        return Result<IReadOnlyList<StocktakeAdjustment>>.Success(adjustments);
    }

    /// <summary>
    /// Abandons a count before it has posted anything to the ledger.
    /// </summary>
    /// <remarks>
    /// Reachable from <see cref="StocktakeStatus.Counting"/> or
    /// <see cref="StocktakeStatus.PendingReview"/> — both precede <see cref="Post"/>, the
    /// step that turns counted variance into ledger movements, so cancelling here is a
    /// pure status change with no stock effect to undo. Once <see cref="Post"/> has run,
    /// the adjustments are real movements against real cost; unwinding those is a
    /// correcting count, not a cancellation, so <see cref="StocktakeStatus.Posted"/> is
    /// refused the same way <c>PurchaseOrder.Cancel</c> refuses an order with receipts.
    /// </remarks>
    public Result Cancel(string reason, Guid userId, DateTimeOffset now)
    {
        if (Status is StocktakeStatus.Posted or StocktakeStatus.Cancelled)
            return Result.Failure(InventoryErrors.StocktakeCannotBeCancelled);

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(InventoryErrors.CancellationReasonRequired);

        Status = StocktakeStatus.Cancelled;
        CancelledByUserId = userId;
        CancellationReason = reason.Trim();
        CancelledAt = now;

        return Result.Success();
    }

    public Guid? CancelledByUserId { get; private set; }
    public string? CancellationReason { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
}

public sealed class StocktakeLine : Entity<Guid>
{
    private StocktakeLine() { }

    internal static StocktakeLine Create(
        Guid stocktakeId, Guid variantId, decimal counted, decimal expected, Guid userId, DateTimeOffset now) => new()
        {
            Id = SequentialId.New(),
            StocktakeId = stocktakeId,
            VariantId = variantId,
            CountedQuantity = counted,
            ExpectedQuantity = expected,
            CountedByUserId = userId,
            CountedAt = now
        };

    public Guid StocktakeId { get; private set; }
    public Guid VariantId { get; private set; }

    public decimal CountedQuantity { get; private set; }

    /// <summary>
    /// On-hand at the moment the count was entered, captured for the audit trail.
    /// </summary>
    /// <remarks>
    /// Stored rather than recomputed at posting time. Between counting and posting the
    /// balance may legitimately move (the store kept trading), and the variance the
    /// counter observed is the one that must be explainable later.
    /// </remarks>
    public decimal ExpectedQuantity { get; private set; }

    public decimal Variance => CountedQuantity - ExpectedQuantity;

    public Guid CountedByUserId { get; private set; }
    public DateTimeOffset CountedAt { get; private set; }

    public int RecountCount { get; private set; }

    internal void Recount(decimal counted, decimal expected, Guid userId, DateTimeOffset now)
    {
        CountedQuantity = counted;
        ExpectedQuantity = expected;
        CountedByUserId = userId;
        CountedAt = now;
        RecountCount++;
    }
}

public sealed record StocktakeAdjustment(Guid VariantId, decimal QuantityDelta);

public enum StocktakeScope
{
    /// <summary>Every variant in the warehouse.</summary>
    Full = 0,

    /// <summary>A rolling subset — a category, a shelf, a value band.</summary>
    Cycle = 1
}

public enum StocktakeStatus
{
    Counting = 0,
    PendingReview = 1,
    Posted = 2,
    Cancelled = 3
}
