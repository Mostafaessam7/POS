# ADR 047 — Value-only stock movements are a distinct kind, not a quantity movement of zero

**Status:** Accepted · **Date:** 2026-07-22 · **Phase:** 7

## Context

Phase 4 established that a stock movement with a zero quantity delta is refused: a movement that changes nothing is noise in the audit trail and a false positive in every reconciliation that counts documents. That rule was correct and remains.

Phase 7 then produced a case the rule did not anticipate. A freight invoice arriving after the goods must change the *value* of stock without changing the *quantity* — no units move, but the average cost is wrong until the charge lands. The same shape appears in a revaluation after a supplier credit, and it will appear again when standard costing arrives.

Worse, the Phase 4 error message told callers to use `MovementType.CostAdjustment`. That enum member did not exist. The guidance pointed at nothing, and there was no path to do the thing it recommended. This was found while building Phase 7 and is the kind of defect that survives review indefinitely, because the message is only read by someone who has already hit the error.

## Decision

Value-only adjustments are a separate movement type (`MovementType.CostAdjustment`) created through a separate factory (`StockMovement.RecordValueAdjustment`), not through `Record` with a quantity of zero.

The two factories exist because the two things have **different invariants**, not merely different arguments:

| | `Record` | `RecordValueAdjustment` |
|---|---|---|
| Quantity delta | must be non-zero | must be zero |
| Total cost | derived from quantity × unit cost | supplied directly, **may be negative** |
| Reason code | required for some types | always required |

Collapsing these into one method means one of the two rules has to become conditional on the type argument, and a conditional invariant is one that will eventually be checked in the wrong branch. `Record` now refuses `CostAdjustment` explicitly, so the guard exists on both sides.

`StockBalance.ApplyValueAdjustment` is the matching balance operation: total value moves, quantity does not, and the average is recomputed. It refuses to divide when nothing is on hand and leaves the average untouched rather than throwing — the charge still needs to land somewhere, and that decision belongs to ADR 049, not here.

`CostAdjustment` reports `AffectsAverageCost() == true`, so it takes the same row lock as a receipt. It reports `IsInbound() == null`: it is neither, and forcing an answer would put it on the wrong side of every directional report.

## Consequences

A negative `TotalCost` is now representable on the ledger, where before every movement's value followed its quantity's sign. Any report that inferred direction from the sign of value must use `IsInbound()` instead. Nothing built so far does, but it is the trap this change sets.

The audit trail gains rows that a stock count will never explain, because no units moved. This is correct — the value changed and somebody should be able to see when and why — but it means reconciling the ledger against a physical count must filter by movement type rather than summing everything.
