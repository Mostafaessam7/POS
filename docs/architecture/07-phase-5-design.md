# Phase 5 — Checkout, Pricing, and Tax

**Status:** Implemented · **Date:** 21 July 2026 · **ADRs:** 034–040

The highest-traffic, most latency-sensitive, most correctness-critical code in the
product. Everything from Phases 0–4 converges here.

## The invariant everything else serves

> The sum of the lines equals the sale total, exactly, on every basket.

A one-cent drift becomes a receipt that disputes itself, a fiscal document a tax
authority rejects, and a drawer that will not balance. The pipeline asserts this
before returning and refuses to price a basket where it fails — a loud error at the
moment arithmetic first disagrees, rather than a silent discrepancy a merchant finds
weeks later.

## Module layout

```
src/Modules/Sales/
├── POS.Sales.Domain/     Sale, SaleLine, Tender, Shift, CashMovement, ReceiptNumber
└── POS.Sales/            PricingPipeline + seven stages
```

Domain depends only on SharedKernel. The pricing engine lives in the application
layer and hands results back through `Sale.ApplyPricing(IReadOnlyList<LinePricing>)`,
a domain-owned DTO — so the dependency points inward and no caller outside the domain
can set a line total directly, bypassing the pipeline.

## Pricing — an ordered pipeline (ADR 034)

Seven stages in a canonical, enforced order:

| # | Stage | Notes |
|---|---|---|
| 1 | Base price | Extended price, recorded in the trace |
| 2 | Line discount | Manual, permission-gated; records **who authorised it** |
| 3 | Promotion | Data-driven, priority order, exclusivity flag |
| 4 | Order discount | Allocated DOWN to lines via `Money.Allocate` |
| 5 | Coupon | Same mechanism as order discount |
| 6 | Tax | Inclusive and exclusive; per-line or per-rate rounding |
| 7 | Rounding | Cash rounding of the payable total only |

Order is not arbitrary. Line discounts precede promotions so a manual markdown does
not stack unexpectedly. Order discounts follow line-level so the percentage applies
to the already-discounted subtotal. Tax is second to last because it applies to the
net actually charged. Rounding is last because it belongs to the total, not a line.

Every stage appends a `PriceAdjustment` — sequence, stage, description, amount,
source, authorising principal. A manager facing a disputed price reads the trace
instead of being told "that is what the system says". Discount frequency by operator
becomes a standing shrinkage report.

Stages are pure over a snapshotted context. None may read the clock or query live
catalog data, so the same basket always produces the same total.

## Tax — the two things usually got wrong

**Inclusive pricing is not a display concern.** A shelf price of 11.50 including 15%
tax has net 11.50 ÷ 1.15 = 10.00 and tax 1.50. Computing 11.50 × 0.15 = 1.73
overstates tax on every inclusive-priced line, which is most of European and Middle
Eastern retail. Both directions are implemented and tested against each other.

**Rounding rule is configuration, not a country check** (ADR 036). `PerLine` or
`PerTaxRate` on the company, consistent with the fiscal design in ADR 031 — behaviour
chosen by data, never by inspecting where the customer is. Under `PerTaxRate`,
rounded totals are redistributed back across contributing lines so line detail still
sums to the summary, which most statutory invoice formats require.

Order discounts are pushed **down** onto lines rather than held at order level,
because tax is per line; a discount invisible to the lines would charge tax on an
amount the customer never paid.

## Sale state machine (ADR 037)

```
Open ──► Suspended ──► Open ──► Completed ──► Voided
  │                              (by reversing document)
  └──► Cancelled
```

`Cancelled` and `Voided` are deliberately distinct: "never happened" versus "happened
and was undone" is exactly what an auditor asks, and merging them destroys the answer.

**Suspend/resume is exclusive ownership transfer, not replication.** This resolves a
real collision with ADR 004: transactional data flows UP only, but a sale resumed on
another till must flow DOWN. Modelling it as a lease — exactly one owning terminal at
any moment, ownership transferred and never copied — means there are never two
divergent versions and nothing to merge. Cross-terminal resume needs connectivity for
central arbitration; same-terminal resume works fully offline.

## Tender (ADR 038)

Multi-tender from the outset. Overtender is **cash only** — card overtender returning
cash is a laundering and refund-fraud pattern prohibited by scheme rules, and on a
gift card it converts a restricted instrument into cash.

Balance-bearing instruments (gift card, loyalty, store credit, voucher) require
connectivity and are refused offline, because the same gift card redeemed on two
disconnected tills is unrecoverable loss — the goods have already left.

## Shift (ADR 039)

`BusinessDate` is fixed once at shift open and inherited by every sale. Cash movements
are signed deltas, summed for the expected drawer position — the same shape as the
stock ledger, for the same reason. Close is **blind**: the counted amount is entered
without showing the expected figure, or the count agrees with the system rather than
the drawer. Variance is recorded, never corrected.

## Deliberate deferrals

| Item | Reason |
|---|---|
| Layaway / partial payment over time | Needs the Payments module (Phase 6) |
| Coupon stage implementation | Same mechanism as order discount; no distinct logic yet |
| Receipt templating | Presentation concern; the data it needs is complete |
| Sale read model projection | Designed in ADR 040, built with reporting |
| Offline card floor limits | Risk assessment belongs with the provider integration |
