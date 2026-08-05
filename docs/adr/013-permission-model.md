# ADR 013 — Permission-based authorization with versioned cache lookup

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 1

## Context

Roles checked at the enforcement point mean editing enforcement code the first time a customer wants a supervisor who can refund but not void. Separately, embedding 200+ scoped permissions in a JWT bloats every request.

## Decision

Enforcement checks permissions, never roles; roles are an administrative grouping in data. Permissions are granted at a scope (company, branch, warehouse). The token carries only a permission version; the set loads from Redis behind a short in-memory cache.

## Consequences

Revocation is immediate rather than waiting up to fifteen minutes for token expiry, which matters for a system authorizing cash refunds. The cost is a sub-millisecond warm-cache read per request. Tokens stay small, which matters when every terminal sends one on every request.
