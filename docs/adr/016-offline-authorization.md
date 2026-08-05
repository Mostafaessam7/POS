# ADR 016 — Signed offline permission bundle

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 1

## Context

A disconnected terminal must still answer whether a cashier may approve a refund. This is the requirement generic authentication designs miss entirely.

## Decision

Terminals cache a server-signed bundle of permissions for every user enrolled at that branch, delivered with master-data sync, with a 24 to 72 hour expiry and verified locally against a public key. Sensitive actions authorized offline are flagged on the transaction and appear in an exceptions report on reconnect.

## Consequences

A terminal can read permissions but cannot forge or edit them, which matters because a till is physically accessible to staff. Revocation cannot be instant offline; it is bounded by bundle expiry. This is in explicit tension with ADR 013's instant online revocation and there is no clever fix, it is the irreducible cost of offline operation. It must be a documented business decision with an operational process for terminals offline beyond the window, not something discovered during an incident.
