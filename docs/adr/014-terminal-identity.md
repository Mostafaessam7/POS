# ADR 014 — Terminals are principals, separate from users

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 1

## Context

Generic authentication designs model only users. A till is a long-lived, physically located, semi-trusted device with a different lifetime and threat model from a cashier session.

## Decision

Terminals authenticate with a client certificate provisioned at installation. Users authenticate on top of an already-authenticated terminal. The access token carries both; a token with a user but no terminal claim cannot complete a sale.

## Consequences

A stolen till can be revoked without touching any user account. Data sync is scoped to a branch by the device rather than by the cashier, who may work at several stores. Receipt numbering is per terminal, which is what makes gap-free fiscal sequences achievable offline.
