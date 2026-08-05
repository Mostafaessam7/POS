# ADR 029 — Stocktakes produce adjustments, never overwrite balances

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 4

## Context

A count of 5 against a system quantity of 7 can either set the balance to 5 or record an adjustment of −2.

## Decision

Counts produce adjustment movements through the ledger. The balance is never set directly. Blind counting — where the counter cannot see the expected quantity — is supported and recommended.

## Consequences

The variance survives, and the variance is the entire commercial value of counting: it is how shrinkage is measured, and shrinkage typically runs at 1–2% of retail revenue. A stocktake that silently corrects balances tells the merchant nothing and conceals theft.

Blind counting is a human control rather than a technical one. Shown the expected number, a counter under time pressure confirms it instead of counting; a blind count measures reality, a visible count measures agreement.

Because the ledger stores deltas (ADR 025) and the variance is computed against the balance at the moment of count entry, counting during trading works without freezing the store — sales during the count are simply later movements. A snapshot-and-replace design would swallow them.

The expected quantity observed at count time is stored on the line rather than recomputed at posting, because the balance legitimately moves in between and the variance the counter saw is the one that must remain explainable.
