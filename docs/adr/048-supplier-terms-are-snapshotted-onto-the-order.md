# ADR 048 — Commercial terms are snapshotted onto the order, not read through to the supplier

**Status:** Accepted · **Date:** 2026-07-22 · **Phase:** 7

## Context

A supplier carries payment terms, a lead time, and a minimum order value. A purchase order needs all three: to compute an expected delivery date, to compute a due date on the eventual invoice, and to validate the order at submission.

The obvious modelling is a foreign key and a join. It is also wrong, and wrong in a way that only shows up months later. Terms change — a supplier moves from 30 days to 45, or a lead time lengthens after a factory move. If the order reads through to the supplier, every historical order silently acquires the new terms. An invoice raised against a year-old order is then measured against terms that did not exist when the order was placed, and the supplier is right and the system is wrong.

## Decision

`PurchaseOrder.Raise` copies the supplier's `SupplierTerms` onto the order as `AgreedTerms`, and everything downstream reads the copy. The expected delivery date is computed once, at the moment of raising, from the lead time in force that day.

Currency is treated the same way but more strictly: it is inherited from the supplier and an order **cannot** override it. A purchase order in a currency the supplier does not trade in is not a business case, it is a mistake, and the place to change the currency is the supplier record.

`Supplier` is its own aggregate rather than a lookup table on the order because it has behaviour and its own invariants — one supplier product code per variant, deactivation instead of deletion — and because supplier records are edited by different people, at different times, from the orders that reference them.

## Consequences

The same terms are stored on every order. That is duplication, and it is the point: these are historical facts about an agreement, not a current attribute of a party.

A correction to terms entered wrongly does not propagate to orders already raised. Fixing those means amending each order, which is tedious and honest. The alternative — silent retroactive change — is neither.

`Supplier` is scoped to tenant and company but **not** to branch. The relationship is with the legal entity; branches order against it. This means a supplier is visible across every branch of a company, which is what people expect and what makes consolidated spend analysis possible.
