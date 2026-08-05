# ADR 023 — Prices and tax rates are versioned and date-effective

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 3

## Context

A mutable price column means a reprinted receipt shows the wrong price, promotions cannot be scheduled in advance, and a tax rate change silently restates every historical report.

## Decision

Price lists are versioned and date-effective with a priority for overlap resolution. Tax rates are versioned by effective date. The sale line snapshots the resolved price, the tax rate, and the price list version that produced it.

## Consequences

Historical questions are answerable years later, which is what pricing disputes require. Returns processed after a tax change correctly use the original transaction's rate. Reporting must never join to a live price, and the snapshot is what makes that possible.
