# ADR 040 — Sale history is served from a read model, not by rehydrating aggregates

**Status:** Accepted · **Date:** 2026-07-21 · **Phase:** 5

## Context

The `Sale` aggregate is designed for correctness during checkout: it loads its lines, tenders, and per-line adjustment traces so it can enforce its invariants. A grocery basket can carry 200 lines, each with several adjustment records.

Almost every read of a sale after completion — receipt lookup, sales history, X and Z reports, dashboards, exports — needs a fraction of that data, and needs it across thousands of sales at once. Loading full aggregates to render a history list would be ruinous.

## Decision

Write goes through the aggregate; read goes through a separate, denormalised projection built from `SaleCompleted` events dispatched after commit via the outbox. Reporting and history queries never rehydrate aggregates. The aggregate is loaded only when a sale is being modified, which is only ever while it is open.

## Consequences

Checkout latency stays governed by a single sale's size, and reporting load is decoupled from transactional storage.

The projection is eventually consistent. A sale may be completed a moment before it appears in history, which is acceptable for reporting and would not be for the receipt itself — so receipt reprint reads the authoritative record, not the projection.

This adds a projection to build, backfill, and keep correct, plus a rebuild path for when it drifts. The rebuild is the same pattern already established for stock balances in Phase 4: the event log is authoritative, the projection is an optimisation, and divergence is reported rather than silently repaired.
