# ADR 002 — Modular monolith over microservices

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 0

## Context

A POS platform is transactionally coupled at its core. Completing a sale atomically writes the sale header, its lines, tenders, stock movements, loyalty accrual, and shift totals. The team is small and the product is pre-revenue.

## Decision

Build a modular monolith with enforced module boundaries: one deployable, one database, separate module assemblies, no direct project references between modules.

## Consequences

A single database transaction covers the entire sale, which is the correct default and avoids sagas for a workflow that is genuinely atomic. Deployment and local development stay simple. The cost is that modules cannot scale independently; the mitigation is that boundaries are compile-enforced (ArchUnit rules 1 and 2), so extracting a module later is a mechanical exercise rather than an archaeology project. Distributed transactions are being deliberately deferred until there is evidence they are needed.
