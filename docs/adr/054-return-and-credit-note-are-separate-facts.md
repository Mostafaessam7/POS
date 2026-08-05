# ADR 054 — A supplier return and its credit note are separate facts

**Status:** Accepted · **Date:** 2026-07-22 · **Phase:** 7

## Context

Sending goods back to a supplier is two events that people habitually describe as one. Goods leave the building — a stock event, on a date we control. Money comes back — a financial event, on a date the supplier controls, for an amount the supplier decides.

Modelling them as one thing means assuming the second follows the first. It usually does. When it does not, the money is simply gone, and nothing in the system is expecting it.

## Decision

`SupplierReturn` carries both, separately. Dispatch produces stock instructions and moves the return to `Dispatched`. The credit note is recorded later, with the supplier's reference, the amount, and the date.

The credit is recorded **as received**, not as expected. If we returned 500 of goods and the supplier credits 450, the return holds both figures and `CreditShortfall` is 50. The status becomes `PartiallyCredited`, not `Credited`.

This is the entire point of the decision. A system that records the expected credit and marks the return closed has quietly written off 50 and told nobody. The gap between dispatched and credited is the report that recovers real money, and it exists only if the two numbers are stored separately and allowed to disagree.

A return may be cancelled while still in draft. Once dispatched it cannot: the goods are on a lorry, and the corrective action is a new inbound document, not the deletion of a fact (ADR 006).

Recording a credit note before dispatch is refused. A credit for goods that have not left is either a pricing adjustment or an error, and both deserve their own document rather than being absorbed here.

## Consequences

**A dispatched-not-credited report is required and is not yet built.** Without it this decision buys nothing: the data is correctly shaped and nobody is looking at it. This joins the reconciliation reports already outstanding from Phases 5 and 6.

Returns carry a reason — damaged, wrong item, overstock, expired, quality rejection — which drives both the supplier conversation and, eventually, supplier quality reporting.

The returned unit cost is supplied by the caller from the stock balance rather than derived from the original receipt. Weighted average means the units going back may no longer be valued at what they came in at, and the balance is the authority on what they are worth now.
