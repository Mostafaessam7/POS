using POS.SharedKernel;

namespace POS.Purchasing.Domain;

/// <summary>
/// A commitment to buy: what we asked for, from whom, at what price, and who authorised it.
/// </summary>
/// <remarks>
/// The purchase order is the control document of this module. Its job is not to record
/// what arrived — that is the goods receipt — but to record what was <em>agreed</em>, so
/// that everything arriving afterwards can be checked against something that predates it.
/// Take that away and three-way matching has nothing to match on.
///
/// Quantities are tracked as ordered / received / cancelled, never as a received flag.
/// A real warehouse receives 47 of 50, then 5 more a week later: that is a partial
/// receipt followed by an over-receipt, and a boolean cannot express either.
/// </remarks>
public sealed class PurchaseOrder : AggregateRoot<Guid>, ITenantScoped, IBranchScoped, ICompanyScoped
{
    private readonly List<PurchaseOrderLine> _lines = [];
    private readonly List<PurchaseOrderApproval> _approvals = [];

    private PurchaseOrder() { }

    public static PurchaseOrder Raise(
        Guid tenantId,
        Guid companyId,
        Guid branchId,
        Guid warehouseId,
        Supplier supplier,
        string orderNumber,
        Guid raisedByUserId,
        DateTimeOffset raisedAt,
        BusinessDate businessDate)
    {
        ArgumentNullException.ThrowIfNull(supplier);
        ArgumentException.ThrowIfNullOrWhiteSpace(orderNumber);

        return new PurchaseOrder
        {
            Id = SequentialId.New(),
            TenantId = tenantId,
            CompanyId = companyId,
            BranchId = branchId,
            WarehouseId = warehouseId,
            SupplierId = supplier.Id,
            OrderNumber = orderNumber,
            Currency = supplier.Currency,
            // Snapshotted, not referenced. Renegotiating terms next quarter must not
            // restate what this order was placed under.
            AgreedTerms = supplier.Terms,
            ExpectedDeliveryDate = businessDate.Value.AddDays(supplier.Terms.LeadTimeDays),
            Status = PurchaseOrderStatus.Draft,
            RaisedByUserId = raisedByUserId,
            RaisedAt = raisedAt,
            BusinessDate = businessDate
        };
    }

    public Guid TenantId { get; private set; }
    public Guid CompanyId { get; private set; }
    public Guid BranchId { get; private set; }

    /// <summary>Where the goods are expected to land. Not necessarily the ordering branch's default.</summary>
    public Guid WarehouseId { get; private set; }

    public Guid SupplierId { get; private set; }

    public string OrderNumber { get; private set; } = string.Empty;

    public string Currency { get; private set; } = string.Empty;

    /// <summary>Terms as they stood when the order was raised.</summary>
    public SupplierTerms AgreedTerms { get; private set; } = null!;

    public DateOnly ExpectedDeliveryDate { get; private set; }

    public PurchaseOrderStatus Status { get; private set; }

    public Guid RaisedByUserId { get; private set; }
    public DateTimeOffset RaisedAt { get; private set; }
    public BusinessDate BusinessDate { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public string? CancellationReason { get; private set; }

    public IReadOnlyList<PurchaseOrderLine> Lines => _lines;
    public IReadOnlyList<PurchaseOrderApproval> Approvals => _approvals;

    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Net order value, excluding tax and before any landed cost.</summary>
    public Money TotalValue =>
        _lines.Count == 0
            ? Money.Zero(Currency)
            : _lines.Aggregate(Money.Zero(Currency), (sum, line) => sum + line.LineTotal);

    public bool IsEditable => Status is PurchaseOrderStatus.Draft or PurchaseOrderStatus.Rejected;

    /// <summary>True once every line has been received in full or explicitly cancelled.</summary>
    public bool IsFullyResolved => _lines.Count > 0 && _lines.All(l => l.OutstandingQuantity <= 0m);

    public Result AddLine(Guid variantId, decimal quantity, Money unitPrice, string? supplierCode = null, string? description = null)
    {
        if (!IsEditable)
        {
            return Result.Failure(PurchasingErrors.OrderNotEditable);
        }

        if (quantity <= 0m)
        {
            return Result.Failure(PurchasingErrors.OrderQuantityMustBePositive);
        }

        if (unitPrice.IsNegative)
        {
            return Result.Failure(PurchasingErrors.PriceCannotBeNegative);
        }

        if (unitPrice.Currency != Currency)
        {
            return Result.Failure(PurchasingErrors.CurrencyMismatch);
        }

        if (_lines.Any(l => l.VariantId == variantId))
        {
            // Merging silently would be friendlier and wrong: two lines for the same
            // variant usually means the buyer lost track, and the correct fix is to tell
            // them rather than to quietly double an order.
            return Result.Failure(PurchasingErrors.DuplicateOrderLine);
        }

        _lines.Add(PurchaseOrderLine.Create(
            Id,
            _lines.Count + 1,
            variantId,
            quantity,
            unitPrice,
            supplierCode,
            description));

        return Result.Success();
    }

    public Result RemoveLine(int lineNumber)
    {
        if (!IsEditable)
        {
            return Result.Failure(PurchasingErrors.OrderNotEditable);
        }

        var removed = _lines.RemoveAll(l => l.LineNumber == lineNumber);
        return removed == 0
            ? Result.Failure(PurchasingErrors.OrderLineNotFound)
            : Result.Success();
    }

    /// <summary>
    /// Submits the order for approval, or approves it outright when policy does not require one.
    /// </summary>
    public Result Submit(ApprovalPolicy policy, DateTimeOffset submittedAt)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (!IsEditable)
        {
            return Result.Failure(PurchasingErrors.OrderNotEditable);
        }

        if (_lines.Count == 0)
        {
            return Result.Failure(PurchasingErrors.OrderHasNoLines);
        }

        if (!policy.RequiresApproval(TotalValue))
        {
            Status = PurchaseOrderStatus.Approved;
            return Result.Success();
        }

        Status = PurchaseOrderStatus.PendingApproval;
        return Result.Success();
    }

    /// <summary>
    /// Records an approval decision.
    /// </summary>
    /// <remarks>
    /// The separation-of-duties check lives here, in the aggregate, rather than in a
    /// handler or a policy service. A buyer approving their own order is the single most
    /// common purchasing fraud, and the rule protecting against it must be impossible to
    /// route around — an aggregate that can reach an approved state without passing
    /// through this method would make the control advisory.
    ///
    /// The policy supplies the <em>threshold</em>; the aggregate enforces the
    /// <em>invariant</em>. That split matters: thresholds are a tenant's commercial
    /// choice and change often, self-approval is a control and does not.
    /// </remarks>
    public Result Approve(ApprovalPolicy policy, Guid approverUserId, ApprovalLevel approverLevel, DateTimeOffset approvedAt)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (Status != PurchaseOrderStatus.PendingApproval)
        {
            return Result.Failure(PurchasingErrors.OrderNotAwaitingApproval);
        }

        if (approverUserId == RaisedByUserId && !policy.AllowSelfApproval)
        {
            return Result.Failure(PurchasingErrors.SelfApprovalForbidden);
        }

        var required = policy.RequiredLevel(TotalValue);
        if (approverLevel < required)
        {
            return Result.Failure(PurchasingErrors.ApprovalLevelInsufficient);
        }

        if (_approvals.Any(a => a.ApproverUserId == approverUserId && a.Approved))
        {
            return Result.Failure(PurchasingErrors.DuplicateApproval);
        }

        _approvals.Add(new PurchaseOrderApproval(approverUserId, approverLevel, true, approvedAt, null));

        Status = PurchaseOrderStatus.Approved;
        return Result.Success();
    }

    public Result Reject(Guid approverUserId, ApprovalLevel approverLevel, string reason, DateTimeOffset rejectedAt)
    {
        if (Status != PurchaseOrderStatus.PendingApproval)
        {
            return Result.Failure(PurchasingErrors.OrderNotAwaitingApproval);
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(PurchasingErrors.RejectionReasonRequired);
        }

        _approvals.Add(new PurchaseOrderApproval(approverUserId, approverLevel, false, rejectedAt, reason.Trim()));

        // Back to editable rather than terminal: rejection almost always means "fix the
        // quantity and resubmit", and forcing a fresh order would lose the discussion.
        Status = PurchaseOrderStatus.Rejected;
        return Result.Success();
    }

    public Result Send(DateTimeOffset sentAt)
    {
        if (Status != PurchaseOrderStatus.Approved)
        {
            return Result.Failure(PurchasingErrors.OrderNotApproved);
        }

        Status = PurchaseOrderStatus.Sent;
        SentAt = sentAt;
        return Result.Success();
    }

    /// <summary>
    /// Applies a received quantity to a line and advances the order's status.
    /// </summary>
    /// <remarks>
    /// Called by <see cref="GoodsReceipt"/> during posting, not by a handler. The order
    /// owns the ordered/received arithmetic because the over-receipt tolerance is a
    /// property of what was agreed, and the receipt cannot know it without asking.
    /// </remarks>
    internal Result ApplyReceipt(int lineNumber, decimal quantity, ReceiptTolerance tolerance)
    {
        if (Status is not (PurchaseOrderStatus.Sent or PurchaseOrderStatus.PartiallyReceived))
        {
            return Result.Failure(PurchasingErrors.OrderNotReceivable);
        }

        var line = _lines.FirstOrDefault(l => l.LineNumber == lineNumber);
        if (line is null)
        {
            return Result.Failure(PurchasingErrors.OrderLineNotFound);
        }

        var result = line.RecordReceipt(quantity, tolerance);
        if (result.IsFailure)
        {
            return result;
        }

        Status = IsFullyResolved ? PurchaseOrderStatus.Received : PurchaseOrderStatus.PartiallyReceived;
        return Result.Success();
    }

    /// <summary>
    /// Cancels the outstanding balance of a line — the supplier is not going to send the rest.
    /// </summary>
    /// <remarks>
    /// Short-shipment closure is a distinct act from receiving, and conflating them is a
    /// common and costly modelling error: if "received 47 of 50" silently closes the line,
    /// the three missing units disappear from the outstanding-orders report and nobody
    /// ever chases the supplier for the credit.
    /// </remarks>
    public Result CancelOutstanding(int lineNumber, string reason)
    {
        if (Status is PurchaseOrderStatus.Draft or PurchaseOrderStatus.Cancelled or PurchaseOrderStatus.Closed)
        {
            return Result.Failure(PurchasingErrors.OrderNotReceivable);
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(PurchasingErrors.CancellationReasonRequired);
        }

        var line = _lines.FirstOrDefault(l => l.LineNumber == lineNumber);
        if (line is null)
        {
            return Result.Failure(PurchasingErrors.OrderLineNotFound);
        }

        line.CancelOutstanding(reason.Trim());

        if (IsFullyResolved)
        {
            Status = PurchaseOrderStatus.Received;
        }

        return Result.Success();
    }

    public Result Cancel(string reason, DateTimeOffset cancelledAt)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(PurchasingErrors.CancellationReasonRequired);
        }

        if (_lines.Any(l => l.QuantityReceived > 0m))
        {
            // Something has already arrived, so the order is now part of the stock and
            // invoice trail. Cancelling it wholesale would strand those movements against
            // a document that claims nothing was ever ordered.
            return Result.Failure(PurchasingErrors.CannotCancelPartiallyReceivedOrder);
        }

        Status = PurchaseOrderStatus.Cancelled;
        CancellationReason = reason.Trim();
        ClosedAt = cancelledAt;
        return Result.Success();
    }

    /// <summary>Closes a fully resolved order, ending its life in the outstanding-orders report.</summary>
    public Result Close(DateTimeOffset closedAt)
    {
        if (Status != PurchaseOrderStatus.Received)
        {
            return Result.Failure(PurchasingErrors.OrderNotFullyReceived);
        }

        Status = PurchaseOrderStatus.Closed;
        ClosedAt = closedAt;
        return Result.Success();
    }
}

/// <summary>
/// One requested variant, and the running account of what has actually turned up.
/// </summary>
public sealed class PurchaseOrderLine : Entity<Guid>
{
    private PurchaseOrderLine() { }

    internal static PurchaseOrderLine Create(
        Guid orderId,
        int lineNumber,
        Guid variantId,
        decimal quantity,
        Money unitPrice,
        string? supplierCode,
        string? description) => new()
        {
            Id = SequentialId.New(),
            PurchaseOrderId = orderId,
            LineNumber = lineNumber,
            VariantId = variantId,
            QuantityOrdered = quantity,
            UnitPrice = unitPrice,
            SupplierCode = supplierCode,
            Description = description
        };

    public Guid PurchaseOrderId { get; private set; }
    public int LineNumber { get; private set; }
    public Guid VariantId { get; private set; }

    public decimal QuantityOrdered { get; private set; }
    public decimal QuantityReceived { get; private set; }
    public decimal QuantityCancelled { get; private set; }

    /// <summary>What we are still waiting for. Never negative — an over-receipt does not create a debt.</summary>
    public decimal OutstandingQuantity =>
        Math.Max(0m, QuantityOrdered - QuantityReceived - QuantityCancelled);

    /// <summary>Quantity received beyond what was ordered. Reported, not hidden.</summary>
    public decimal OverReceivedQuantity =>
        Math.Max(0m, QuantityReceived - QuantityOrdered);

    public Money UnitPrice { get; private set; }

    public Money LineTotal => UnitPrice * QuantityOrdered;

    public string? SupplierCode { get; private set; }
    public string? Description { get; private set; }
    public string? CancellationReason { get; private set; }

    internal Result RecordReceipt(decimal quantity, ReceiptTolerance tolerance)
    {
        ArgumentNullException.ThrowIfNull(tolerance);

        if (quantity <= 0m)
        {
            return Result.Failure(PurchasingErrors.ReceiptQuantityMustBePositive);
        }

        var projected = QuantityReceived + quantity;

        if (projected > QuantityOrdered && !tolerance.PermitsOverReceipt(QuantityOrdered, projected))
        {
            return Result.Failure(PurchasingErrors.OverReceiptExceedsTolerance);
        }

        QuantityReceived = projected;
        return Result.Success();
    }

    internal void CancelOutstanding(string reason)
    {
        QuantityCancelled += OutstandingQuantity;
        CancellationReason = reason;
    }
}

public sealed record PurchaseOrderApproval(
    Guid ApproverUserId,
    ApprovalLevel Level,
    bool Approved,
    DateTimeOffset DecidedAt,
    string? Reason);

public enum PurchaseOrderStatus
{
    Draft = 1,
    PendingApproval = 2,
    Approved = 3,
    Rejected = 4,
    Sent = 5,
    PartiallyReceived = 6,
    Received = 7,
    Closed = 8,
    Cancelled = 9
}

/// <summary>
/// Approval seniority. Ordered, so that a higher level satisfies a lower requirement.
/// </summary>
public enum ApprovalLevel
{
    None = 0,
    Supervisor = 1,
    Manager = 2,
    Director = 3
}

/// <summary>
/// Tenant-configured thresholds deciding who may approve what.
/// </summary>
/// <remarks>
/// Configuration data, not code. A tenant with two shops and a tenant with two hundred
/// have genuinely different answers, and both are right; hard-coding either produces a
/// branch on tenant identity somewhere, which is the thing this project has avoided
/// everywhere else.
///
/// Thresholds are held as an ordered list of (from-value, level) rather than three named
/// limits so that a tenant needing a fourth band adds a row instead of waiting for a
/// release.
/// </remarks>
public sealed class ApprovalPolicy
{
    private readonly List<ApprovalThreshold> _thresholds;

    public ApprovalPolicy(Money approvalRequiredAbove, IEnumerable<ApprovalThreshold> thresholds, bool allowSelfApproval = false)
    {
        ArgumentNullException.ThrowIfNull(thresholds);

        ApprovalRequiredAbove = approvalRequiredAbove;
        AllowSelfApproval = allowSelfApproval;
        _thresholds = thresholds.OrderBy(t => t.FromValue.Amount).ToList();
    }

    /// <summary>Orders at or below this value are approved on submission. Zero means everything needs approval.</summary>
    public Money ApprovalRequiredAbove { get; }

    /// <summary>
    /// Whether the raiser may approve their own order.
    /// </summary>
    /// <remarks>
    /// Defaults to false and should stay false in any tenant with more than one member of
    /// staff. It is configurable at all only because a single-owner shop where the owner
    /// is the only user would otherwise be unable to raise an order — a control that makes
    /// the system unusable gets switched off wholesale, which is worse than one that bends.
    /// </remarks>
    public bool AllowSelfApproval { get; }

    public IReadOnlyList<ApprovalThreshold> Thresholds => _thresholds;

    public bool RequiresApproval(Money orderValue) => orderValue > ApprovalRequiredAbove;

    /// <summary>The minimum seniority that may approve an order of this value.</summary>
    public ApprovalLevel RequiredLevel(Money orderValue)
    {
        var level = ApprovalLevel.Supervisor;

        foreach (var threshold in _thresholds)
        {
            if (orderValue >= threshold.FromValue)
            {
                level = threshold.Level;
            }
        }

        return level;
    }

    /// <summary>A policy where nothing needs approval — useful for tests and single-user tenants.</summary>
    public static ApprovalPolicy None(string currency) =>
        new(new Money(decimal.MaxValue, currency), [], allowSelfApproval: true);
}

public sealed record ApprovalThreshold(Money FromValue, ApprovalLevel Level);

/// <summary>
/// How much more than ordered we are willing to accept.
/// </summary>
/// <remarks>
/// Over-receipt tolerance is separate from invoice-matching tolerance on purpose. They
/// answer different questions — "will I put these on my shelf" versus "will I pay for
/// them" — and a business quite reasonably says yes to the first and no to the second.
/// </remarks>
public sealed class ReceiptTolerance
{
    public ReceiptTolerance(decimal percentage, decimal absoluteUnits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(percentage);
        ArgumentOutOfRangeException.ThrowIfNegative(absoluteUnits);

        Percentage = percentage;
        AbsoluteUnits = absoluteUnits;
    }

    public decimal Percentage { get; }
    public decimal AbsoluteUnits { get; }

    /// <summary>Accepts an over-receipt if it is within <em>either</em> bound.</summary>
    /// <remarks>
    /// Either, not both, because the two guard different scales. A 2% tolerance on an
    /// order of 3 units permits nothing at all; an absolute tolerance of 5 units on an
    /// order of 10,000 is meaninglessly tight. Whichever is more generous is the one the
    /// buyer actually meant.
    /// </remarks>
    public bool PermitsOverReceipt(decimal ordered, decimal receivedTotal)
    {
        if (receivedTotal <= ordered)
        {
            return true;
        }

        var excess = receivedTotal - ordered;

        if (excess <= AbsoluteUnits)
        {
            return true;
        }

        return ordered > 0m && excess / ordered * 100m <= Percentage;
    }

    /// <summary>Nothing over the ordered quantity is accepted.</summary>
    public static ReceiptTolerance Strict { get; } = new(0m, 0m);

    /// <summary>A pragmatic default: 5% or two units, whichever is kinder.</summary>
    public static ReceiptTolerance Default { get; } = new(5m, 2m);
}
