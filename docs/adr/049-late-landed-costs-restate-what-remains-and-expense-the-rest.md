# ADR 049 — A late landed cost revalues the stock still held and expenses the rest

**Status:** Accepted · **Date:** 2026-07-22 · **Phase:** 7

## Context

Freight and duty invoices arrive weeks after the goods. By then some of those units have been sold, at a margin computed from a cost that did not include the charge.

There are three ways to handle this and two of them are wrong.

**Restate history.** Reopen the original receipt, recompute the cost, and cascade the correction through every sale that consumed those units. This is arithmetically pure and operationally intolerable: it changes reported margin for periods already closed, and it violates ADR 006, which holds financial records immutable.

**Ignore the split.** Load the whole charge onto the units still on hand. Cheap to implement, and it produces a per-unit cost that is simply false — 100 of freight spread over the 50 units that happen to remain values them as though they cost twice what they did. If none remain, the charge cannot land at all.

**Split it.** Revalue the proportion still on hand; recognise the remainder in the current period as a variance.

## Decision

`LateLandedCostAllocator.Split` divides the charge by **quantity remaining**, not by value:

- The share attributable to units still held is applied as a value-only `CostAdjustment` movement (ADR 047).
- The share attributable to units already sold is a **purchase price variance**, recognised now.

The split is by quantity because the question being answered is "how many of the units this charge relates to do I still have". Value would answer a different question and would drift as the average moved.

The quantity still on hand is **supplied by the caller**, read from the stock balance in the application layer. Purchasing does not query Inventory's tables (ADR 002).

Three edge cases are decided rather than left to emerge:

- **More on hand than was received** — capped at the received quantity. The excess came from later deliveries that carried their own freight; charging them again double-counts.
- **Negative stock** — permitted by ADR 027, but it cannot be revalued: there are no units present to carry the cost. Treated as nothing on hand, so the whole charge becomes variance, where somebody will see it.
- **Indivisible splits** — delegated to `Money.Allocate`, so the two halves sum exactly to the charge.

## Consequences

Margin on the units already sold stays wrong forever. That is the deliberate trade: a permanently slightly-wrong closed period, against a period that reopens every time a haulier is slow with paperwork.

The variance has nowhere to go. There is no General Ledger module, so it is carried on the document for later export. **This is unfinished work**, and until it is finished the variance is visible only to someone who reads the purchasing documents.

A large variance is a signal, not just an accounting entry: it means goods are being sold before their true cost is known. A report over this figure is a genuinely useful early warning about buying practice, and it is not yet built.
