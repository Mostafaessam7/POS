using POS.Sales.Domain;

namespace POS.Sales.Sync;

/// <summary>
/// What a terminal uploads for one completed sale.
/// </summary>
/// <remarks>
/// <para>
/// THIS CONTRACT IS AN ASSUMPTION, not a specification handed down from anywhere. No
/// prior artefact in this repository defines the sale upload payload; it was inferred
/// from what <see cref="Sale"/> requires in order to be reconstituted faithfully. If a
/// terminal implementation exists elsewhere, reconcile against it before shipping —
/// and treat a mismatch as this file being wrong.
/// </para>
/// <para>
/// THE TERMINAL IS THE AUTHOR OF THESE NUMBERS. It priced the basket offline, took the
/// money, and printed a receipt; the customer has already left. The server's job on
/// ingest is to RECORD what happened, not to re-derive it — re-pricing here against
/// today's price lists would silently restate a sale that was legitimately made at
/// last week's prices, which is the exact failure ADR 023's price versioning exists to
/// prevent. That is why line amounts are carried explicitly rather than recomputed.
/// </para>
/// <para>
/// The payload is validated on the way in by the aggregate itself: the handler replays
/// the terminal's steps through <see cref="Sale"/>'s own state machine, so a batch
/// that violates a domain invariant is rejected as a bad record rather than persisted
/// as a bad sale.
/// </para>
/// </remarks>
public sealed record SaleSyncPayload
{
    /// <summary>
    /// The identity the TERMINAL assigned when it opened the sale (ADR 005).
    /// </summary>
    /// <remarks>
    /// Not generated server-side. It is the stable key that lets a replayed upload be
    /// recognised as the same sale, and it is the document id the stock movement is
    /// attributed to — so the answer to "why did this sale not reduce stock?" is a
    /// single indexed lookup.
    /// </remarks>
    public required Guid SaleId { get; init; }

    public required Guid CompanyId { get; init; }
    public required Guid BranchId { get; init; }
    public required Guid TerminalId { get; init; }

    /// <summary>
    /// The stock location this terminal sells from.
    /// </summary>
    /// <remarks>
    /// Carried on the payload rather than resolved from the branch. A branch has
    /// several warehouses — sales floor, stock room, quarantine — and which one a till
    /// depletes is terminal configuration, not something derivable from the branch.
    /// Resolving it server-side would also mean Sales reaching into Identity, which the
    /// module boundary forbids.
    /// </remarks>
    public required Guid WarehouseId { get; init; }
    public required Guid ShiftId { get; init; }
    public required Guid CashierId { get; init; }

    public required string Currency { get; init; }

    /// <summary>
    /// The receipt printed for the customer. Its uniqueness per terminal is what makes
    /// ingest idempotent — see <see cref="SaleSyncHandler"/>.
    /// </summary>
    public required string ReceiptSeries { get; init; }

    public required long ReceiptSequence { get; init; }

    public required DateOnly BusinessDate { get; init; }
    public required DateTimeOffset OpenedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }

    public required IReadOnlyList<SaleSyncLine> Lines { get; init; }

    public required IReadOnlyList<SaleSyncTender> Tenders { get; init; }

    /// <summary>Order-level rounding the terminal applied. Zero where cash rounding is not in force.</summary>
    public decimal RoundingAdjustment { get; init; }
}

/// <summary>One line as the terminal priced and printed it.</summary>
public sealed record SaleSyncLine
{
    public required int LineNumber { get; init; }
    public required Guid VariantId { get; init; }
    public required string Description { get; init; }
    public required decimal Quantity { get; init; }
    public required string UnitOfMeasure { get; init; }
    public required decimal UnitPrice { get; init; }
    public required string TaxCode { get; init; }
    public required decimal TaxRate { get; init; }
    public bool TaxInclusivePricing { get; init; }

    /// <summary>Cost snapshotted at the moment of sale, for margin reporting (ADR 020).</summary>
    public decimal UnitCostAtSale { get; init; }

    public Guid? PriceListId { get; init; }
    public int? PriceListVersion { get; init; }

    public decimal Discount { get; init; }
    public required decimal Net { get; init; }
    public decimal Tax { get; init; }
    public required decimal Gross { get; init; }
}

public sealed record SaleSyncTender
{
    public required TenderMethod Method { get; init; }
    public required decimal Amount { get; init; }
    public required DateTimeOffset TakenAt { get; init; }
    public string? Reference { get; init; }
    public Guid? PaymentId { get; init; }
}
