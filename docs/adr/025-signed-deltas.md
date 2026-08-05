# ADR 025 — Stock movements are signed deltas, never absolute balances

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 4

## Context

Each ledger entry must record a stock change. It can store either the resulting quantity ("on hand is now 7") or the change ("−1"). The choice looks cosmetic and is not.

## Decision

Movements store a signed delta. Absolute resulting quantities are never persisted on a movement.

## Consequences

Deltas are commutative, so replaying them in any order produces the same balance. That property is what makes offline work: a movement generated on a till disconnected for three days can arrive at any point and still be correct. An absolute "quantity is now 7" record would silently overwrite every sale, delivery and transfer that occurred while the till was offline — a data-loss bug that only appears under exactly the conditions hardest to reproduce in testing.

The ledger design and the offline design are therefore one decision rather than two, and this ADR is the reason ADR 004's sync asymmetry works for stock. The cost is that a current balance is never readable from a single movement row; that is what StockBalance is for.
