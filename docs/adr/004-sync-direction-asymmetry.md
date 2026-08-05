# ADR 004 — Master data flows down, transactional data flows up

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 2

## Context

Bidirectional synchronisation of mutable records requires conflict resolution. Last-write-wins across intermittently connected terminals produces the classic POS failure where a store's price silently reverts overnight.

## Decision

Enforce directional asymmetry. HQ owns master data (products, prices, promotions, tax rules, users) and publishes it downward as versioned immutable snapshots; terminals never modify it. Terminals own transactional data (sales, payments, stock movements, shifts) and push it upward as append-only facts; HQ never edits it.

## Consequences

There are effectively no merge conflicts, because neither side mutates the same row. A store-level price override is modelled as a new master-data record published downward, not as an edit to the central record. The constraint must be defended: any future request for editable-at-store master data should be met with a downward-published override record instead.
