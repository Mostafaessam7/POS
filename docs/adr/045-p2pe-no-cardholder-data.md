# ADR 045 — No cardholder data, enforced by the build

**Status:** Accepted · **Date:** 2026-07-22 · **Phase:** 6

## Context

PCI-DSS scope is a step function. A system that never touches a primary account number qualifies for SAQ P2PE, a short self-assessment. A system that processes or transmits one — storage is not required to trigger it — needs a full Report on Compliance: an audited annual engagement costing tens of thousands, applied to every customer of the platform.

The distance between those two worlds is one field, added by someone who wanted to display the last four digits and happened to have the whole number to hand. That change looks like a display concern and passes code review.

## Decision

Card data is read and encrypted inside a P2PE-certified device. The application handles only an opaque `EncryptedPayload` it cannot decrypt, plus a masked last-four value, which is explicitly not cardholder data under PCI-DSS. There is nowhere in `PaymentRequest`, `PaymentInstrument` or `CardReadResult` to put a PAN.

This is enforced by `CardDataArchitectureTests`, which fails the build on identifiers such as `Cvv`, `TrackData` or `ExpiryMonth`, and on any long numeric literal that passes a Luhn check.

The detector was narrowed after its first run: a naive "13-to-19 digits" rule flagged the EAN-13 barcodes in `BarcodeTests`. In a retail codebase, long numeric literals are ordinary domain data, and a check that cries wolf on product codes is suppressed within a week and then protects nothing. Requiring fourteen digits *and* a valid Luhn checksum separates card numbers from barcodes.

## Consequences

Compliance scope is defended by an executable rule rather than a paragraph in a wiki nobody reads. The cost is a constraint on hardware procurement — only P2PE-certified readers are usable — and that constraint is the single largest determinant of compliance cost, so it is worth accepting deliberately rather than discovering later.

The rule also forbids test card numbers in the repository. Even a well-known test PAN normalises card numbers in source and is the first thing an automated scanner flags.
