using POS.Payments.Domain;
using POS.SharedKernel;

namespace POS.Payments.Reconciliation;

/// <summary>
/// Matches an acquirer's settlement file against our own payment records.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately pure: it takes two lists and returns a report, touching no database and
/// no clock. Reconciliation logic is where money is found and lost, so it must be
/// exhaustively testable without a fixture, and reproducible against a historic file
/// when someone disputes last quarter's numbers.
/// </para>
/// <para>
/// <b>The discrepancy classes are not symmetric</b>, and treating them as one "mismatch"
/// bucket is the mistake this design exists to avoid. They differ in who is harmed, how
/// urgent they are, and who must act.
/// </para>
/// </remarks>
public static class SettlementReconciler
{
    /// <summary>
    /// Reconciles a settlement batch against local payments.
    /// </summary>
    public static ReconciliationReport Reconcile(
        IReadOnlyCollection<SettlementRecord> settlement,
        IReadOnlyCollection<Payment> localPayments)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        ArgumentNullException.ThrowIfNull(localPayments);

        var byReference = new Dictionary<string, Payment>(StringComparer.OrdinalIgnoreCase);

        foreach (var payment in localPayments)
        {
            if (!string.IsNullOrWhiteSpace(payment.ProviderReference))
            {
                byReference[payment.ProviderReference] = payment;
            }
        }

        var matched = new List<ReconciledPair>();
        var mismatched = new List<ReconciledPair>();
        var settledButNotRecorded = new List<SettlementRecord>();
        var seen = new HashSet<Guid>();

        foreach (var record in settlement)
        {
            if (!byReference.TryGetValue(record.ProviderReference, out var payment))
            {
                // The acquirer moved money we have no record of. Highest severity:
                // the customer has been charged for something our system cannot
                // explain, and only the acquirer's file proves it happened.
                settledButNotRecorded.Add(record);
                continue;
            }

            seen.Add(payment.Id);

            var pair = new ReconciledPair(payment, record);

            if (record.Amount.Currency != payment.CapturedAmount.Currency
                || record.Amount.Amount != payment.CapturedAmount.Amount)
            {
                mismatched.Add(pair);
            }
            else
            {
                matched.Add(pair);
            }
        }

        var recordedButNotSettled = localPayments
            .Where(p => p.Status == PaymentStatus.Captured && !seen.Contains(p.Id))
            .ToList();

        var stillIndeterminate = localPayments
            .Where(p => p.Status == PaymentStatus.Indeterminate)
            .ToList();

        return new ReconciliationReport
        {
            Matched = matched,
            AmountMismatches = mismatched,
            SettledButNotRecorded = settledButNotRecorded,
            RecordedButNotSettled = recordedButNotSettled,
            StillIndeterminate = stillIndeterminate,
        };
    }
}

/// <summary>One line from the acquirer's settlement file.</summary>
public sealed record SettlementRecord(
    string ProviderReference,
    Money Amount,
    DateTimeOffset SettledAt,
    string? Scheme);

/// <summary>A local payment and the settlement line it matched.</summary>
public sealed record ReconciledPair(Payment Payment, SettlementRecord Settlement);

/// <summary>
/// The outcome of one reconciliation run.
/// </summary>
/// <remarks>
/// Each collection is a distinct operational workflow with a different owner, which is
/// why they are separate properties rather than a single list of exceptions.
/// </remarks>
public sealed record ReconciliationReport
{
    public required IReadOnlyList<ReconciledPair> Matched { get; init; }

    /// <summary>
    /// Settled for a different amount than we recorded — tips, partial captures, or
    /// scheme fees deducted at source. Finance investigates.
    /// </summary>
    public required IReadOnlyList<ReconciledPair> AmountMismatches { get; init; }

    /// <summary>
    /// The acquirer took money we never recorded. <b>Customer-facing and urgent.</b>
    /// </summary>
    /// <remarks>
    /// Usually the tail of an indeterminate payment: the authorisation succeeded, the
    /// response was lost, and our record was never completed. The customer has been
    /// charged and may have walked out without the goods.
    /// </remarks>
    public required IReadOnlyList<SettlementRecord> SettledButNotRecorded { get; init; }

    /// <summary>
    /// We recorded a capture the acquirer has not settled. <b>Merchant-facing.</b>
    /// </summary>
    /// <remarks>
    /// Often benign — settlement files lag by a day or two, so this list is expected to
    /// be non-empty and only matters when an entry persists. The customer is unaffected;
    /// the merchant is owed money.
    /// </remarks>
    public required IReadOnlyList<Payment> RecordedButNotSettled { get; init; }

    /// <summary>
    /// Payments never resolved by the sweep. Every one is a potential double charge.
    /// </summary>
    public required IReadOnlyList<Payment> StillIndeterminate { get; init; }

    public int ExceptionCount =>
        AmountMismatches.Count
        + SettledButNotRecorded.Count
        + RecordedButNotSettled.Count
        + StillIndeterminate.Count;

    /// <summary>
    /// True only when every category is empty.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT derived from a net variance figure. Two offsetting errors — a
    /// customer overcharged by 50 and another undercharged by 50 — net to zero and are
    /// emphatically not clean. A reconciliation that reports "balanced" in that case is
    /// worse than no reconciliation, because it retires the alarm.
    /// </remarks>
    public bool IsClean => ExceptionCount == 0;

    /// <summary>
    /// Signed difference between settled and recorded amounts, for reporting only.
    /// </summary>
    /// <remarks>
    /// Provided because finance asks for it, and explicitly not used to decide
    /// <see cref="IsClean"/>. Returns zero when nothing matched, since a variance across
    /// mixed currencies is not a meaningful number.
    /// </remarks>
    public Money NetVariance(string currency)
    {
        var total = Money.Zero(currency);

        foreach (var pair in AmountMismatches)
        {
            if (pair.Settlement.Amount.Currency == currency
                && pair.Payment.CapturedAmount.Currency == currency)
            {
                total = total + (pair.Settlement.Amount - pair.Payment.CapturedAmount);
            }
        }

        return total;
    }
}
