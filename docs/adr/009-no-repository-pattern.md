# ADR 009 — No repository pattern over EF Core

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 0

## Context

The default Clean Architecture template wraps EF Core in repositories and a unit of work.

## Decision

Use DbContext directly. DbContext is already a unit of work and DbSet<T> is already a repository.

Exceptions are permitted only where an abstraction has **genuinely different implementations per storage engine**, not merely to hide EF Core. Two currently qualify:

- **`IStockLedger`** — the SQL Server implementation uses a lock-free relative UPDATE with an UPDLOCK/HOLDLOCK path for cost (ADR 026); SQLite on the terminal cannot and does not need to. The concurrency strategy, not the query, is what differs.
- **`IFiscalDocumentStore`** — the fiscalisation pipeline runs on the terminal as well as in the cloud, because offline issuance is the normal case in reporting regimes (ADR 032). Chained regimes additionally need `GetLastCanonicalHashAsync` to be serialised against concurrent issuance, and the mechanism for that differs by engine.

**Amended 2026-07-21.** This ADR previously read "the single exception is IStockLedger". Phase 5 introduced `IFiscalDocumentStore` without amending it, so the codebase silently contradicted its own ADR for a whole phase. The wording is now a *criterion* rather than a count, because a numbered allowance invites exactly that outcome: the next author either violates the ADR quietly or adds an exception without stating the principle that justified it.

## Consequences

Avoids a layer that mostly forwards calls while blocking Include, projection, and split queries. Testing uses Testcontainers against real SQL Server rather than mocking a repository, which catches constraint and relational-semantics bugs that mocks hide. The trade-off accepted is tighter coupling to EF Core; the abstraction it would buy is one that teams routinely find leaks anyway.
