# ADR 015 — PIN authentication for cashiers

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 1

## Context

A cashier may switch a dozen times an hour. A twelve-character password is unusable in that context and will be defeated in practice by a sticky note on the monitor, which is strictly worse security.

## Decision

Cashiers authenticate with a PIN, subject to constraints: valid only on an authenticated enrolled terminal, scoped to a single branch, rate-limited per terminal and per user with lockout and alerting, stored as a separate credential from the password, and never valid for the web back office. Back-office and administrative access requires full password authentication plus MFA.

## Consequences

The small keyspace is acceptable only because of the constraints; a PIN is useless to a remote attacker, and the search space is employees at one store rather than all users. This will be challenged in a security review, and the documented threat model is the answer. Approving this ADR means approving that threat model.
