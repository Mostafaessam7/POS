# ADR 036 — Tax rounding rule is configuration, and totals must reconcile exactly

**Status:** Accepted · **Date:** 2026-07-21 · **Phase:** 5

## Context

Two independent problems meet in the same code.

First, where tax rounding happens changes the answer. Rounding tax per line and summing gives a different total from summing exact line tax and rounding once per rate. Jurisdictions genuinely differ on which is required, and most statutory invoice formats carry both a tax summary and line detail that must agree.

Second, discounts and percentages produce amounts that do not divide evenly. A 10.00 order discount across three lines is 3.333… each; rounding independently yields 9.99 and loses a cent. That cent surfaces as a receipt whose total does not match the sum of its lines, a fiscal document rejected on submission, and a drawer that will not balance.

## Decision

`TaxRoundingRule` is configuration on the company — `PerLine` or `PerTaxRate` — never inferred from a country code, consistent with the fiscal design in ADR 031. Under `PerTaxRate`, rounded totals are redistributed back across the contributing lines so line detail still sums to the summary.

All proportional distribution goes through `Money.Allocate`, which uses largest-remainder distribution in minor units and is guaranteed to sum exactly to the input.

Order-level discounts are pushed DOWN onto the lines rather than held at order level, because tax is computed per line; a discount invisible to the lines would charge tax on an amount the customer never paid.

The pipeline asserts that the sum of lines plus the rounding adjustment equals the sale total, and refuses to price a basket where it does not.

Cash rounding for currencies without small denominations applies to the payable total only, and is held as its own field. The invoice and tax totals stay exact.

## Consequences

Receipts reconcile by construction rather than by inspection. The failure mode changes from a silent one-cent drift discovered by a merchant weeks later to an immediate, loud refusal at the point the arithmetic first disagrees.

Two rounding modes mean two code paths, and `PerTaxRate` requires a redistribution pass. Property-style tests over awkward baskets are worth more here than example-based ones, and the test suite reflects that.

Cash rounding as a separate field means it must be carried through the fiscal document and the receipt template rather than quietly folded into a total.
