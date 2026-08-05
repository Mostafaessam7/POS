# Phase 6 — Payments and Hardware

**Status:** implemented (domain and orchestration layers compiled and tested; persistence and hosting not built)
**ADRs:** 041–045

---

## The gate, and why it drove every decision

> A card payment survives a network cut immediately after authorisation and before the
> response arrives, with no double charge.

Almost every choice below is downstream of that one sentence. It is worth being precise
about what it actually demands, because it is easy to satisfy the words and miss the
point.

The cut happens at the worst possible instant: the acquirer has approved and is holding
the customer's funds, and we do not know. There is no clever protocol that makes us
know. So the design goal is not *avoid the ambiguity* — it is **guarantee that the
ambiguity always lands on a durable row we can come back to**, and never on nothing.

That produces four rules, in order of how much they matter:

1. **Write before you call.** The payment row is committed before the provider is
   touched (ADR 042). If we call first and crash, money moved and no record exists —
   the only genuinely unrecoverable failure in the whole system.
2. **Unknown is a state, not a synonym for failed.** `Indeterminate` is a distinct,
   non-final status (ADR 044). Mapping a timeout to `Failed` is how a customer gets
   charged for a sale nobody recorded.
3. **The retry key comes from the terminal.** A server-generated key cannot deduplicate
   the retry, because the retry is exactly the case where the server's answer never
   arrived (ADR 043).
4. **Resolve by asking, not by guessing.** `QueryAsync` is on the required provider
   interface, not an optional extension. `NotFound` from the system of record is the
   only inference we permit.

## What is deliberately *not* here

**No cardholder data, at all** (ADR 045). Not encrypted, not tokenised-by-us, not
"temporarily". The device encrypts at the read head; we handle an opaque payload, a
masked last-four, and an acquirer token. This is not caution — PCI scope is a step
function, and the difference between handling a PAN briefly and never handling one is
the difference between a SAQ D audit programme and a SAQ P2PE questionnaire. It is
enforced by `CardDataArchitectureTests`, which fails the build rather than filing a
ticket.

**No provider named in a branch.** Behaviour keys off `PaymentCapabilities` data —
`SeparatesAuthAndCapture`, `SupportsOfflineAuthorisation`, `OfflineFloorLimit`,
`SupportsPartialRefund`, `SupportsVoid`. This mirrors the fiscal module's country
agnosticism (ADR 031): the moment orchestration says `if (provider == "X")`, the second
acquirer becomes a rewrite instead of a config row.

**No fallback provider.** If provider resolution fails, that is an error. Silently
falling back means charging the wrong merchant account — a reconciliation problem that
surfaces days later at the bank, not seconds later at the till.

## Aggregate shape

`Payment` is a separate aggregate from `Sale` (ADR 041), on the same reasoning as
`FiscalDocument`: their lifecycles differ by orders of magnitude. A sale is done when
the customer leaves; the payment settles tomorrow and can be charged back in six months.

A refund is a **new** `Payment` with `Kind = Refund` linked to the original, never a
mutation of it (D6). The original accumulates `RefundedAmount` and keeps its own status,
because it remains a true record of a charge that did happen. Refund-to-original-tender
then falls out for free — the refund carries the original's provider reference.

`RefundedAmount` is persisted on the aggregate rather than derived by summing linked
refunds. Deriving it would make the over-refund check a query whose answer is stale on
return; persisting it puts the check under the aggregate's own `RowVersion`.

## The provider seam

`IPaymentProvider` — `Authorise`, `Capture`, `Void`, `Refund`, `Query`. `Query` is
required, not optional, because a provider that cannot tell us what happened cannot be
integrated safely; the resolution path has no alternative.

The reference implementation is `ManualCardProvider`: a standalone bank terminal sitting
beside the till, where the cashier keys in the approval code. This was chosen as the
"at least one real integration" deliberately, and the honesty is worth stating — nuget
is blocked in this environment, so no SDK-based integration could have been *verified*,
only written. `ManualCardProvider` needs no network, no credentials and no certification,
and it exercises the awkward corners rather than the comfortable ones: it captures
immediately (no separate auth), supports no void at all, has no floor limit because our
connectivity is irrelevant to a device that dials out itself, and its `QueryAsync`
honestly returns `Unknown` because only the paper receipt knows. A provider seam that
survives that shape will survive a well-behaved SDK.

## Reconciliation

`SettlementReconciler` is a **pure function**: two lists in, a report out. No database,
no clock, no I/O. That is what makes the interesting case testable.

The buckets are asymmetric on purpose:

| Bucket | Meaning | Urgency |
|---|---|---|
| `SettledButNotRecorded` | They took money we have no record of | **Urgent** — a charged customer |
| `RecordedButNotSettled` | We recorded it, they have not paid yet | Usually benign banking lag |
| `AmountMismatches` | Same reference, different money | Investigate |
| `StillIndeterminate` | Unresolved on our side at cut-off | Blocks the day's close |

`IsClean` is `ExceptionCount == 0`, **not** `NetVariance == 0`. Two offsetting errors net
to zero and are still two errors. There is a test named after exactly that.

## Hardware

Contracts were consolidated into `POS.Hardware.Abstractions` — `IReceiptPrinter`,
`ICashDrawer`, `IBarcodeScanner`, `IWeighingScale`, `ICardReader`, `ICustomerDisplay`.
The move was not cosmetic: the previous location inside `POS.TerminalAgent` declared its
own `Result<T>`, which shadowed the SharedKernel type. Two `Result<T>` types in one
solution is a bug waiting for a `using` directive.

`ICardReader` returns `CardReadResult` — encrypted payload, masked PAN, scheme, entry
mode. There is no member that could carry a PAN, and the architecture test proves it.

## Known gaps this phase leaves open

Stated plainly rather than buried:

- **No `PaymentDbContext`, configuration or migration.** The ERD section is design, not
  schema. Same gap Sales and Fiscal have.
- **The indeterminate-resolution background sweep does not exist.** `ResolveAsync` works
  and is tested; nothing calls it on a schedule. Until that is built, ADR 044's
  guarantee depends on a human noticing.
- **The settlement job does not exist.** The reconciler is pure and tested; nothing
  fetches the acquirer file.
- **Three mandatory reconciliation reports, none built** — Sale↔Fiscal, Sale↔Payment,
  Payment↔settlement. Each substitutes for a foreign key we chose not to have. This is
  the largest single debt in the project.
