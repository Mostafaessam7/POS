# ADR 035 — Promotions are data with a closed set of effects, not a scripting language

**Status:** Accepted · **Date:** 2026-07-21 · **Phase:** 5

## Context

Marketing will eventually ask for "buy 2 get 1 free on category X, except on Tuesdays, unless the customer is in group Y". The instinctive engineering response is to make promotions arbitrarily expressive — embed a rules engine, a scripting language, or a general expression evaluator so new promotions need no deployment.

## Decision

Promotions are persisted data with a closed set of match conditions (variant, category, minimum quantity) and a closed set of effects (percentage off, amount off per unit, fixed unit price), evaluated in priority order with an exclusivity flag.

When a promotion cannot be expressed, the response is to add one new effect type deliberately, with tests, not to make everything expressible.

## Consequences

Promotions remain testable, auditable, and fast. Every possible pricing outcome is reachable by a finite set of code paths that can be reasoned about and property-tested.

Marketing cannot self-serve arbitrary promotions; genuinely novel mechanics require a release. This is the deliberate trade. The alternative puts an interpreter for user-authored logic in the most correctness-critical, most latency-sensitive path in the product, where a runaway expression stalls a checkout queue and a subtly wrong rule misprices thousands of transactions before anyone notices. Debugging a customer-authored script at 9am on a Saturday is a worse problem than shipping a release.

The closed set will be under continuous pressure to grow. Growth is fine; it should just be a decision each time rather than an escape hatch designed in from the start.
