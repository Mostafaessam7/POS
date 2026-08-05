# ADR 026 — Split concurrency by whether a movement changes cost

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 4

## Context

Multiple terminals sell the same product concurrently and must all update one StockBalance row. Read-modify-write loses updates. Optimistic concurrency with retry degrades under exactly the contention it must handle — a hot SKU at a busy checkout. Pessimistic locking makes the best-selling product the throughput ceiling. Asynchronous projection is fine for reporting and too stale for "2 left in stock".

## Decision

Split the write path by whether the movement can change weighted average cost.

Quantity is associative, so quantity-only movements use an atomic relative statement — `SET QuantityOnHand = QuantityOnHand + @delta` — with no read, no version check and no retry. Weighted average cost is not associative and requires read-modify-write under `UPDLOCK, HOLDLOCK`, but only inbound movements can change it.

## Consequences

Sales, wastage and transfers out take the lock-free path. Receipts, returns to stock and cost adjustments take the locked path, and these are low-frequency back-office actions performed by one goods-in user at a time. The contended path is therefore never on the checkout critical path, so throughput does not degrade on popular products — the scalability property that actually matters for a POS.

The constraint this creates: a sale must never be allowed to change average cost. `MovementTypeExtensions.AffectsAverageCost` is the single point of truth and is covered by a test, because if a future movement type quietly joins the cost-changing set the regression is a silent throughput cliff rather than a visible failure.

FIFO costing would break this decision, since consuming ordered cost layers serialises every outbound movement. That is a further argument for weighted average (ADR 020) beyond the accounting one.
