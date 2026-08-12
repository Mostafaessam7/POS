# Architecture Decision Records — index

57 ADRs, grouped by the area of the system they govern. Each title below links to the
full record. See [HANDOVER.md](../../HANDOVER.md) for how these decisions play out in
the current codebase, and [docs/architecture/](../architecture/) for the supporting
design docs (coding standards, DB conventions, API design, ERD, sequence diagrams).

## Foundations — structure, tooling, identity

| # | Decision |
|---|---|
| [001](001-solution-structure.md) | Two projects per module |
| [002](002-modular-monolith.md) | Modular monolith over microservices |
| [003](003-three-tier-topology.md) | Three-tier terminal, store, cloud topology |
| [004](004-sync-direction-asymmetry.md) | Master data flows down, transactional data flows up |
| [005](005-offline-identity.md) | UUID v7 for machine identity, gap-free sequences for fiscal numbering |
| [009](009-no-repository-pattern.md) | No repository pattern over EF Core |
| [010](010-assertion-library.md) | Shouldly rather than FluentAssertions |
| [011](011-no-mediator.md) | No mediator library |
| [018](018-protocol-versioning.md) | Sync protocol is versioned from the first message |

## Tenancy, auth, identity

| # | Decision |
|---|---|
| [006](006-tenancy-model.md) | Tenant is a security boundary; company/branch/warehouse are authorization boundaries |
| [012](012-argon2id.md) | Argon2id for password hashing |
| [013](013-permission-model.md) | Permission-based authorization with versioned cache lookup |
| [014](014-terminal-identity.md) | Terminals are principals, separate from users |
| [015](015-pin-authentication.md) | PIN authentication for cashiers |
| [016](016-offline-authorization.md) | Signed offline permission bundle |
| [017](017-business-date.md) | Business date is assigned, never derived from the calendar |

## Catalog & pricing

| # | Decision |
|---|---|
| [019](019-product-variant-model.md) | Every product has at least one variant |
| [021](021-barcode-as-entity.md) | Barcode is an entity, unique per tenant, filtered on soft delete |
| [022](022-category-materialised-path.md) | Materialised path for the category hierarchy |
| [023](023-price-versioning.md) | Prices and tax rates are versioned and date-effective |
| [024](024-commercial-rounding.md) | Round half away from zero, in one place |
| [034](034-pricing-pipeline.md) | Pricing as an ordered pipeline with a traceable adjustment record |
| [035](035-promotions-as-data.md) | Promotions are data with a closed set of effects, not a scripting language |
| [036](036-tax-rounding-and-reconciliation.md) | Tax rounding rule is configuration, and totals must reconcile exactly |

## Inventory & stock

| # | Decision |
|---|---|
| [007](007-financial-immutability.md) | Financial records are immutable |
| [008](008-stock-ledger.md) | Append-only movement ledger with a materialised balance |
| [020](020-costing-method.md) | Weighted average cost |
| [025](025-signed-deltas.md) | Stock movements are signed deltas, never absolute balances |
| [026](026-split-path-concurrency.md) | Split concurrency by whether a movement changes cost |
| [027](027-negative-stock.md) | Negative stock is permitted by default |
| [028](028-two-leg-transfers.md) | Transfers are two movements through an in-transit location |
| [029](029-stocktake-adjusts.md) | Stocktakes produce adjustments, never overwrite balances |
| [047](047-value-only-stock-movements.md) | Value-only stock movements are a distinct kind, not a quantity movement of zero |

## Purchasing

| # | Decision |
|---|---|
| [030](030-landed-cost.md) | Landed costs are apportioned into unit cost by largest remainder |
| [048](048-supplier-terms-are-snapshotted-onto-the-order.md) | Commercial terms are snapshotted onto the order, not read through to the supplier |
| [049](049-late-landed-costs-restate-what-remains-and-expense-the-rest.md) | A late landed cost revalues the stock still held and expenses the rest |
| [050](050-purchase-approval-thresholds-are-data-controls-are-invariants.md) | Approval thresholds are configuration; separation of duties is an invariant |
| [051](051-ordered-received-outstanding.md) | A purchase order line carries three quantities, never a received flag |
| [052](052-receipts-yield-instructions-not-inventory-types.md) | Posting a receipt yields plain instructions, not Inventory objects |
| [053](053-three-way-match-asymmetry.md) | Quantity is matched against the receipt, price against the order |
| [054](054-return-and-credit-note-are-separate-facts.md) | A supplier return and its credit note are separate facts |

## Sales, payments, fiscal

| # | Decision |
|---|---|
| [031](031-pluggable-fiscalisation.md) | Fiscalisation as a pluggable capability |
| [032](032-clearance-versus-offline.md) | Clearance-model fiscalisation versus offline-first selling |
| [033](033-fiscal-document-separate-from-sale.md) | The fiscal document is a separate aggregate from the sale |
| [037](037-sale-state-and-suspend.md) | Sale state machine, and suspend/resume as exclusive ownership transfer |
| [038](038-multi-tender-and-offline-instruments.md) | Multi-tender by default, and which instruments may be taken offline |
| [039](039-shift-and-cash-accountability.md) | Shift as the business-date anchor and the unit of cash accountability |
| [040](040-sale-read-model.md) | Sale history is served from a read model, not by rehydrating aggregates |
| [041](041-payment-as-separate-aggregate.md) | A payment is a separate aggregate, not part of the sale |
| [042](042-write-ahead-payment-record.md) | The payment record is committed before the provider is called |
| [043](043-terminal-generated-idempotency-keys.md) | Idempotency keys are generated on the terminal |
| [044](044-indeterminate-is-not-failed.md) | An unknown payment outcome is its own state, not a failure |
| [045](045-p2pe-no-cardholder-data.md) | No cardholder data, enforced by the build |

## Expenses

| # | Decision |
|---|---|
| [055](055-expenses-are-small-and-capitalisation-is-a-closed-list.md) | Expenses stay small, and only freight and duty may reach stock |

## Cross-cutting, infrastructure, delivery

| # | Decision |
|---|---|
| [046](046-executable-baseline-deferred.md) | The executable baseline is deliberately deferred, and is the highest-priority milestone before production readiness |
| [056](056-infrastructure-blocked-by-environment.md) | The infrastructure milestone is blocked by the environment, not by the design |
| [057](057-cross-module-writes-are-eventually-consistent.md) | Cross-module writes are eventually consistent, not transactional |
