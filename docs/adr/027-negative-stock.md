# ADR 027 — Negative stock is permitted by default

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 4

## Context

When on-hand reads zero and a cashier scans the item, the system can refuse the sale or record it and go negative. The instinct to refuse is strong and, for a POS, wrong.

## Decision

Selling into negative stock is permitted by default. The policy is configurable per company (`Allow` / `Warn` / `Block`) and defaults to `Allow` for sales and `Warn` for back-office movements.

## Consequences

The customer is standing at the till holding the item; it physically exists. If the system says zero, the system is wrong — an unbooked delivery, a mis-entered count, an unreceived transfer. Refusing does not produce accurate data, it loses a sale, creates a queue, and teaches staff to work around the system, which ends with a "miscellaneous item" button absorbing several percent of revenue and no usable stock data at all.

Negative balances become an exception report rather than an error, and are genuinely useful: a negative balance is a reliable indicator of an unbooked delivery. A filtered index keeps that report cheap.

`Block` remains available for controlled or serialised high-value goods. It costs a read before every movement on that path, which is why it is not the default.

Offline terminals never enforce `Block`, because they cannot know the true balance; the policy is applied server-side at ingest where the real balance is known. Enforcing it against stale local data would reject valid sales for stock the store genuinely has.
