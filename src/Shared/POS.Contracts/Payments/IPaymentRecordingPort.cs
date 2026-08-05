using POS.SharedKernel;

namespace POS.Contracts.Payments;

/// <summary>
/// The seam through which a completed sale's electronic tenders are recorded as payments.
/// </summary>
/// <remarks>
/// <para>
/// This is RECORDING, not taking. A synced sale was rung up on a terminal that was
/// offline at the time, so the card was already authorised and captured at the pin pad
/// before the server ever heard of it (ADR 042: write-ahead recording, "authorised
/// offline"). The server's job is to write down what happened, not to call an acquirer —
/// there is nothing left to authorise.
/// </para>
/// <para>
/// CASH IS NOT A PAYMENT HERE. The Payments module owns the electronic-payment lifecycle:
/// authorise, capture, settle, refund, and the indeterminate state that a dropped
/// network response leaves behind. Cash has none of those; it is reconciled against the
/// drawer by the shift, which is a different control entirely. Sales therefore sends only
/// its electronic tenders through this port.
/// </para>
/// <para>
/// Sales does not reference the Payments module; the two meet here (ADR 002). The request
/// speaks of tenders and money, never of a <c>Payment</c> aggregate or a provider.
/// </para>
/// </remarks>
public interface IPaymentRecordingPort
{
    /// <summary>
    /// Records each electronic tender of a sale as a captured payment, once.
    /// </summary>
    /// <remarks>
    /// IDEMPOTENT PER TENDER. A replayed sale upload records each tender exactly once —
    /// the derived idempotency key is stable across replays, and the unique index behind
    /// it is the guarantee. Recording a card payment twice would double the money the
    /// settlement reconciliation expects to see arrive, so this has to converge rather
    /// than accumulate.
    /// </remarks>
    public Task<Result> RecordSaleTendersAsync(
        RecordSaleTendersRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>A completed sale's electronic tenders, in the terms Payments needs.</summary>
public sealed record RecordSaleTendersRequest
{
    public required Guid BranchId { get; init; }
    public required Guid TerminalId { get; init; }

    /// <summary>The sale these tenders settled. Part of every tender's idempotency key.</summary>
    public required Guid SaleId { get; init; }

    public required DateOnly BusinessDate { get; init; }

    public required IReadOnlyList<RecordedTender> Tenders { get; init; }
}

/// <summary>One electronic tender, as the terminal took it.</summary>
/// <remarks>
/// <c>Sequence</c> is the tender's position within the sale, 0-based, and is what makes
/// the idempotency key stable: a replay rebuilds the tender list in the same order, so
/// tender <c>n</c> keeps its identity across uploads without the payload needing to carry
/// a payment id.
///
/// <c>Method</c> is the tender method name — "Card", "GiftCard", and so on — as a string
/// rather than a shared enum, because the method is a Sales concept that Payments only
/// records and never branches on.
/// </remarks>
public sealed record RecordedTender(
    int Sequence,
    string Method,
    Money Amount,
    DateTimeOffset TakenAt,
    string? Reference);
