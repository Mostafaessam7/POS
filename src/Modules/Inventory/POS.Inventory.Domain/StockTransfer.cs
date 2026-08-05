using POS.SharedKernel;

namespace POS.Inventory.Domain;

/// <summary>
/// Movement of stock between warehouses, modelled as two legs with an in-transit period.
/// </summary>
/// <remarks>
/// <para>
/// A transfer is not instantaneous. Stock leaves Branch A on Monday and arrives at
/// Branch B on Thursday. Modelling it as one movement forces a false choice: either A
/// is short for three days while B's staff see stock they cannot find, or the stock
/// exists in both places at once. Both break the conservation invariant that total
/// stock across all warehouses is constant across a transfer.
/// </para>
/// <para>
/// So: dispatch moves stock from the source into an <c>InTransit</c> warehouse, and
/// receipt moves it from in-transit to the destination. At every instant the stock is
/// in exactly one place. See ADR 028.
/// </para>
/// </remarks>
public sealed class StockTransfer : AggregateRoot<Guid>, ITenantScoped
{
    private readonly List<StockTransferLine> _lines = [];

    private StockTransfer() { }

    public static StockTransfer Draft(
        Guid tenantId,
        Guid sourceWarehouseId,
        Guid destinationWarehouseId,
        Guid inTransitWarehouseId,
        string transferNumber,
        Guid createdByUserId,
        DateTimeOffset now)
    {
        if (sourceWarehouseId == destinationWarehouseId)
            throw new ArgumentException("A transfer must move stock between different warehouses.");

        return new StockTransfer
        {
            Id = SequentialId.New(),
            TenantId = tenantId,
            SourceWarehouseId = sourceWarehouseId,
            DestinationWarehouseId = destinationWarehouseId,
            InTransitWarehouseId = inTransitWarehouseId,
            TransferNumber = transferNumber,
            Status = TransferStatus.Draft,
            CreatedByUserId = createdByUserId,
            CreatedAt = now
        };
    }

    public Guid TenantId { get; private set; }
    public Guid SourceWarehouseId { get; private set; }
    public Guid DestinationWarehouseId { get; private set; }
    public Guid InTransitWarehouseId { get; private set; }

    public string TransferNumber { get; private set; } = null!;
    public TransferStatus Status { get; private set; }

    public Guid CreatedByUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? DispatchedByUserId { get; private set; }
    public DateTimeOffset? DispatchedAt { get; private set; }

    public Guid? ReceivedByUserId { get; private set; }
    public DateTimeOffset? ReceivedAt { get; private set; }

    public IReadOnlyList<StockTransferLine> Lines => _lines.AsReadOnly();

    /// <summary>Optimistic concurrency token.</summary>
    /// <remarks>
    /// Two staff receiving the same delivery on two terminals is ordinary warehouse
    /// behaviour, not an edge case. Without a token the second receipt overwrites the
    /// first, stock is credited once instead of twice or vice versa, and the variance
    /// write-off hides it.
    /// </remarks>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>True where anything sent did not arrive. Blocks completion until resolved.</summary>
    public bool HasVariance => _lines.Any(l => l.HasVariance);

    public void AddLine(Guid variantId, decimal quantity)
    {
        if (Status != TransferStatus.Draft)
            throw new InvalidOperationException("Lines can only be added to a draft transfer.");

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(quantity, 0m);

        var existing = _lines.SingleOrDefault(l => l.VariantId == variantId);
        if (existing is not null)
        {
            existing.IncreaseQuantitySent(quantity);
            return;
        }

        _lines.Add(StockTransferLine.Create(Id, variantId, quantity));
    }

    public Result Dispatch(Guid userId, DateTimeOffset now)
    {
        if (Status != TransferStatus.Draft)
            return Result.Failure(InventoryErrors.TransferNotDraft);

        if (_lines.Count == 0)
            return Result.Failure(InventoryErrors.TransferHasNoLines);

        Status = TransferStatus.InTransit;
        DispatchedByUserId = userId;
        DispatchedAt = now;

        return Result.Success();
    }

    /// <summary>
    /// Records what actually arrived. Shortfalls are retained as variance in the
    /// in-transit location rather than quietly written off.
    /// </summary>
    /// <remarks>
    /// Transfer shrinkage is a well-known theft vector. Making the shortfall persist
    /// until someone with the write-off permission explicitly disposes of it is the
    /// entire control: the stock cannot vanish without a named person deciding it did.
    /// </remarks>
    public Result Receive(
        IReadOnlyDictionary<Guid, decimal> receivedQuantities,
        Guid userId,
        DateTimeOffset now)
    {
        if (Status != TransferStatus.InTransit)
            return Result.Failure(InventoryErrors.TransferNotInTransit);

        foreach (var line in _lines)
        {
            var received = receivedQuantities.TryGetValue(line.VariantId, out var q) ? q : 0m;
            line.RecordReceipt(received);
        }

        ReceivedByUserId = userId;
        ReceivedAt = now;
        Status = HasVariance ? TransferStatus.ReceivedWithVariance : TransferStatus.Completed;

        return Result.Success();
    }

    /// <summary>
    /// Explicitly disposes of an unresolved shortfall, subject to the value-based
    /// approval ladder and the separation-of-duties rule below.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>PurchaseOrder.Approve</c>'s split: the policy supplies the
    /// <em>threshold</em> and the <em>self-approval rule</em>; the aggregate enforces
    /// both as invariants, because a control that could be routed around by calling a
    /// different method would be advisory, not a control. The self-approval check is
    /// against <see cref="ReceivedByUserId"/>, not the creator — the person who counted
    /// the shortfall is the one who must not also be the one who decides, unchecked,
    /// that it is written off rather than investigated.
    /// </remarks>
    public Result WriteOffVariance(
        VarianceApprovalPolicy policy,
        Money varianceValue,
        Guid userId,
        ApprovalLevel approverLevel,
        string reasonCode,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (Status != TransferStatus.ReceivedWithVariance)
            return Result.Failure(InventoryErrors.TransferHasNoVariance);

        if (string.IsNullOrWhiteSpace(reasonCode))
            return Result.Failure(InventoryErrors.ReasonCodeRequired);

        if (userId == ReceivedByUserId && !policy.AllowSelfApproval)
            return Result.Failure(InventoryErrors.SelfApprovalForbidden);

        var required = policy.RequiredLevel(varianceValue);
        if (approverLevel < required)
            return Result.Failure(InventoryErrors.ApprovalLevelInsufficient);

        VarianceWrittenOffByUserId = userId;
        VarianceWriteOffReason = reasonCode;
        VarianceWrittenOffAt = now;
        Status = TransferStatus.Completed;

        return Result.Success();
    }

    public Guid? VarianceWrittenOffByUserId { get; private set; }
    public string? VarianceWriteOffReason { get; private set; }
    public DateTimeOffset? VarianceWrittenOffAt { get; private set; }

    /// <summary>
    /// Abandons a transfer that never left the source warehouse.
    /// </summary>
    /// <remarks>
    /// Only reachable from <see cref="TransferStatus.Draft"/>, the same restriction
    /// <c>PurchaseOrder.Cancel</c> applies once anything has been received: once
    /// <see cref="Dispatch"/> runs, stock has actually left the source warehouse (the
    /// TransferOut/TransferIn pair is posted to the ledger before the status advances),
    /// so cancelling from <see cref="TransferStatus.InTransit"/> onward would need to
    /// reverse a real stock movement, not just flip a status — out of scope for this
    /// method. A dispatched transfer that goes wrong is resolved through
    /// <see cref="Receive"/> and <see cref="WriteOffVariance"/> instead.
    /// </remarks>
    public Result Cancel(string reason, Guid userId, DateTimeOffset now)
    {
        if (Status != TransferStatus.Draft)
            return Result.Failure(InventoryErrors.TransferNotDraft);

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(InventoryErrors.CancellationReasonRequired);

        Status = TransferStatus.Cancelled;
        CancelledByUserId = userId;
        CancellationReason = reason.Trim();
        CancelledAt = now;

        return Result.Success();
    }

    public Guid? CancelledByUserId { get; private set; }
    public string? CancellationReason { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
}

public sealed class StockTransferLine : Entity<Guid>
{
    private StockTransferLine() { }

    internal static StockTransferLine Create(Guid transferId, Guid variantId, decimal quantitySent) => new()
    {
        Id = SequentialId.New(),
        StockTransferId = transferId,
        VariantId = variantId,
        QuantitySent = quantitySent
    };

    public Guid StockTransferId { get; private set; }
    public Guid VariantId { get; private set; }

    public decimal QuantitySent { get; private set; }
    public decimal? QuantityReceived { get; private set; }

    public decimal Variance => (QuantityReceived ?? QuantitySent) - QuantitySent;
    public bool HasVariance => QuantityReceived.HasValue && QuantityReceived.Value != QuantitySent;

    internal void IncreaseQuantitySent(decimal additional) => QuantitySent += additional;

    internal void RecordReceipt(decimal quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quantity);

        QuantityReceived = quantity;
    }
}

public enum TransferStatus
{
    Draft = 0,
    InTransit = 1,
    ReceivedWithVariance = 2,
    Completed = 3,
    Cancelled = 4
}
