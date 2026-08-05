# ADR 024 — Round half away from zero, in one place

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 1

## Context

The .NET default is banker's rounding (half to even), which gives 2.34 for 2.345. Commercial and most statutory conventions round half away from zero, giving 2.35. Rounding scattered across the codebase produces amounts that differ by a penny depending on which path computed them.

## Decision

Money.Round() rounds half away from zero and is the only rounding entry point in the system. Money is decimal throughout; float and double are banned in any financial path.

## Consequences

Amounts are consistent regardless of code path, and reconciliation differences of a penny do not appear. Any jurisdiction requiring a different convention changes one method. Binary floating point cannot represent 0.10, so a hundred additions drift by enough to unbalance a drawer.
