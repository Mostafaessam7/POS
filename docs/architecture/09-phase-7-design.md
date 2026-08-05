# Phase 7 — Purchasing, supply chain and expenses

## The gate

> A purchase order is raised and approved, received in two partial deliveries each
> carrying its own freight, the supplier's invoice is matched with a tolerance variance,
> and the stock balance and weighted average cost are correct at every step.

Everything in this phase is downstream of that sentence. It is exercised as a single test
(`PurchasingToInventoryWorkflowTests.An_order_received_in_two_deliveries_with_freight_lands_the_right_weighted_average_cost`)
rather than only as unit tests, because the interesting failures in cost accounting are
not in any one calculation — they are in a correct calculation whose result never reaches
the ledger.

## Four numbers that are not the same number

The design follows from noticing that a single line of a purchase transaction carries four
different quantities of money, and that most systems get into trouble by conflating two of
them:

| Number | Lives on | Authority for |
|---|---|---|
| Agreed price | Purchase order | Whether the invoice may be paid |
| Delivered price | Goods receipt | Nothing on its own — it is what the supplier *claims* |
| Landed cost | Computed at posting | Stock value and therefore margin |
| Billed price | Purchase invoice | The payment, once matched |

Cost comes from the receipt; payment authority comes from the order. The invoice price
never touches the stock balance, and the gate test asserts that explicitly. This is the
same asymmetry that ADR 053 applies to matching, seen from the other side.

## Ordered, received, outstanding

The single most consequential decision in the phase is the smallest: a purchase order line
carries three quantities rather than a received flag (ADR 051). Everything else about
partial delivery falls out of it — the status machine, the tolerance check, the
replenishment feed, the ability to say "three are still coming" as distinct from "three are
never coming".

Short shipments are closed by an explicit act with a mandatory reason. The order never
quietly decides it is finished, because an order that looks open but is really dead
suppresses reordering, and stock runs out while the system believes it is inbound.

Over-receipt is allowed within a tolerance expressed as either a percentage or an absolute
number of units, whichever is satisfied. Small orders need the absolute bound; large orders
need the percentage. Beyond both, the receipt is refused rather than accepted-and-flagged —
goods can still be turned away at the door, and a decision at the point of receipt is worth
more than a report generated after the lorry has left.

## Landed cost, and the late one

Freight allocates by quantity, duty by value, and both go through `Money.Allocate` so the
remainder is distributed a penny at a time. A penny lost per delivery is a stock valuation
that will not reconcile against the purchase ledger, and finding it costs an afternoon.

The allocator is a pure static function over plain data — no clock, no database, no
aggregate — which is what makes the rounding behaviour, the interesting part, testable
directly.

A charge arriving after the goods is the harder case, and ADR 049 splits it: revalue the
proportion still on hand, expense the rest. The split is by quantity remaining, because the
question is "how many of the units this charge relates to do I still have". The revaluation
is applied as a value-only movement, which is the reason ADR 047 exists.

## What is deliberately absent

**No general ledger posting.** The purchase price variance from a late landed cost has
nowhere to go and is carried on the document. This is honest rather than finished.

**No supplier payment.** Invoices reach `Approved` and `Paid`; nothing models a payment
run, a remittance, or a bank file. That is an accounts-payable product and this is not one.

**No expense payment, reimbursement, budget or receipt image.** Expenses record that money
was spent and whether it belongs in stock. Everything else is out of scope until asked for
(ADR 055).

**No landed cost on transfers.** Inter-warehouse movement does not currently attract cost,
which is fine for a single-country chain and wrong for an importing one.

**No blind receiving.** The storeman sees the ordered quantities. Blind receipt — where
expected quantities are hidden so the count is independent — is a genuine control and is a
Phase 8+ conversation.

## Module boundary

Purchasing does not reference Inventory. `GoodsReceipt.Post` returns instructions built
from primitives and SharedKernel types, and the application layer applies them (ADR 052).
The same shape covers supplier returns.

The cost of this is that nothing at compile time forces the second half to happen. That
exposure is identical to the one Sales already carries, and it is discharged the same way —
by reconciliation. Phase 7 therefore adds two more reports to the outstanding list:
receipts posted versus stock movements recorded, and goods returned versus credits
received.

## Controls, and where they live

Two controls are enforced inside aggregates rather than in handlers, filters or the UI,
because those are the three places a control gets bypassed by a new call site:

- The person who raised a purchase order cannot approve it (ADR 050).
- The person who recorded an expense cannot approve it (ADR 055).

Thresholds, by contrast, are configuration. A tenant may set them to anything, including
requiring no approval at all. The split is deliberate: thresholds are a commercial choice
and change often; separation of duties is a control and does not.

Blocked invoices can be overridden. The control is not that overrides are impossible — a
three-cent rounding difference is not worth a week of correspondence — it is that they are
attributable, with a named person and a mandatory reason, so "who has been waving invoices
through" is a report rather than an investigation.

## Known gaps leaving Phase 7

- Both new modules are **domain-only**. There is no `PurchasingDbContext`, no
  `ExpensesDbContext`, no configuration, no migration, no API surface. They join the
  deferred infrastructure milestone (ADR 046), which remains the highest-priority work
  before production readiness.
- The reports that make several of these decisions worth anything are **not built**:
  dispatched-not-credited, receipts-without-movements, linked-but-unapplied expenses,
  purchase price variance.
- Delegated approval authority, absence cover, and per-category approval bands are not
  modelled.
- Supplier quality reporting — return reasons aggregated per supplier — is a natural
  consequence of the data now being captured, and is not built.
