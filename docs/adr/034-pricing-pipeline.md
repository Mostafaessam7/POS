# ADR 034 — Pricing as an ordered pipeline with a traceable adjustment record

**Status:** Accepted · **Date:** 2026-07-21 · **Phase:** 5

## Context

Pricing is where retail systems rot. It starts as a unit price times a quantity, then accumulates line discounts, promotions, order discounts, coupons, customer group pricing, and tax — each added under delivery pressure as a conditional in whatever method was nearest. Within two years nobody can predict what a basket will total, and nobody dares change it.

There is a second, operational problem. A customer disputes a price at the counter, and the manager has no way to answer beyond "that is what the system says". That is not an acceptable answer, and it produces disputes that cannot be resolved and refunds that should not have happened.

## Decision

Pricing is a fixed, ordered pipeline of seven stages: base price, line discount, promotion, order discount, coupon, tax, rounding. The order is canonical and enforced by the pipeline rather than by the order stages happen to be registered in.

Every stage appends a `PriceAdjustment` to the line it touches, recording sequence, stage, description, amount, the source that caused it, and the principal who authorised it where one was involved. The trace is persisted with the sale.

Stages are pure functions over a snapshotted `PricingContext`. No stage may read the clock or query live catalog data, so the same basket and configuration always produce the same total.

The pipeline asserts, before returning, that the lines sum exactly to the total. Failing that assertion is an error, not a warning.

## Consequences

"Why was this 4.37?" is answerable at the counter by reading the trace, and the answer includes who authorised each discount. Discount frequency by operator becomes a standing shrinkage report rather than a forensic exercise.

Ordering is now a deliberate, documented decision. Line discounts precede promotions so a manual markdown does not stack unexpectedly; order discounts follow line-level so the percentage applies to the already-discounted subtotal; tax is second to last because it applies to the net actually charged; rounding is last because it belongs to the payable total rather than to any line.

The cost is more machinery than a single method, and an adjustment trace that consumes storage on every line of every sale. Both are accepted: the trace is the only thing that makes the engine auditable, and pricing that cannot be explained cannot be trusted.
