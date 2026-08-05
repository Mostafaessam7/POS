# ADR 038 — Multi-tender by default, and which instruments may be taken offline

**Status:** Accepted · **Date:** 2026-07-21 · **Phase:** 5

## Context

Split payment is the norm in retail, not an edge case: cash plus card, gift card plus cash, loyalty points plus card. A single `PaymentMethod` column on the sale is the most common early modelling mistake and always requires a painful retrofit.

Separately, an offline terminal must decide what it may accept. Cash is self-evidently safe. Gift cards, loyalty points, and store credit are balance-bearing instruments whose balance is held centrally.

## Decision

A sale holds a collection of `Tender` records from the outset. The balance due is the total less the sum of tenders.

Overtender — taking more than the balance and returning the difference as change — is permitted for **cash only**. Card and gift card overtender are rejected in the domain.

Each tender method declares whether it requires connectivity. Cash does not. Gift cards, loyalty points, store credit, vouchers, and bank transfers do, and are refused on a disconnected terminal.

## Consequences

Split tender works from day one with no retrofit, and change calculation has one code path.

Refusing cash overtender restrictions on cards closes a real fraud channel: taking 200 on a card against a 20 basket and handing back 180 in cash is a money-laundering and refund-fraud pattern, and card scheme rules prohibit it. Permitting it on a gift card converts a restricted instrument into cash.

Refusing balance-bearing instruments offline prevents double-spend, where the same gift card is redeemed on two disconnected terminals and both syncs are accepted. The cost is a genuine functional gap during an outage, which merchants must be told about; the alternative is unrecoverable financial loss, since the goods have already left.

Offline card acceptance via floor limits is deliberately left open and deferred to Phase 6, where the payment provider integration makes the risk assessment concrete.
