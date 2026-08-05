# ADR 043 — Idempotency keys are generated on the terminal

**Status:** Accepted · **Date:** 2026-07-22 · **Phase:** 6

## Context

The classic double charge has a fixed shape: the authorisation succeeds, the network drops before the response arrives, the cashier sees an error and presses Pay again, and the customer is charged twice. It is not a rare edge case — on a flaky store link it happens weekly.

A server-generated key cannot prevent it. By the time the server issues a key the first request has already been made, so the retry gets a *different* key and looks like a new payment. The key must identify the cashier's *intent*, and the intent originates at the till.

## Decision

The terminal generates an `IdempotencyKey` when the cashier initiates payment, and reuses the same key for every retry of that intent. The orchestrator looks the key up before doing anything else and returns the existing payment if it finds one.

Uniqueness is enforced by a unique index on `(TenantId, IdempotencyKey)`. The application-level lookup alone has a race between the check and the insert; under a double-tap on a slow till that race is not theoretical.

Additionally, a payment found in `Indeterminate` state is **not** returned for reuse — the retry is refused outright with `payment.prior_attempt_unresolved`. Handing back an unresolved payment invites the caller to try again, which is exactly the sequence being prevented.

## Consequences

Retry becomes safe, which means the terminal can retry aggressively instead of guessing.

The terminal is now responsible for persisting the key across its own restart. A key held only in browser memory is lost precisely when it is most needed — after the crash that caused the retry — so it must be written to the local store alongside the sale.
