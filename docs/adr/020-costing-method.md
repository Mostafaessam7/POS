# ADR 020 — Weighted average cost

**Status:** Accepted · **Date:** 2026-07-20 · **Confirmed:** 2026-07-20 by product owner · **Phase:** 3, implemented in 4

**Supersedes:** the provisional version of this ADR, which recorded weighted average
as an unconfirmed assumption pending a jurisdiction answer.

## Context

Costing method determines stock valuation, reported margin, and taxable profit. FIFO,
weighted average, and standard cost are all defensible, and the choice is an
accounting policy decision rather than an engineering one.

The original ADR defaulted to weighted average and flagged it as an assumption. The
product owner has now confirmed: **weighted average is the default costing method,
unless a future legal or business requirement explicitly demands FIFO.**

## Decision

Weighted average cost, maintained on the materialised `StockBalance` row per
(warehouse, variant) and recalculated on every inbound movement:

```
newAverage = (existingQty * existingAvg + inboundQty * inboundUnitCost)
             / (existingQty + inboundQty)
```

Outbound movements consume at the current average and do **not** change it.

The calculation lives behind `ICostingPolicy` with a single implementation,
`WeightedAverageCostingPolicy`. The interface is retained despite having one
implementation — normally a smell — for the specific reason given below.

## Consequences

**Why weighted average is the better default.** It is the most widely accepted method
across jurisdictions, and it is markedly simpler to compute *correctly under
concurrency* than FIFO. FIFO requires maintaining ordered cost layers and consuming
them in sequence; two concurrent sales of the same variant must agree on which layer
each consumed, which forces serialisation on the hottest rows in the system. Weighted
average collapses that to a single running figure updated under a short row lock. For
a POS where the same fast-moving SKU is sold on twelve tills at once, that difference
is architectural, not cosmetic.

**Precision is retained in storage and rounded only for display.** The average unit
cost is stored at 6 decimal places (`decimal(19,6)`). Rounding it to 2dp and
multiplying back up by quantity reintroduces exactly the drift `Money` exists to
prevent; on a 10,000-unit balance a half-cent error in unit cost is a 50-pound
valuation error.

**The negative-balance case needs an explicit rule.** When existing quantity is zero
or negative, the weighted average formula divides by a number that is zero or has the
wrong sign, producing a meaningless or infinite cost. The implemented rule: if
existing quantity is at most zero, the incoming cost is *adopted outright* rather than
averaged. This is the standard treatment and the only defensible one — there is no
prior stock for the incoming cost to be averaged against. See ADR 027, which allows
negative stock and therefore creates this case routinely.

**Retaining the interface with one implementation is a deliberate exception** to the
usual rule against speculative abstraction. The justification is that the confirmation
above is explicitly conditional — "unless a future legal or business requirement
explicitly requires FIFO" — so a second implementation is a *known contingency* rather
than an imagined one, and the seam costs one interface. It is also the natural place
to add per-company policy selection if a franchise group operates across jurisdictions
with different requirements.

**What a switch to FIFO would still cost.** The interface limits the *code* change to
the Inventory module. It does not limit the *data* change: FIFO needs cost layers that
weighted average never recorded, so historical restatement would require replaying the
movement ledger to reconstruct layers, and any period already reported to a tax
authority could not be restated at all. This is the real cost of the decision, and it
is why the confirmation was worth obtaining before Phase 4 rather than after.

The append-only movement ledger (ADR 008) is what makes even partial restatement
possible. Had stock been stored as a mutable balance, the information needed to
recompute cost on any other basis would simply not exist.
