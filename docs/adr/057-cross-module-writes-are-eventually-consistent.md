# ADR 057 — Cross-module writes are eventually consistent, not transactional

**Status:** Accepted · **Date:** 2026-07-28 · **Phase:** Production-readiness hardening

## Context

Five call sites now cross a module boundary and write on both sides of it: a goods
receipt posts stock (`GoodsReceiptPostingService`), a supplier return dispatches stock
(`SupplierReturnDispatchService`), a synced sale posts stock, issues a fiscal document,
and records electronic tenders (`SaleSyncHandler`), a transfer moves stock between two
legs (`StockTransferService`), and a stocktake posts its counted variance
(`StocktakeService`).

Every one of these wants to be a single database transaction and cannot be one. ADR 002
gives each module its own schema and its own `DbContext` specifically so a module can be
deployed, migrated, and eventually extracted independently; a distributed transaction
across `PurchasingDbContext` and `InventoryDbContext` would immediately reintroduce the
coupling that separation exists to prevent, and SQL Server's own distributed transaction
coordinator is not something this platform wants to depend on for every sale a terminal
uploads.

So every one of these writes two things that cannot commit together, and the question
this ADR answers is: in which order, and what happens when the process dies between
them.

## Decision

**The external effect commits first; the local record is marked complete second.**
"External" means whichever side is not the aggregate initiating the operation — the
stock movement a receipt posts, the fiscal document a sale issues, the payment a sale
records, the ledger movement a transfer or stocktake posts. A crash between the two
writes leaves the external effect applied and the initiating record NOT YET marked
done. This is the safe half of the failure: a retry re-derives the identical external
effect, finds it already applied, and completes the local write it had not yet reached.
The alternative ordering — mark the local record done, then apply the external effect —
leaves a record that CLAIMS an effect happened when it silently never did, and nothing
about that state signals a problem to retry.

**Idempotency is the other half, and it is what makes the retry safe rather than merely
survivable.** Every external write is keyed so a repeat call is recognised and no-ops
instead of re-applying:

- A goods receipt or supplier return keys on `(document id, movement type, warehouse)`,
  not document id alone — a single document can legitimately produce movements at more
  than one warehouse across its lifecycle, and each leg must be independently
  replayable.
- A synced sale keys stock posting and payment recording on the sale id (and, for
  payments, the sale id plus tender sequence, since one sale can carry several tenders).
- Fiscal issuance keys on the sale id the document was issued for.
- A transfer keys on `(document id, movement type, warehouse)` for the same reason as a
  receipt: dispatch, receive, and write-off each move stock at a different warehouse,
  and each is its own replayable leg.
- A stocktake keys its posted adjustment on the stocktake id.

None of this is a message queue, an outbox table, or a saga. It is two writes in a
specific order, each individually idempotent, running in a single process against two
databases that will never see a single transaction. That is a deliberately small amount
of machinery for what it buys: a crash at the worst possible moment is a retry, not a
reconciliation project.

## Consequences

**A caller must actually retry.** This pattern makes a retry safe; it does not make one
happen. If a terminal or a background job gives up after the first attempt, a crash
between the two writes leaves the local record permanently incomplete — correct, in
that nothing was double-applied, but stuck. Every current caller (the terminal upload
protocol, the goods-receipt and supplier-return posting endpoints) is built to retry on
failure or ambiguous response, which is what makes this safe in practice and not just in
theory.

**Every new cross-module write must follow the same two rules or it is not safe.**
Getting the order backwards, or keying idempotency on the wrong thing (document id alone
when a document produces effects at more than one location), reintroduces exactly the
failure mode this ADR exists to close. `GoodsReceiptPostingService`,
`SupplierReturnDispatchService`, `SaleSyncHandler`, `StockTransferService`, and
`StocktakeService` are the reference implementations — a sixth call site should read one
of them before inventing its own ordering.

**This is not free of a genuine gap.** Between the external commit and the local
commit, a reader querying the local aggregate sees it as not-yet-complete even though
the external effect has already happened — a stock movement can exist for a receipt
that still reads as unposted. No caller in this codebase currently depends on
strict read-after-write consistency across that window, so it is accepted rather than
solved. A future caller that cannot tolerate it needs a different mechanism, not a
tighter version of this one.

**Distributed transactions and a message-based saga were both rejected.** A two-phase
commit across `PurchasingDbContext`/`InventoryDbContext` reinstates cross-module
coupling ADR 002 was written to prevent, and the estate has exactly one process talking
to exactly one SQL Server instance today — the operational cost of a coordinator has no
corresponding benefit yet. An outbox-and-relay saga is the right answer at a scale where
these writes cross a network boundary or a process boundary; at the current scale it
would be machinery built for a problem this system does not have.
