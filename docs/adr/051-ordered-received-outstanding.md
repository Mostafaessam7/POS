# ADR 051 — A purchase order line carries three quantities, never a received flag

**Status:** Accepted · **Date:** 2026-07-22 · **Phase:** 7

## Context

Real deliveries are partial. A supplier ships 47 of 50 and promises the rest, then sends 2 more and discontinues the line. Any model that stores "received: true/false" per line cannot express that sequence, and any model that stores only a received quantity cannot distinguish "3 still coming" from "3 never coming".

The distinction matters commercially. Outstanding quantity drives replenishment: an order that is really finished but looks open suppresses reordering, and stock runs out while the system believes it is inbound.

## Decision

Each line carries **ordered**, **received**, and **cancelled**, all as quantities.

```
Outstanding    = max(0, ordered − received − cancelled)
Over-received  = max(0, received − ordered)
```

Both are derived, so they cannot disagree with the facts they are derived from.

Closing a short shipment is an **explicit act**: `CancelOutstanding(lineNumber, reason)`, with a mandatory reason. The order never quietly decides it is finished. Somebody states that the last three are not coming and why, and the difference between "supplier discontinued the line" and "we changed our mind" is preserved.

Over-receipt is permitted within a `ReceiptTolerance` expressed as **either** a percentage **or** an absolute number of units, whichever is satisfied. Small orders need the absolute bound — 5% of 10 units is half a unit and rounds to refusing everything — and large orders need the percentage. A receipt exceeding both is refused outright rather than accepted-and-flagged: goods can be turned away at the door, and a supervisor override at the point of receipt is a better control than a report generated after the lorry has left.

An order status of `PartiallyReceived` versus `Received` is derived from the lines, so it cannot drift.

## Consequences

A receipt must be validated against the order **as a whole** before any of it is applied, because the tolerance check depends on quantities already received on earlier deliveries. `GoodsReceipt.Post` therefore validates every line first and mutates nothing until all pass. A half-applied receipt is worse than a rejected one: the second is a message to the storeman, the first is a stock count nobody can explain.

Over-received units enter stock at the delivered cost, and the excess appears on the invoice match as a quantity the order did not authorise. That is deliberate — the goods are physically present and must be in the balance, and the commercial argument happens on the invoice.

Cancelling outstanding quantity is refused on lines that have not been sent, and cancelling a whole order is refused once anything has been received. Received goods are a fact; an order that produced them cannot be made to have never existed.
