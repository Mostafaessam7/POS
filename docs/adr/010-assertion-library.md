# ADR 010 — Shouldly rather than FluentAssertions

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 0

## Context

FluentAssertions moved to a paid Xceed licence at version 8. Several other common .NET libraries made similar moves during 2025, including MediatR 13 and MassTransit v9.

## Decision

Use Shouldly (MIT). Pin FluentAssertions out of the dependency graph. Run a licence audit in CI against an allow-list of permissive licences.

## Consequences

Avoids an unbudgeted per-developer cost and, more importantly, avoids discovering the constraint at the point where migrating away is expensive. The licence audit stage generalises the protection to future changes.
