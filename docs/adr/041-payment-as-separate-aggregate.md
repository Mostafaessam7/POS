# ADR 041 — A payment is a separate aggregate, not part of the sale

**Status:** Accepted · **Date:** 2026-07-22 · **Phase:** 6

## Context

A sale already records tenders: "the customer is paying 40.00 by card". It is tempting to let that tender line carry the authorisation code and the settlement status, keeping everything about one transaction inside one aggregate.

The two objects have different lifetimes and different owners. A tender records the merchant's *intent*; a payment records what an acquirer *did*. They diverge routinely: a card authorises and the customer abandons the basket, a sale completes offline while the payment settles three days later, a refund is issued against a sale that has already been archived, a settlement file arrives a week after the shift closed. A payment's state is advanced by an external system on its own schedule, long after the sale is immutable (ADR 007).

## Decision

`Payment` is its own aggregate root with its own lifecycle, referencing `SaleId` loosely and with no foreign key. It is the same structural choice already made for `FiscalDocument` in ADR 033, for the same underlying reason: the sale must not be coupled to the availability or timing of a third party.

A refund is a new `Payment` of kind `Refund` linked to the original, never a mutation of it. The original is the evidence of what the customer was actually charged, and it is the record a chargeback is argued from.

## Consequences

Sale completion no longer depends on a payment reaching a terminal state, which is what allows offline selling to work at all.

The cost is real and is now paid twice over: the database cannot enforce the sale-to-payment relationship, so a **reconciliation report is mandatory rather than optional**. This platform now requires three of them — Sale↔FiscalDocument, Sale↔Payment, and payment↔settlement. None is built yet. That is the standing debt of the loose-coupling strategy and it should be discharged before go-live, not after.
