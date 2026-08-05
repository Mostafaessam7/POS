# ADR 030 — Landed costs are apportioned into unit cost by largest remainder

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 4

## Context

Freight, duty and handling are part of what stock cost. Expensing them separately overstates margin on every subsequent sale of the goods.

## Decision

Landed costs are apportioned across receipt lines and folded into unit cost. The apportionment basis is configurable per receipt — by value (default), quantity, or weight — and the arithmetic uses `Money.Allocate`, which distributes the indivisible remainder by largest remainder so shares sum exactly to the input.

## Consequences

Naive division does not work: £100 across three lines yields £33.33 three times and loses a penny, and a stock valuation that is a penny out is a stock valuation that will not reconcile — which is the Phase 4 gate. Allocation is therefore a SharedKernel concern rather than an Inventory one, and Phase 5 reuses it to spread invoice-level discounts across sale lines.

Where the chosen basis carries no information — apportioning by weight when no line has a weight — the code falls back to an even split rather than throwing from deep inside the allocator, since an even split is the only defensible answer when the basis cannot discriminate.

Retrospective landed costs, where a freight invoice arrives after the goods have been received and partly sold, are NOT handled in Phase 4. That requires either a revaluation movement or restating cost of goods already sold, and the correct treatment is an accounting-policy decision. Recorded as known technical debt.
