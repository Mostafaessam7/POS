# ADR 005 — UUID v7 for machine identity, gap-free sequences for fiscal numbering

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 1

## Context

Records created on a disconnected terminal need identity before they reach the server. Database identity columns cannot provide it: two offline terminals would both allocate 501. Tax authorities separately require gap-free receipt numbering.

## Decision

Two distinct concepts. Machine identity is Guid.CreateVersion7(), generated at the terminal. Fiscal identity is a gap-free ReceiptNumber allocated per terminal from a local counter, formatted branch-terminal-sequence.

## Consequences

UUID v7 is time-sortable, so it retains clustered-index locality without central coordination. Gap-free numbering per terminal is achievable offline; gap-free chain-wide is not, which is why the terminal code forms part of the number. A sequence regression on sync indicates a restored backup or a cloned terminal and is surfaced for operator investigation rather than silently accepted.
