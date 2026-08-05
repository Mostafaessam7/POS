# ADR 018 — Sync protocol is versioned from the first message

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 2

## Context

Terminals in the field run whatever build was installed when the store opened. A chain will routinely have three versions live simultaneously.

## Decision

Every sync message carries a protocol version. The server declares a minimum supported version and rejects below it with an actionable error. The sync API is versioned independently of the main API.

## Consequences

The protocol can evolve without a flag-day upgrade across every till simultaneously, which is not something a retailer will agree to. The cost is maintaining compatibility shims for older versions, bounded by the declared minimum.
