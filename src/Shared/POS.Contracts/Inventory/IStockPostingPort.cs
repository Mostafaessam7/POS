using POS.SharedKernel;

namespace POS.Contracts.Inventory;

/// <summary>
/// The seam through which any module moves stock without depending on Inventory.
/// </summary>
/// <remarks>
/// <para>
/// Purchasing must post stock when a goods receipt is posted, and must not reference
/// the Inventory module to do it — architecture rule 2 forbids module-to-module
/// references and the architecture suite fails the build on one. ADR 002 names the two
/// permitted routes: POS.Contracts, or an integration event. This is the first.
/// </para>
/// <para>
/// The contract is deliberately expressed in PRIMITIVES AND SHARED KERNEL TYPES only.
/// No <c>StockMovement</c>, no <c>MovementType</c>, no <c>NegativeStockPolicy</c>. A
/// contract that leaked Inventory's own types would be a module reference wearing a
/// different hat: Purchasing would break every time Inventory renamed an enum member,
/// which is exactly the coupling the rule exists to prevent. The adapter on the
/// Inventory side is the anti-corruption layer that translates.
/// </para>
/// <para>
/// WHY A PORT AND NOT AN INTEGRATION EVENT. An event would decouple further, and is the
/// right answer once the outbox has a dispatcher. It does not have one yet, and posting
/// stock is not a fire-and-forget concern: the caller needs to know whether the
/// movement was accepted, because a receipt that cannot move stock must not be marked
/// posted. A synchronous port gives that answer; an event would require inventing a
/// compensation path for a failure the caller could have simply been told about.
/// </para>
/// </remarks>
public interface IStockPostingPort
{
    /// <summary>
    /// Applies a document's stock effect, once.
    /// </summary>
    /// <remarks>
    /// IDEMPOTENT BY DOCUMENT. Calling twice for the same
    /// <see cref="StockPostingRequest.DocumentId"/> applies the movements once and
    /// reports success both times. That is load-bearing rather than defensive: the
    /// caller commits its own record in a separate transaction, so a crash between the
    /// two leaves a document whose stock has moved but which is not yet marked posted.
    /// The retry must converge rather than double-count, and stock is append-only so a
    /// duplicate can never be tidied away afterwards.
    /// </remarks>
    public Task<Result> PostAsync(StockPostingRequest request, CancellationToken cancellationToken = default);
}

/// <summary>What a document does to stock.</summary>
public enum StockPostingKind
{
    /// <summary>Goods arriving from a supplier. Sets cost, and moves the weighted average.</summary>
    PurchaseReceipt = 1,

    /// <summary>Goods going back to a supplier. Consumes at the prevailing average.</summary>
    SupplierReturn = 2,

    /// <summary>
    /// Sold to a customer. Consumes at the prevailing average and never blocks.
    /// </summary>
    /// <remarks>
    /// The only kind that may drive stock negative without complaint. By the time a
    /// sale reaches this port the customer has paid and left, frequently having been
    /// rung up on a terminal that was offline at the time. Refusing the movement
    /// because the system's idea of stock disagrees with the shelf would leave the
    /// ledger further from the truth, not closer to it — the goods are gone either way,
    /// and a negative balance is an exception report rather than an error (ADR 027).
    /// </remarks>
    Sale = 3,

    /// <summary>
    /// A customer's purchase, given back and placed into sellable stock again.
    /// </summary>
    /// <remarks>
    /// The inbound counterpart to <see cref="Sale"/>. The domain already anticipated
    /// this — <c>MovementType.CustomerReturn</c> and <c>StockDocumentType.Return</c>
    /// have existed since Phase 4 — but nothing posted through this port used them
    /// until a void/refund flow existed to call it.
    /// </remarks>
    CustomerReturn = 4
}

/// <summary>One line of a document's stock effect.</summary>
/// <param name="VariantId">The variant being moved.</param>
/// <param name="Quantity">
/// Always POSITIVE. Direction is carried by <see cref="StockPostingRequest.Kind"/>, not
/// by the sign: a caller that has to remember which way round the minus goes will get
/// it wrong eventually, and an inbound movement recorded as outbound is a stock error
/// nobody detects until a count.
/// </param>
/// <param name="UnitCost">
/// For a receipt this is the LANDED unit cost — the supplier price plus its share of
/// freight and duty — not the invoice price. Costing the goods at the invoice price is
/// how margin quietly overstates itself (ADR 052).
/// </param>
public sealed record StockPostingLine(Guid VariantId, decimal Quantity, Money UnitCost);

/// <summary>A document's complete stock effect.</summary>
public sealed record StockPostingRequest
{
    public required Guid WarehouseId { get; init; }

    public required StockPostingKind Kind { get; init; }

    /// <summary>The originating document. Also the idempotency key.</summary>
    public required Guid DocumentId { get; init; }

    /// <summary>Human-facing document number, carried onto the ledger for support queries.</summary>
    public required string DocumentNumber { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required DateOnly BusinessDate { get; init; }

    public Guid? UserId { get; init; }

    public required IReadOnlyList<StockPostingLine> Lines { get; init; }
}
