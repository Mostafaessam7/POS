# ADR 019 — Every product has at least one variant

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 3

## Context

Most catalogues contain a mix of simple products (a can of beans) and variant products (a shirt in three colours and five sizes). Modelling these as two shapes is the obvious approach.

## Decision

There is one shape. Every product has at least one variant; simple products have exactly one. Stock, barcodes, and prices attach to the variant, never to the product.

## Consequences

Avoids two code paths through pricing, stock, barcodes, and reporting forever, where the second path is always the buggy one. The cost is a single wrapper row per simple product, which is trivial against a permanent dual model.
