# ADR 028 — Transfers are two movements through an in-transit location

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 4

## Context

Stock leaves branch A on Monday and arrives at branch B on Thursday. A single instantaneous movement forces a false choice: either A is short for three days while B's staff see stock they cannot find, or the stock briefly exists in both places.

## Decision

A StockTransfer document with two legs — dispatch and receipt — moving stock through a warehouse of kind `InTransit`. Receipt discrepancies are recorded as a variance that persists until explicitly written off with a reason code and an elevated permission.

## Consequences

Total stock across all warehouses is conserved at every instant, which is the invariant the nightly reconciliation checks. Stock in transit is visible and ageing on it is reportable — transfers stuck in transit for weeks are either lost or stolen, and nobody notices without a status index.

Retaining the variance rather than auto-adjusting is the control. Transfer shrinkage is a well-known theft vector; requiring a named person to decide the stock is gone is the entire point, and silently balancing it would remove the only signal.

The cost is one extra warehouse record per branch and a two-step workflow where users may expect one step. That is a genuine usability cost, accepted because the alternative makes reconciliation impossible.
