# ADR 053 — Quantity is matched against the receipt, price against the order

**Status:** Accepted · **Date:** 2026-07-22 · **Phase:** 7

## Context

Three-way matching exists to stop a business paying for goods it did not receive, at prices it did not agree. Both halves are needed, and each has a correct reference document. Getting them the wrong way round produces a system that matches happily and pays for the wrong things.

## Decision

- **Quantity** is checked against the **goods receipts**, summed per order line across every partial delivery. What was ordered is irrelevant to whether a bill should be paid; what arrived is the only thing that matters. Matching quantity against the order is a two-way match wearing a third document as a hat, and it pays in full for a short shipment.
- **Price** is checked against the **purchase order**. The agreed price is on the order. Matching price against the delivery note lets a supplier reprice unilaterally by writing a different number on a docket.

Two cases are decided explicitly:

**Nothing received at all** is always a block, regardless of tolerance. Tolerance absorbs measurement noise; it does not absorb goods that do not exist.

**Tolerances are asymmetric.** They apply only where the supplier billed *more* than expected. Being undercharged is not a control failure, and blocking payment because a supplier charged too little would be an odd use of anyone's afternoon.

A match that only passed because of tolerance is reported as `MatchedWithinTolerance`, distinct from `Matched`. This is not pedantry: a supplier whose invoices sit permanently at 1.9% over under a 2% tolerance is a commercial problem, and it is completely invisible if both outcomes are reported as "matched".

Invoices are recorded **before** they are matched. A disputed bill is still a bill, and one that exists only after it passes matching cannot be aged, chased, or reported on.

Blocked invoices cannot be approved for payment. They are resolved by a credit note, a corrected receipt, or a deliberate `OverrideBlock` that records the person and a mandatory reason. Overrides exist because reality does — a supplier's three-cent rounding difference is not worth a week of correspondence. The control is not that overrides are impossible, it is that they are attributable, so "who has been waving invoices through" is a report rather than an investigation.

## Consequences

`SupplierInvoiceNumber` is unique per supplier per company, enforced by index. The commonest expensive mistake in accounts payable is paying the same invoice twice, and it is a database constraint, not a business rule anyone should be asked to remember.

`ThreeWayMatcher` is a pure static function over the three documents. It has no clock and no database, so every combination of variance can be tested directly.

Matching runs against receipts passed in by the caller. Loading the right set is the application layer's job, and passing an incomplete set produces a false block. That is the safe direction to fail, but it makes the query that assembles them worth getting right.
