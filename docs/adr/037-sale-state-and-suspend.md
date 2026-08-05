# ADR 037 — Sale state machine, and suspend/resume as exclusive ownership transfer

**Status:** Accepted · **Date:** 2026-07-21 · **Phase:** 5

## Context

A parked transaction that can be resumed on a different till is a routine retail requirement — a customer forgets an item, the queue moves on, they return to whichever till is free.

This collides directly with ADR 004, the sync asymmetry that makes offline work: transactional data flows UP from terminal to cloud only. A suspended sale resumed elsewhere must flow DOWN, which is precisely the direction the architecture forbids, and for good reason: bidirectional flow of a mutable record reintroduces the merge-conflict problem the whole design exists to eliminate.

There is also a modelling question about abandoned versus reversed transactions, which are frequently conflated.

## Decision

A sale moves through `Open → Suspended → Open → Completed | Cancelled`, with `Voided` reachable only from `Completed` by way of a separate reversing document.

`Cancelled` and `Voided` are distinct states. Cancelled means an open basket was abandoned before any financial event occurred. Voided means a completed sale was reversed by a subsequent document.

Suspend/resume is modelled as **exclusive ownership transfer**, not replication. Exactly one terminal owns a suspended sale at any moment, recorded in `OwningTerminalId`. Resuming on another terminal transfers ownership; it never copies. Because ownership is exclusive, two tills can never hold divergent copies, and there is nothing to merge — the record still only ever flows in one direction at a time.

Cross-terminal resume therefore requires connectivity, since ownership transfer must be arbitrated centrally. Same-terminal resume works fully offline.

A sale that has already taken a tender cannot be suspended.

## Consequences

The requirement is met without weakening the sync asymmetry. What would have been a bidirectional replication problem becomes a lease, which is a far smaller and better-understood mechanism.

Cross-terminal resume is unavailable while a store's uplink is down. Same-terminal resume, which is the common case, continues to work. This is a documented product limitation rather than a bug.

Keeping `Cancelled` separate from `Voided` preserves the distinction between "never happened" and "happened and was undone" — the exact question an auditor asks, and one that cannot be recovered once the two are merged.

Ownership arbitration needs a concurrency test: two terminals attempting to resume the same suspended sale simultaneously must result in exactly one winner. That test is outstanding.
