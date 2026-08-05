# ADR 011 — No mediator library

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 0

## Context

MediatR 13 requires a commercial licence as of mid-2025. The pattern is near-ubiquitous in .NET Clean Architecture templates.

## Decision

Use no mediator. Handlers are plain classes registered in DI and invoked directly from minimal API endpoints. Cross-cutting concerns use ASP.NET Core endpoint filters.

## Consequences

Endpoint filters provide the pipeline behaviour that was MediatR's main benefit, are built into the framework, and involve no reflection at dispatch. Call stacks are traceable. Revisit Wolverine (MIT) at Phase 2 if genuine in-process message dispatch is needed; it would also bring a transactional outbox.
