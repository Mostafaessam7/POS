# ADR 008 — Append-only movement ledger with a materialised balance

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 4

## Context

A mutable QuantityOnHand column loses history, cannot answer why stock is wrong, and is a lock-contention hotspot when several terminals sell the same fast-moving item concurrently.

## Decision

Stock is an append-only ledger of movements. Current balance is a materialised projection maintained transactionally, which can be rebuilt from the ledger at any time.

## Consequences

Every discrepancy is explainable, which is what shrinkage investigation actually requires. The projection can be rebuilt if it drifts. The cost is a second write per movement and a reconciliation job; both are cheap relative to the alternative.
