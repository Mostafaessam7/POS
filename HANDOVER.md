# HANDOVER

Complete state of the POS SaaS platform after the infrastructure-execution milestone,
updated through the transfers-and-stocktakes, operator-key-provisioning,
variance-write-off-approval, production-readiness-hardening, and back-office-completeness
milestones that followed it. Every module builds, migrates, runs, and is verified
end-to-end against a real SQL Server instance. This document supersedes all prior
handover notes — the codebase now compiles, runs, and is tested; treat any claim in an
older document that contradicts this one as stale.

**Back-office-completeness milestone, in brief**: user/role management (invite,
custom roles, assign/revoke at a scope) went from domain-only to a real HTTP surface and
screen; the Expenses, Reconciliation, and Purchasing invoices/returns modules went from
"tested backend, no UI" to full screens; a product's name can finally be edited (the
previous document's claim that an update endpoint existed was wrong — it didn't); and
the refresh token moved out of `localStorage` into an `HttpOnly` cookie with a real
`POST /auth/logout` that revokes it server-side, closing the one XSS-exposure gap this
document had flagged since the frontend milestone. 336 unit / 15 architecture / 136
integration tests, all passing, zero build warnings.

**If you take one thing from this document: the whole system was never run until this
milestone. Every fact below was verified by executing code against a real database, not
inferred from reading it.** Where a bug was found by running previously-untested code,
it is called out explicitly, because the same class of bug is likely wherever
`dotnet ef migrations add` has not yet been run against real data.

**Re-verified 2026-08-23** (no code changes since 2026-08-05 — this was a from-scratch
re-run, not a new milestone): `dotnet build POS.sln -c Release` clean, `POS.UnitTests`
336/336, `POS.ArchitectureTests` 15/15, `POS.IntegrationTests` 136/136 against a real SQL
Server, `npm run build` clean. Every number below still holds. Two things changed
without any code change on this side: `npm audit` now reports 0 vulnerabilities (§9
known-shortcuts item 2, previously an accepted advisory); and this pass found two
frontend files with no corresponding documentation anywhere in this file — an animated
login/dashboard background and a dashboard count-up animation, both purely cosmetic,
now recorded in §9.

---

## 1. Architecture

**Modular monolith**, nine business modules plus shared kernel, one process (`POS.Api`),
one SQL Server database with **nine schemas** (one per module, own migration history
table — ADR 002). Modules never reference each other's implementation. They share only:

- `POS.SharedKernel` — `Money`, `Result`/`Error`, `Entity`/`AggregateRoot`, `BusinessDate`,
  `IClock`, tenancy marker interfaces. No dependencies.
- `POS.Common` — cross-cutting infrastructure: tenancy enforcement, EF interceptors,
  validation, error-to-HTTP mapping, SQL Server configuration, background-job base class.
  Depends only on SharedKernel.
- `POS.Contracts` — **the seam** through which modules that must cooperate do so without
  referencing each other. Ports (interfaces) + plain DTOs only. A module that needs to
  affect another module implements or consumes a contract interface; the architecture
  test suite fails the build if a module references another module directly.

### Module map

| Module | Domain | Infra | Responsibility |
|---|---|---|---|
| Identity | ✅ tested | ✅ | Tenants, companies, branches, warehouses, terminals, users, roles, permissions, JWT |
| Catalog | ✅ tested | ✅ | Products, variants, barcodes, categories, pricing, tax |
| Inventory | ✅ tested | ✅ | Stock ledger (append-only), materialised balances, weighted-average costing, stock transfers, stocktakes |
| Sales | ✅ tested | ✅ | Sale/Shift aggregates, pricing pipeline, sync ingest orchestration |
| Payments | ✅ tested | ✅ | Payment lifecycle, provider seam, indeterminate resolution, settlement recon (logic only) |
| Fiscal | ✅ tested | ✅ | Country-agnostic fiscalisation pipeline, gap-free numbering, GENERIC profile |
| Purchasing | ✅ tested | ✅ | Suppliers, POs, goods receipts, landed cost, invoices, 3-way match, supplier returns |
| Expenses | ✅ tested | ✅ | Expense recording, approval, capitalisation eligibility |
| Sync | ✅ tested | ✅ | Idempotent terminal upload ingest, master-data pull (full-snapshot pull now implemented — §6 item 15) |
| Reconciliation | ✅ tested | n/a (host-assembled) | Pure functions comparing two modules' projections |

`POS.TerminalAgent` is legacy scaffolding from before this milestone — an unfinished
SQLite-based terminal host, out of scope. `POS.WalkingSkeleton`, the other legacy host
predating real persistence, has been deleted (§6 item 14) — nothing referenced it.

### Cross-module contracts (POS.Contracts) — the pattern to follow for anything new

Each contract is a **port** (interface) implemented by the owning module and an
**adapter** (anti-corruption layer) that translates. The consuming module depends only
on the interface and DTOs.

| Contract | Owner (implements) | Consumer | Purpose |
|---|---|---|---|
| `IStockPostingPort` | Inventory (`StockPostingAdapter`) | Purchasing, Sales | Post a receipt/return/sale's stock effect |
| `IFiscalisationPort` | Fiscal (`FiscalisationAdapter`) | Sales | Issue a fiscal document for a completed sale |
| `IPaymentRecordingPort` | Payments (`PaymentRecordingAdapter`) | Sales | Record a sale's electronic tenders as payments |
| `ICompanyDirectory` | Identity (`CompanyDirectory`) | Fiscal | Read a company's fiscal identity (profile code, country, tax reg) |
| `ITenantSettingsDirectory` | Identity (`TenantSettingsDirectory`) | Purchasing, Inventory | Read a tenant's stored policy override, if any (§6 item 16) |

Two more pairs are registered-implementation seams **owned by Sync, not POS.Contracts**
— the same pattern, but Sync itself defines the interface and modules reference
`POS.Sync` directly (a deliberate, narrow exception to "modules never reference each
other" — see `IMasterDataSource`'s remarks): `ISyncRecordHandler` (Sales implements
`SaleSyncHandler` for the upload direction) and `IMasterDataSource` (Catalog implements
`ProductMasterDataSource` for the pull direction — §6 item 15).

**Critical pattern — cross-module writes are eventually consistent, not transactional.**
There is no distributed transaction across module DbContexts. Safety comes from two
rules applied together everywhere this pattern is used:

1. **Ordering**: the *external* effect (stock movement, fiscal document, payment) is
   written and committed **before** the *local* record is marked complete/saved.
2. **Idempotency**: the adapter recognises a repeat call for the same document/sale id
   and returns success without re-applying. Keyed on the caller-supplied id (receipt id,
   sale id, or sale+tender-sequence for payments).

This means a crash between the two writes leaves the external effect applied and the
local record NOT YET marked done — which is self-healing on retry (retry re-derives the
identical effect, the adapter no-ops, the local record then saves) rather than the
alternative ordering, which would leave a "done" record whose external effect silently
never happened. See `GoodsReceiptPostingService`, `SupplierReturnDispatchService`,
`SaleSyncHandler`, `StockTransferService`, `StocktakeService` for the reference
implementations. **Now ratified as ADR 057** (`docs/adr/057-cross-module-writes-are-
eventually-consistent.md`) — previously only documented in code comments.

### Tenancy (ADR 006)

Three enforcement layers, all mandatory for every entity implementing `ITenantScoped`:

1. **Query filter** — `TenantQueryFilter.ApplyTo` in every DbContext's
   `OnModelCreating`, applied by reflection over every entity implementing
   `ITenantScoped`/`ISoftDeletable`. New entities are filtered automatically.
2. **Write guard** — `TenantGuardInterceptor` rejects any write carrying a foreign
   `TenantId`.
3. **Enforcement test** — `TenantIsolationArchitectureTests` fails the build if any
   `DbContext.OnModelCreating` omits the filter call. (Caught Inventory once already.)

**Important subtlety (found and fixed this milestone):** the tenant filter expression
must be rooted at the `DbContext` instance (`PosDbContext.CurrentTenantId`), never at an
injected `ITenantContext` closed over in a lambda, and never at `AsyncLocal` ambient
state. EF caches the compiled model per context **type** for the process lifetime; a
filter closing over anything except the live context instance either serves stale data
forever or (with `AsyncLocal`) leaks state across concurrent in-process requests. Both
failure modes were hit and fixed during this milestone — see `PosDbContext.cs` for the
full explanation. **Do not "simplify" this back to an injected context.**

Tenant resolution: `TenantResolutionMiddleware` reads a signed `tenant_id` JWT claim
**only** — never a header, route value, or subdomain. A route-supplied tenant id is
validated against the token and mismatches return 404 (not 403 — avoids confirming
resource existence across a tenant boundary).

### Authentication / Authorization

- **JWT bearer**, HMAC-SHA256, issued by `TokenService` (Identity module). Access token
  carries `tenant_id`, `terminal_id`, `branch_id`, `company_id[]`, `perm_version` claims
  — **not** the permission set itself (would bloat every request; see ADR 013).
- **Permission resolution**: `CachedPermissionResolver`, keyed by `(userId,
  permVersion)`. Bumping `User.PermissionVersion` invalidates the cache instantly on the
  next request — this is the whole revocation mechanism, no distributed cache yet
  (single-instance assumption, flagged as a scale gap in ADR 013).
- **Endpoint gating** is two-part, both required for any endpoint acting on a specific
  scope:
  1. `RequirePermission("module.resource.action")` — coarse gate via
     `PermissionPolicyProvider`, a custom `IAuthorizationPolicyProvider` that builds a
     policy per permission code on demand (no per-permission policy registration).
  2. `IPermissionScopeGuard.HasAtScopeAsync(code, scopeId)` — checked inside the handler
     against the request's actual branch/company/warehouse id. **Omitting step 2 is a
     real vulnerability** (a user with permission at branch A could act on branch B) —
     every scoped endpoint in Purchasing/Expenses/Inventory does both; follow that
     pattern for anything new.
- **Approval ladders** (Purchasing orders): `IPermissionScopeGuard.HighestHeldAsync`
  resolves the highest of an ordered permission ladder
  (`approve.supervisor`/`.manager`/`.director`), then the *domain* (not the endpoint)
  decides if that level is sufficient for the document's value and whether the raiser
  may self-approve (refused by default; separation of duties, ADR 049/050).
- Refresh tokens: opaque random, hashed at rest, single-use, family-based reuse
  detection (`RefreshTokenService`) — a reused token revokes the whole family.

### Persistence patterns worth knowing before touching an EF configuration

- **EF Core 9 cannot index or `FromSql` a complex-type member.** Three real
  consequences hit and fixed this milestone:
  - `IX_StockMovements_Document`, `UX_Sales_Receipt` — indexes over a complex property
    member (`Reference.DocumentId`, `ReceiptNumber.Sequence`) cannot be declared via
    `HasIndex` in a configuration class. They are **hand-written SQL in the migration
    file**, with a comment explaining why, and the model snapshot does not know they
    exist — a future migration touching those columns must carry the index by hand.
  - `FromSql("SELECT * FROM StockBalances WITH (UPDLOCK, HOLDLOCK) ...")` silently uses
    **default** column naming instead of the mapping's configured names, and fails at
    runtime against a perfectly correct schema. Fix: acquire the lock with a
    zero-projection statement (`SELECT TOP 1 1 ...`), then read the entity through the
    normal LINQ path. See `SqlServerStockLedger.ApplyCostChangingAsync`.
  - `SupplierReturn.CreditedAmount` (`Money?`) — EF complex properties cannot be
    nullable and `Money` is a struct so it can't be an optional owned type either.
    Mapped through a `HasConversion` to a single round-trip-formatted string column.
    This is the **one place in the codebase money is not two typed columns** — documented
    at the mapping with the escape hatch if it ever needs SQL-side aggregation.
- **Records/structs carrying `Money` need an EF materialisation constructor.** EF can't
  bind a complex-typed parameter to a positional-record primary constructor. Pattern:
  add a `private` parameterless constructor chaining to the primary one with `default`
  for the Money parameter. Done for `PriceAdjustment`, `GoodsReceiptLine`,
  `PurchaseInvoiceLine`, `SupplierReturnLine`, `LandedCostCharge`.
- **Execution strategy + user transactions.** `PosSqlServer` enables
  `EnableRetryOnFailure`. Any code opening its own `BeginTransactionAsync` **must** wrap
  it in `Database.CreateExecutionStrategy().ExecuteAsync(...)` or it throws at runtime
  ("does not support user-initiated transactions"). Three real instances of this bug
  were found and fixed: `SqlServerStockLedger.RecordBatchAsync`,
  `EfFiscalSequenceAllocator.NextAsync`, `FiscalisationAdapter` (which also had to
  retry-on-contention because two concurrent sales could both read the same fiscal
  sequence number before either committed — allocation now shares one transaction with
  document issuance so a rollback returns the number instead of burning it, gap-free
  numbering intact).
- **Unique-violation detection must handle both wrapped and raw exceptions.**
  `SaveChangesAsync` throws `DbUpdateException` wrapping `SqlException`;
  `ExecuteSqlInterpolatedAsync` throws `SqlException` **raw, unwrapped**. A single
  `catch (DbUpdateException)` around raw SQL never fires. `POS.Common.Persistence.
  UniqueViolation.Matches` handles both shapes (checks `SqlException.Number` for
  2601/2627, recurses into `InnerException`) and should be the only way this is done
  anywhere in the codebase — three call sites were silently broken before this was
  centralised (`SyncIngestService`, `SqlServerStockLedger`, `SaleSyncHandler`).
- **A new child added to an already-loaded, already-tracked aggregate needs its EF
  state corrected by hand.** Found and fixed this milestone in
  `StocktakeService.RecordCountAsync`: the first count for a variant adds a new
  `StocktakeLine` to a `Stocktake` that was loaded (not newly `Add()`-ed) in this same
  call. EF discovers that new line via change detection on an already-tracked graph,
  not via an explicit `Add()`, and its default heuristic for a reachable entity with a
  non-default, client-generated key it has never seen is `Modified`, not `Added` —
  because it cannot distinguish "new, key assigned by the client" from "already
  exists". The result is an `UPDATE` against a row that was never inserted, which SQL
  Server reports as a concurrency conflict (0 rows affected) rather than the missing
  `INSERT` it actually is. Fix: after calling the domain method, explicitly set
  `db.Entry(newLine).State = EntityState.Added` when the line is known to be new. This
  did **not** affect `StockTransfer.AddLine`, because every call to it happens before
  the transfer's first `Add()` + `SaveChangesAsync` — the whole graph is new, so `Add()`
  cascades `Added` to every line already. The bug is specific to mutating an aggregate
  **after** it has already been persisted and reloaded.

---

## 2. Database

**One SQL Server database** (`PosPlatform` locally; `PosIntegrationTests` for the test
suite), **nine schemas**, each with its own `__EFMigrationsHistory_<Module>` table so
modules deploy independently (ADR 001/002).

| Schema | Tables | Migration |
|---|---|---|
| identity | 14 | `InitialIdentity` + `ProvisioningOperators` + `TenantSettings` |
| catalog | 14 | `InitialCatalog` |
| inventory | 7 | `InitialInventory` + `TransferAndStocktakeCancellation` (columns, not new tables) |
| sales | 7 | `InitialSales` |
| payments | 3 | `InitialPayments` + `PaymentAttemptNumberNotIdentity` |
| fiscal | 4 | `InitialFiscal` |
| purchasing | 13 | `InitialPurchasing` |
| expenses | 2 | `InitialExpenses` |
| sync | 5 | `InitialSync` |

**69 tables total.** All migrations confirmed `in sync` (no pending model changes)
against a live `dotnet ef migrations has-pending-model-changes` check as of this
session. `PaymentAttemptNumberNotIdentity` is hand-written (EF's generated
`AlterColumn` cannot flip a SQL Server `IDENTITY` property in place) — it drops and
recreates the `PaymentAttempts` composite PK, safe because that table had never held a
row before the background-job milestone exercised it for the first time and found the
bug.

**Design-time factories**: every module has a `PosDesignTimeDbContextFactory<T>`
subclass so `dotnet ef` commands work without booting the full host (which would need a
resolved tenant that doesn't exist at design time). Connection string resolution order:
CLI arg → `ConnectionStrings__Pos` env var → `appsettings.json` → LocalDB fallback.

---

## 3. APIs implemented

All routes under `/api/v1/`, all `RequireAuthorization()`, all with FluentValidation on
POST bodies via `AddValidation<T>()`.

| Area | Routes |
|---|---|
| Health | `GET /health` (liveness, anonymous), `GET /health/ready` (checks all 9 DbContexts) |
| Auth | `POST /auth/login` (subdomain + email + password → access token; refresh token set as an `HttpOnly` cookie, never in the body — §6 item 20), `POST /auth/refresh` (rotates the refresh token via the cookie, no body), `POST /auth/logout` (revokes the refresh token server-side and clears the cookie — new, §6 item 20) — all anonymous except logout, all rate-limited under the `auth` policy |
| Organization | `GET /organization` — a tenant's companies/branches/warehouses, for populating a picker instead of pasting a GUID. §6 item 19 |
| Provisioning | `POST /tenants` (gated by a named, individually-revocable operator identity — §6 item 13; now also seeds an Owner role holding every permission and one admin user, so the tenant is actually loggable-into — §6 item 19), `POST/GET /provisioning/operators`, `POST /provisioning/operators/{id}/revoke` (root-key gated — §6 item 13), `POST /terminals`, `GET /tenants/{id}/settings` (both gated by ordinary tenant `RequireAuthorization()`) — all still off by default (`Provisioning:Enabled` config / Development-only) |
| Users & roles | `GET/POST /users` (list, invite — returns a one-time temporary password), `POST /users/{id}/roles` and `.../roles/revoke` (assign/revoke at a scope — self-revocation refused), `GET/POST /roles` (list, create a custom role from permission codes), `GET /permissions` (the full catalogue) — new, §6 item 21 |
| Catalog | Products CRUD (create/list/get, **`PUT` to rename — new, §6 item 22**, `DELETE` to deactivate/soft-delete), barcodes |
| Sync | `POST /sync/batches` (idempotent terminal upload), `POST /sync/master-data/pull` (full-snapshot master-data pull — §6 item 15), `GET /sales/count` |
| Purchasing | Suppliers, orders (raise/approve/send/cancel), goods receipts (create/post), invoices (**list — new**, record/match/approve/override-block), supplier returns (**list — new**, create/dispatch/credit-note) — ~27 endpoints |
| Expenses | Record, approve, reject, list, get |
| Inventory | Warehouse balances (paged), single balance, movement ledger, negative-stock report, per-warehouse ledger reconciliation, manual adjustments (increase/decrease/wastage), stock transfers (create/dispatch/receive/write-off-variance/cancel), stocktakes (start/count/complete-counting/post/cancel) |
| Reconciliation reports | `GET /reports/{receipt-stock, supplier-credit, sale-fiscal, sale-payment}-reconciliation`, `GET /reports/stock-balance-reconciliation` — **all four reconcilers now have real data on both sides and are exposed** |
| Settings | `GET/PUT /settings/purchasing-policy`, `GET/PUT /settings/inventory-policy` — per-tenant policy overrides (§6 item 16) |
| Docs | `GET /openapi/v1.json`, `GET /scalar` — Development-only or via `Api:EnableOpenApiDocs` (§6 item 17) |

JSON enums serialise as **names**, not ordinals (`JsonStringEnumConverter`), configured
globally in `Program.cs`. Every route is additionally behind a rate limiter (§6 item 17)
— a stricter policy for the anonymous Provisioning surface, a generous global default
elsewhere — and a refuse-closed CORS policy with an empty allow-list by default.

---

## 4. Background jobs

Both built on `POS.Common.Jobs.PeriodicJob<TWorker>` — singleton `BackgroundService`,
fresh DI scope per tick, catches and logs per-tick exceptions without killing the loop,
non-overlapping (`PeriodicTimer` waits *after* work finishes). Worker logic is a plain
scoped service so tests call it directly without waiting on a timer.

| Job | Interval (default) | What it does |
|---|---|---|
| `IndeterminatePaymentSweepJob` (Payments) | 5 min | Cross-tenant scan for `PaymentStatus.Indeterminate`; resolves each via `PaymentOrchestrator.ResolveAsync` under its own tenant. Only a definite provider "not found" answer marks a payment failed — an "unknown" answer is left indeterminate for the next sweep (ADR 044: never guess). |
| `FiscalDeadlineMonitorJob` (Fiscal) | 1 min | Cross-tenant scan for documents past `TransmissionDueBy`; logs at `Error` level (compliance breach, should page). Report-only, does not transmit. |

**Not built — blocked on missing counterparties, not on job infrastructure:**
- **Fiscal transmission sweep** — needs a transmitter implementation; GENERIC profile
  (the only one registered) has no transmitter by design (no clearance mandate). Nothing
  to exercise until a country plugin exists.
- **Settlement job** — `SettlementReconciler` is a pure function needing a bank
  settlement file; no import path exists. Blocked on that, not on the job shape.

Both existing jobs are confirmed running (`Background job {name} started`) in a live
`dotnet run` of `POS.Api`.

---

## 5. Testing status

All three suites green from a clean rebuild, run repeatedly for stability:

| Suite | Count | What it proves |
|---|---|---|
| `POS.UnitTests` | 336 | Domain logic in isolation |
| `POS.ArchitectureTests` | 15 | Module boundaries, tenant filter presence, fiscal country-agnosticism, no card data at rest |
| `POS.IntegrationTests` | 136 | Full HTTP → auth → validation → domain → EF → real SQL Server, including background jobs run directly |

Zero build warnings solution-wide (`TreatWarningsAsErrors` is on).

**Numbers integration tests specifically pin down** (regression canaries — if these
change, something real broke):
- Two landed-cost receipts blend to a weighted average of **10.80** (the number
  HANDOVER has always used as the canonical proof the whole costing chain works).
- Fiscal numbers for 5 sales are exactly `[1,2,3,4,5]` — gap-free under concurrent
  upload.
- A receipt whose stock movement is deleted below the ORM (simulating a lost write) is
  reported at its **landed** value (420.00), not goods value (400.00).
- A wastage adjustment leaves the average cost **unchanged**; an increase adjustment
  blends the new cost in correctly.
- The indeterminate-sweep leaves an "unknown" payment untouched and reaches payments
  across two tenants in one pass.
- A transfer dispatched twice moves stock out of the source warehouse exactly once
  (`TransferApiTests`); a short receipt leaves the shortfall sitting in the in-transit
  warehouse until it is explicitly written off, never silently absorbed.
- A stocktake counting 34 against an expected 40 posts a **StocktakeAdjustment of
  exactly −6**, at the prevailing average, and posting the same stocktake twice applies
  that adjustment once (`StocktakeApiTests`).
- Dropping a permission resolution from L1 (in-process) only — simulating a cold
  instance in a multi-instance deployment — still resolves correctly from L2
  (`IDistributedCache`) without touching the database, and invalidation clears **both**
  levels (`DistributedPermissionCacheTests`).
- A tenant that overrides `PurchasingPolicyOptions.ApprovalRequiredAbove` changes what
  its OWN orders do (a previously-approval-requiring order goes straight to `Approved`)
  without affecting a different tenant that never set an override
  (`TenantSettingsApiTests`) — this test caught a real bug: the settings PUT endpoints
  silently no-op'd because `PurchasingPolicyOptions`/`InventoryPolicyOptions` are ALSO
  registered as DI singletons, and minimal API resolves a parameter of a
  DI-registered type from the container instead of the request body unless
  `[FromBody]` forces it. See §6 item 16.
- 40 concurrent lock-free stock movements (wastage adjustments) against the same
  balance row lose zero updates (`StockLedgerConcurrencyTests`) — a real result behind
  ADR 026's design claim, not just an argument for it.
- A pulled master-data snapshot contains an active product's variant and every one of
  its barcodes, and never leaks a different tenant's products
  (`MasterDataPullApiTests`).

**Integration test infrastructure**: `ApiFixture` (`ICollectionFixture`) starts one
`WebApplicationFactory<Program>` + one SQL Server connection for the whole collection.
Connection resolution: `POS_TEST_SQL` env var → local SQL Server probe → Testcontainers
(needs Docker, **not available in this environment**, untested path). Runs real
`Database.MigrateAsync()` per module, not `EnsureCreated` — so the suite is also the
proof the migrations themselves work. `TestPaymentProvider` is a controllable double
registered via `ConfigureTestServices`; the estate registers no real acquirer by design.

**Database reset**: `ApiFixture.InitializeAsync` runs `Respawner` right after
`MigrateAsync`, wiping every row in the nine module schemas (not the per-module
`__EFMigrationsHistory_*` tables — those are excluded so the next run doesn't think
its migrations need re-applying). This closes the gap from the previous milestone,
where the suite tolerated a persistent local SQL Server never being reset only because
every test generates fresh GUIDs. Verified by running the full suite three times in a
row against the same persistent local database — 76/76 every time, including the
"seed permissions if absent" path in `CreateClientWithPermissionsAsync`, which now
always inserts because the table is empty at the start of each run rather than
sometimes finding rows from the previous one.

---

## 6. Key architectural decisions made this milestone (not yet written as formal ADRs)

1. **Cross-module writes are eventually consistent** (external effect commits first,
   idempotency + ordering are the safety net, not a distributed transaction) — see §1.
   Used by five call sites now. **Should become a real ADR.**
2. **`Sale.Open` accepts an optional caller-supplied id.** Changed from always generating
   a fresh UUIDv7 to accepting the terminal-assigned id when replaying from sync. This
   was necessary: without it, a crash-and-retry on sale upload would re-post the stock
   movement under a *new* document id and deplete stock twice (the ledger is
   append-only, so this could never be corrected). Believed to be what ADR 005 (offline
   id assignment) always intended, but it is a change to a previously-tested domain
   aggregate, not purely additive infrastructure — flagged for awareness.
3. **Cash is not a `Payment`.** Only electronic tenders (`TenderMethod != Cash`) go
   through `IPaymentRecordingPort`; cash is drawer/shift accountability, a different
   control with no auth/capture/settle lifecycle. This is a judgement call, not
   something the original domain design stated explicitly — confirm it's the intended
   split before it hardens further.
4. **`PurchasingPolicyOptions` (approval thresholds, receipt tolerance) is
   deployment-wide configuration**, not per-tenant. ADR 049 implies tenant-level
   settings; no tenant settings store exists yet. Every caller already funnels through
   `PurchasingPolicyOptions.ApprovalPolicyFor(currency)`, so migrating to per-tenant
   later is a contained change.
5. **Fiscal numbering serialises per series under a held row lock across the full
   issuance transaction** (allocate + issue in one transaction, not allocate-then-issue
   in two). Correct and gap-free; means concurrent sales *on the same terminal* contend
   briefly. Acceptable because a fiscal series is per-terminal, so cross-terminal
   throughput is unaffected.
6. **The stock-adjustment cost rule is enforced in a module service, not the endpoint**:
   an increase takes an operator-supplied cost; a decrease/wastage *always* consumes at
   the prevailing average — the API validator actively rejects a caller-supplied cost on
   a reduction, because allowing it would be a way to quietly restate margin.
7. **Transfers and stocktakes post to the ledger directly, not through
   `IStockPostingPort`.** `StockTransferService` and `StocktakeService`
   (`POS.Inventory`) call `IStockLedger` the same way `StockAdjustmentService` does,
   because `StockTransfer`/`Stocktake` already live inside the Inventory module — the
   port exists specifically for OTHER modules (Purchasing, Sales) to reach Inventory
   without depending on it directly. The same commit-then-save ordering from item 1
   applies here too: the ledger write commits first, the aggregate's own status change
   saves second, each leg individually idempotent so a retry after a crash between the
   two is safe. Idempotency is keyed on `(document id, movement type, warehouse)`
   rather than document id alone, because a single transfer produces movements at up to
   three different warehouses across its lifecycle (dispatch, receive, write-off) and
   each leg must be independently replayable.
8. **A stocktake's expected quantity is resolved server-side from the live balance,
   never trusted from the caller** — required for blind counting to mean anything
   (a visible expected quantity handed back by the client would let a blind count be
   defeated by simply echoing it), and to stop a non-blind client from lying about it.
9. **A transfer's variance write-off is generic in sign, though the ADR names only the
   shortfall case.** `StockTransfer.WriteOffVariance` permits a positive variance
   (more arrived at the in-transit leg than was ever dispatched — a miscount, not the
   theft case the control targets) as well as a shortfall; `StockTransferService`
   records a shortfall as `Wastage` and a surplus as `AdjustmentIncrease`, both at the
   in-transit warehouse's prevailing cost, so either direction is accounted for.
   **Both paths now have test coverage**: `A_surplus_receipt_can_also_be_written_off`
   (unit — the aggregate itself is sign-agnostic; only the service picks the movement
   type) and `A_surplus_receipt_is_written_off_as_a_found_stock_increase_not_a_wastage`
   (integration — receiving 12 against 10 dispatched drives the in-transit leg to −2,
   permitted under `NegativeStockPolicy` per ADR 027, and the write-off's
   `AdjustmentIncrease` movement brings it back to 0).
10. **`POST /tenants` is gated by a named operator identity, not a platform-operator
    role.** This was a deliberate choice between two real designs, made explicitly with
    the user rather than assumed: a full tenant-less identity (new user store, a token
    shape without `tenant_id`, carve-outs in `TenantResolutionMiddleware`) was judged
    disproportionate to the actual need ("let ops tooling bootstrap a tenant safely")
    and would have touched a foundational invariant — every authenticated request has
    exactly one tenant — that is currently enforced in three independent places
    (`TokenService.Issue`'s non-nullable `tenantId` parameter,
    `TenantResolutionMiddleware`'s hard 403 on a missing claim, and `User`/`Role` being
    `ITenantScoped` with no Platform `ScopeType`) and covered by
    `TenantIsolationArchitectureTests`. `POST /terminals` and
    `GET /tenants/{id}/settings` were deliberately left untouched: they already sit
    behind ordinary `RequireAuthorization()` (a tenant-scoped bearer token), because
    enrolling a terminal or reading settings is a merchant operation performed inside a
    tenant that already exists, not a platform-bootstrap step — conflating the two
    would have been scope creep.
    **This item originally described a single secret shared by every operator
    (`Provisioning:OperatorApiKey`) and flagged that design for replacement, not
    extension, the moment more than one person needed individually revocable access.
    That replacement has now happened — see item 13.**
11. **`TransferWriteOffVariance` became a three-level approval ladder** —
    `Permissions.Inventory.TransferWriteOffVarianceSupervisor/Manager/Director` plus
    `InventoryPolicyOptions.VarianceWriteOffThresholds` — mirroring
    `PurchasingPolicyOptions`/`ApprovalLevel`/`ApprovalPolicy` exactly, deliberately
    NOT by sharing Purchasing's `ApprovalLevel` type: Inventory now has its own
    `ApprovalLevel` enum, `VarianceApprovalPolicy`, and `VarianceApprovalThreshold` in
    `POS.Inventory.Domain`, because the module boundary (ADR 002) means two modules
    independently needing the same shape is not evidence they should share a type — and
    sharing it would have made Inventory depend on Purchasing's domain assembly for a
    single enum. This did surface one genuine cross-cutting gotcha: `PurchasingWorkflowTests.cs`
    and `PurchasingEndpoints.cs`/`InventoryEndpoints.cs` now both import a namespace
    exposing an `ApprovalLevel`, so a handful of previously-unambiguous usages needed a
    full `POS.Purchasing.Domain.ApprovalLevel.X` qualification (including one in the
    otherwise-untouched `POS.WalkingSkeleton` host, since deleted — §6 item 14) — a
    compile error, not a runtime one, so the build catches it immediately if it recurs.
    - **The value the ladder gates on is computed in the SERVICE, not the aggregate.**
      Unlike `PurchaseOrder.TotalValue` (self-contained, computed from the order's own
      lines and prices), a transfer line only ever holds quantities — cost is resolved
      from the prevailing balance at posting time, same as every other movement in this
      module. So `StockTransferService.WriteOffVarianceAsync` loads the variance lines'
      costs and sums `Σ(|variance| × cost)` **before** calling
      `StockTransfer.WriteOffVariance(policy, varianceValue, ...)` — the aggregate still
      enforces the invariant (self-approval, required level), it just cannot compute the
      value itself the way `PurchaseOrder.Approve` can.
    - **Self-approval is checked against `ReceivedByUserId`, not a "raiser".** A transfer
      has no single "raiser" the way a purchase order does; the person who counted the
      shortfall (whoever received the transfer) is the one who must not also be the one
      who writes it off unchecked — that is the actual fraud vector for this control, so
      that is what `AllowSelfApproval` gates.
    - Default thresholds (`0 → Supervisor, 500 → Manager, 5,000 → Director`) mean EVERY
      write-off needs at least a Supervisor's permission, unlike Purchasing's separate
      `ApprovalRequiredAbove` free pass below a floor — a deliberate difference: a
      shrinkage write-off is a control by design, not a convenience, so there is no
      "no-approval-needed" band at all here.
12. **`Cancel()` is scoped to only the states that precede any real stock effect** —
    `StockTransfer.Cancel` from `Draft` only (before `Dispatch` posts the
    TransferOut/TransferIn pair), `Stocktake.Cancel` from `Counting` or
    `PendingReview` only (before `Post` turns counted variance into ledger movements).
    Mirrors `PurchaseOrder.Cancel`'s own rule (refuses once anything has been
    received) rather than inventing a new shape: once an aggregate has committed an
    effect outside itself, "cancel" would mean reversing a real movement, not flipping
    a status, and that reversal belongs to the aggregate's normal lifecycle
    (`Receive` + `WriteOffVariance` for a transfer, a correcting count for a
    stocktake) instead of a shortcut around it. Both cancellations require a
    non-empty reason (`InventoryErrors.CancellationReasonRequired`), same as
    `PurchaseOrder.Cancel`. Gated behind the same permission that creates/performs the
    workflow (`Inventory.TransferCreate`, `Inventory.CountPerform`) rather than a new
    permission code — cancelling before anything has moved is not a more sensitive
    operation than starting the thing in the first place.
    Added via `TransferAndStocktakeCancellation` (Inventory module migration) — six
    nullable columns (`CancelledByUserId`, `CancellationReason`, `CancelledAt`) across
    `StockTransfers` and `Stocktakes`. 4 new integration tests (2 per aggregate: cancel
    succeeds pre-effect, refused post-effect) and 7 new unit tests.
13. **Provisioning now has per-operator identity, replacing the single shared secret
    flagged in item 10.** `ProvisioningOperator` (`POS.Identity.Domain`) is a named,
    individually revocable credential — hashed at rest
    (`ProvisioningOperator.HashKey`, SHA-256, the same shape `RefreshToken.TokenHash`
    uses for a login session), with a `RevokedAt` that idempotently freezes once set.
    Deliberately NOT `ITenantScoped`: provisioning a tenant is the one operation that
    necessarily happens before any tenant exists, so the credential authorizing it
    cannot itself belong to one — same reasoning as `Permission` and `Tenant` sitting
    outside the tenant boundary.
    - **Two secrets, two purposes, not one.** `RequireOperatorApiKeyFilter` (gates
      `POST /tenants`) now looks up the presented key's hash against active
      `ProvisioningOperator` rows instead of comparing against a single configured
      string, and refuses closed if the operator table is empty — the same stance
      the old single-secret design took toward being unconfigured.
      `RequireRootOperatorApiKeyFilter` is new and gates a *separate* secret,
      `Provisioning:RootApiKey`, that only mints and revokes operator identities via
      `POST/GET /provisioning/operators` and `POST /provisioning/operators/{id}/revoke`
      — it can never itself provision a tenant (`The_root_key_does_not_double_as_an_
      operator_key` pins this down). Splitting the two means the rarely-used
      credential (onboard/offboard an operator) can be locked away far more tightly
      than the one used for routine bootstrap calls.
    - **The plaintext key exists exactly once, in the creation response, and is never
      persisted** — only `ProvisioningOperator.KeyHash` is stored. A lost key has no
      recovery path other than revoking that operator and minting a replacement,
      the same stance taken toward a lost refresh token.
    - **`Tenant.ProvisionedByOperatorId` is nullable and not a foreign key on
      purpose.** A tenant seeded outside the API has no operator to attribute, and an
      operator row may be pruned long after the tenants it created are still active
      — the column's whole job is to outlive that, so it degrades to "unknown" rather
      than becoming an orphaned reference or blocking an operator's deletion.
    - **The audit-visibility endpoint (`GET /provisioning/operators`) returns names
      and timestamps only, never a hash** — confirmed by
      `Listing_operators_never_exposes_a_key_or_its_hash`. This is deliberately a
      narrower promise than "never leaks a key": the hash is already one-way, but the
      test exists so nobody "simplifies" the response DTO later by including it.
    - Added via `ProvisioningOperators` (Identity module migration) — one new table
      plus `Tenants.ProvisionedByOperatorId`. `ApiFixture` now mints its shared test
      operator through the real `POST /provisioning/operators` endpoint (not a seeded
      row) so the mint path itself is exercised by every integration test in the
      suite, not only the ones naming it explicitly. 10 new integration tests, 6 new
      unit tests.
14. **`POS.WalkingSkeleton` deleted.** Confirmed nothing referenced it outside its own
    project — no `ProjectReference` from any other `.csproj`, no `using` of its
    namespace anywhere in `src/` or `tests/` — then removed it from `POS.sln`
    (`dotnet sln remove`) and deleted `src/Hosts/POS.WalkingSkeleton/`. Solution builds
    clean with zero warnings afterward, confirming it was genuinely dead rather than
    silently load-bearing for something (a design-time factory, a shared test host).
    `POS.TerminalAgent` is untouched — it is unfinished, not dead: a real, if
    incomplete, SQLite-based terminal host, out of scope for this cleanup.
15. **Sync's master-data pull is a full snapshot every time, deliberately not a true
    incremental delta.** `POS.Sync.Domain.MasterDataVersion`/`TerminalSyncCursor` were
    already scaffolded for real incremental sync but nothing populated them — wiring
    them up for real means every write path in every source module (each product
    create/update/deactivate) publishing a version bump and a durable change-log entry
    that does not exist as a table anyone appends to. That is materially larger than
    the actual gap: there was **no way at all** for a terminal to receive master data.
    `MasterDataPullService` asks every registered `IMasterDataSource` for its complete
    current state and returns it as `IsFullSnapshot: true`; `PullMasterDataRequest.Cursors`
    is accepted (so the wire contract is unaffected) but not yet used to filter the
    response. Safe because every change is an idempotent upsert or soft-remove — a
    terminal that already has everything just re-applies identical data. Catalog
    implements the one registered source today (`ProductMasterDataSource`: active
    variants, their barcodes, their owning product's tax group) via the SAME
    registered-implementation inversion `ISyncRecordHandler` uses for the upload
    direction — Sync knows a "Product" source exists, never that Catalog provides it.
    3 new integration tests.
16. **Per-tenant configuration store, closing the gap items 3–4 flagged.**
    `TenantSetting` (Identity, tenant-scoped, an opaque `(tenant, key) -> JSON` row) plus
    `ITenantSettingsDirectory` (the read-only contract Purchasing/Inventory resolve
    through) plus `PurchasingPolicyResolver`/`InventoryPolicyResolver` (each: read the
    tenant's override if one exists, fall back to the deployment default otherwise,
    swallowing a malformed override rather than taking down every purchasing operation
    for that tenant). `GET/PUT /api/v1/settings/{purchasing,inventory}-policy` expose
    it, gated by `Permissions.Administration.SettingsEdit` — a permission code that
    already existed in the catalogue, unused, apparently anticipating exactly this.
    **Caught a real, would-have-shipped bug**: `PurchasingPolicyOptions`/
    `InventoryPolicyOptions` are ALSO registered as DI singletons (the deployment
    default every other endpoint injects as a service). Minimal API silently resolves
    a parameter of a DI-registered type FROM THE CONTAINER instead of the request body
    unless told otherwise — so the PUT endpoints returned 200 and echoed a
    plausible-looking object while saving whatever the deployment default already was,
    completely ignoring the caller's JSON. Only the read-after-write integration test
    caught it; a hand-inspection of the code would not have. Fixed with an explicit
    `[FromBody]` on both PUT parameters, with a comment explaining why it is load-
    bearing. **Any future endpoint taking a request body of a type that is also
    DI-registered needs the same attribute.** 6 new integration tests.
17. **Edge hardening and observability wiring, all previously entirely absent.**
    Rate limiting (`AddRateLimiter`/`UseRateLimiter`): a generous global per-IP fixed
    window (3,000/10s, small queue) as a coarse circuit breaker — not the primary
    defence against enumeration, which is the credential itself — plus a separate,
    still-generous policy on the anonymous Provisioning surface specifically. CORS
    defaults to an **empty allow-list** (refuse closed, the same stance
    `RequireOperatorApiKeyFilter` takes) since no browser client exists yet.
    `UseHttpsRedirection` unconditional (a harmless no-op with no HTTPS port
    configured, e.g. under the integration suite's TestServer); `UseHsts` outside
    Development only. Health checks are exempted from rate limiting
    (`.DisableRateLimiting()`) since an orchestrator polls them far more often than any
    real client calls anything else. Separately: `CachedPermissionResolver` (ADR 013)
    now has a genuine two-level cache — L1 unchanged (`IMemoryCache`), L2 added
    (`IDistributedCache`, Redis via an optional connection string, or the
    same-abstraction in-process `AddDistributedMemoryCache` stand-in when unconfigured
    — one code path, not a branch). Proven with a test that force-drops L1 only
    (simulating a cold instance) and confirms L2 answers correctly without a database
    round trip, and that invalidation clears both levels. The Seq sink
    (`Serilog.Sinks.Seq`, previously package-referenced but never in any `WriteTo`
    list) is now wired in `appsettings.Development.json` against docker-compose's
    `seq` service. 5 new integration tests (rate limiting/CORS verified by the whole
    suite continuing to pass under the new limits; L1/L2 caching has 2 dedicated
    tests; health/docs endpoints have `ApiSurfaceTests`).
18. **`Api:EnableOpenApiDocs` and Scalar, matching what README.md always claimed.**
    `AddOpenApi()`/`MapOpenApi()` are always registered (the schema costs nothing to
    generate and other tooling can consume it regardless); the interactive Scalar page
    at `/scalar` is gated like Provisioning — on in Development, an explicit opt-in
    otherwise, because a schema browser is reconnaissance information an operator
    should choose to expose. 4 new integration tests confirm both `/openapi/v1.json`
    and `/scalar` are genuinely live, not aspirational.
19. **A real login/refresh HTTP surface, tenant-admin seeding, and a Redis
    resilience bug caught in the process — the work that made a real back-office
    frontend possible.** `TokenService`/`RefreshTokenService` had existed since the
    identity milestone with nothing mapping either to HTTP — every integration test
    minted a token in-process. `IdentityEndpoints.cs` (`POST /auth/login`,
    `POST /auth/refresh`) is the first real front door for a human.
    - **Login needs a tenant to look the user up in, and email alone cannot supply
      one** — email is unique per tenant, not globally (the same person may
      legitimately work for two merchants), so the request carries the tenant's
      `Subdomain` (already globally unique) alongside credentials — the same
      "which workspace" step most multi-tenant products ask for. Every failure path
      (unknown subdomain, unknown email, wrong password) returns the identical
      `auth.invalid_credentials` shape; only the lockout gets a distinct message,
      because knowing an account is locked doesn't hand an attacker anything they
      didn't already know from triggering it.
    - **`POST /tenants` now seeds an Owner role holding every permission
      (`Permissions.AllCodes`, gathered by reflection so the grant-everything path
      never drifts out of step with the catalogue by hand) and one admin user
      granted that role tenant-wide** — a tenant nobody can log into is not usable,
      and there was previously no HTTP path to create the first user at all.
    - **`GET /organization`** exposes a tenant's companies/branches/warehouses —
      every document-raising endpoint (a purchase order, a stock adjustment)
      correctly requires these ids explicitly rather than guessing, but until this
      existed the only way to learn what they WERE was to read tenant
      provisioning's one-time response or query the database directly.
    - **Found and fixed a real production bug while wiring the frontend up against
      a live host**: `CachedPermissionResolver`'s L2 read/write calls had no
      exception handling. With `ConnectionStrings:Redis` configured but pointing at
      nothing (exactly what a bare `dotnet run` without `docker compose up` looks
      like), every permission-gated request paid Redis's full connection timeout
      (5+ seconds) and then failed with a 500 — an optimisation layer taking down
      the entire authorization path the moment its backing store was unavailable.
      Fixed by treating an L2 exception on read as a cache miss and on write/remove
      as a no-op, logging a warning either way, in `TryGetFromL2Async`/
      `TrySetL2Async`. Proven with `An_unreachable_L2_degrades_to_a_database_load_
      instead_of_failing_the_request`, which resolves permissions against a fake
      `IDistributedCache` that throws on every call. `appsettings.Development.json`'s
      `ConnectionStrings:Redis` also reverts to empty by default (the in-process
      fallback) — it had been pointed at `localhost:6379` on the assumption
      docker-compose's Redis would be running, which is exactly the misconfiguration
      that surfaced the bug. **This is the kind of bug that only shows up from
      actually running the system against a live scenario, not from reading the
      code** — precisely the standing reminder at the top of this document. 1 new
      integration test.
20. **The refresh token moved from the JSON response body into an `HttpOnly` cookie,
    and a real `POST /auth/logout` now exists.** Previously `POST /auth/login` and
    `POST /auth/refresh` returned the refresh token as a plain string in the body —
    exactly the shape the frontend milestone's own `tokenStorage.ts` comment flagged as
    an XSS-exfiltration risk the moment it landed in `localStorage`. Both endpoints now
    set it via `Response.Cookies.Append` (`HttpOnly`, `Secure`, `SameSite=None`, scoped
    to `/api/v1/auth`) instead, and neither `LoginResponse` nor `TokenResponse` carries
    it at all any more — client-side script can never read it, full stop.
    `Secure` is set unconditionally rather than branching on environment the way
    `UseHsts()` does, because Chromium/Firefox treat `http://localhost` as a secure
    context — this needed no HTTPS to verify locally. `POST /auth/logout` (new) is
    genuinely necessary, not decorative: without it "signing out" only ever cleared the
    frontend's own storage, leaving the refresh token valid server-side for up to 14
    more days — `RefreshTokenService.RevokeAsync` (new method) is what actually kills
    it, and the endpoint is idempotent (a missing/already-revoked cookie still returns
    `204`) the same way `ProvisioningOperator.Revoke` already is. Program.cs's CORS
    policy gained `.AllowCredentials()`, which only works because it already used an
    explicit `WithOrigins` allow-list rather than a wildcard — the two are mutually
    exclusive in ASP.NET Core by design. 5 new/changed integration tests in
    `AuthApiTests.cs` (login no longer exposes the token in the body, refresh via the
    cookie, refusing a bare request with no cookie, logout actually revoking, logout
    being a no-op with nothing to revoke).
21. **User & role management got a real HTTP surface and a back-office screen** —
    previously the domain (`User.AssignRole`, `Role.Grant`, `RoleAssignment`) fully
    supported it but nothing mapped it to HTTP, so every tenant was stuck with exactly
    the one seeded Owner admin. `UserManagementEndpoints.cs` adds invite (`POST
    /users`, mints and hashes a one-time temporary password server-side, returned
    exactly once — the same "plaintext exists only in the creation response" stance
    `POST /provisioning/operators` already takes), list users/roles/permissions, create
    a custom role from a chosen permission subset, and assign/revoke a role at a scope.
    Assign/revoke both use the codebase's established two-step authorization
    (`RequirePermission` plus an in-handler `IPermissionScopeGuard.HasAtScopeAsync`
    check against the request's actual scope) rather than the coarse gate alone, and
    revoking your OWN role assignment is refused — the same separation-of-duties stance
    approval ladders already take. **Caught a real bug in the process**: assigning a
    role to an already-loaded `User` threw `DbUpdateConcurrencyException` — EF's
    change-detection heuristic marked the new `RoleAssignment` owned-collection entry
    `Modified` instead of `Added` because the aggregate was loaded, not freshly
    `Add()`-ed, the exact same class of bug §1 already documents for
    `StocktakeService.RecordCountAsync`. Fixed the same way: explicitly set
    `db.Entry(newAssignment).State = EntityState.Added` after calling the domain
    method. 12 new integration tests.
22. **`Product` finally has a real update path, closing a gap this document previously
    misreported as already closed.** The frontend milestone's own §9 claimed "updating
    a product" had a working endpoint; on inspection there was none — only
    create/list/delete. Added `Product.Rename(name, occurredAt)` (a `ProductRenamed`
    domain event, mirroring `Deactivate`'s shape) and `PUT /catalog/products/{id}`. The
    Products screen gained matching Edit (inline rename form) and Deactivate controls;
    Deactivate already existed server-side (`DELETE`, a soft delete via
    `AuditingInterceptor`'s `Remove()` reinterpretation — see `CatalogEndpoints.cs`'s
    remarks) but had no UI trigger either. 4 new integration tests.
23. **Expenses, Reconciliation, and Purchasing invoices/returns went from
    tested-backend-no-UI to full screens**, closing the largest remaining gap §9 had
    flagged. Two of the four Purchasing endpoint groups had no list route at all —
    only get-by-id — so `GET /purchasing/invoices` and `GET /purchasing/returns` were
    added first (mirroring `GET /purchasing/orders`'s existing shape) before a screen
    could show anything beyond what a caller had just created. 2 new integration
    tests for the list endpoints. The Expenses and Reconciliation screens are entirely
    additive — every endpoint they call already existed.
24. **A product/variant picker closed the "GUIDs typed by hand" shortcut** (§9 known
    shortcut 1, formerly the single highest-priority remaining frontend gap — §8 item
    6). `ProductSummaryResponse` gained a `VariantId` field (`GET /products`,
    `GET /products/{id}`, and the `PUT` rename response all now expose the product's
    primary variant id, not just its own creation response) — a new
    `src/api/products.ts` (`useProducts`, mirroring `useOrganization`'s shape) backs a
    `<select>` of product names everywhere a variant id used to be a raw text field:
    a purchase order line, a supplier return line, and a stock adjustment. 1 new
    integration test (`Listing_products_exposes_the_primary_variant_id`).

## 7. Known limitations, technical debt, blockers

- **No Docker in this environment** — the Testcontainers fallback path in `ApiFixture`
  is unexercised, and the new `src/Hosts/POS.Api/Dockerfile` /
  `docker-compose.yml` `api` service (§6 item 17 area) have been reviewed carefully but
  never actually built or run. If CI or a future session runs with Docker available,
  verify both paths.
- **Fiscal transmission/settlement jobs blocked** on missing counterparties, not
  infrastructure — see §4. **No real (online-gateway) payment provider** either, for
  the same class of reason — `ManualCardProvider` is a genuine, complete
  implementation of the standalone-terminal workflow, not a stub, but nothing
  authorizes over an API. All three need an external business relationship (a tax
  authority, a bank, a card acquirer) this codebase cannot establish for itself.
- **Cash/payment split (§6 item 3) is a judgement call** still needing confirmation —
  a domain-expert sign-off, not an engineering task.
- **`Cancel()` only covers the pre-effect states, by design** — see §6 item 12.
  Neither aggregate can be cancelled once it has committed a real stock effect
  (`Dispatch` for a transfer, `Post` for a stocktake); a transfer past Draft or a
  posted stocktake must be resolved through its normal lifecycle instead (`Receive` +
  `WriteOffVariance`, or a correcting count).
- **No terminal/cashier UI exists** — the offline, hardware-integrated (barcode
  scanner, receipt printer, cash drawer) application a cashier would actually use to
  ring up a sale. Not attempted: a full separate application, realistically weeks of
  dedicated work, needing explicit direction (target platform — native, Electron,
  embedded browser on fixed hardware — and offline-first architecture) before any
  code should be written.
- **The back-office admin UI now covers every module with a tested backend** — see §9.
  Products, Purchasing (including invoices/returns), Inventory, Expenses,
  Reconciliation, Users & Roles, and per-tenant Settings are all covered end-to-end.
  The refresh-token-in-`localStorage` shortcut (§6 item 20) and the raw-GUID
  product/variant fields (§6 item 24) flagged since earlier milestones are both closed
  now. What remains is listed in full in §9: no user/role-management screen for
  anything beyond what `UsersPage.tsx` already covers (bulk operations, pagination on
  large lists).
- **No CD pipeline, IaC, or secrets-management integration.** All three depend on a
  deployment target (Azure/AWS/on-prem/etc.) that has not been chosen — a business/
  infrastructure decision, not an engineering gap.
- **Sync's master-data pull is a full snapshot, not a true incremental delta** — see §6
  item 15. The scaffolding for real incremental sync (`MasterDataVersion`,
  `TerminalSyncCursor`) exists but is unused; `IMasterDataSource` is the seam a real
  implementation would plug into.

---

## 8. Recommended next milestone

A back-office-completeness pass closed every internally-resolvable item from the
previous punch list: user & role management got a real HTTP surface and screen (§6 item
21), Expenses/Reconciliation/Purchasing invoices-returns got screens (§6 item 23), the
product-rename gap this document had previously misreported as closed actually got
closed (§6 item 22), and the refresh-token-in-`localStorage` shortcut plus the missing
real logout are both fixed (§6 item 20). 336 unit / 15 architecture / 136 integration
tests, all passing, zero build warnings.

**What remains is, without exception, either blocked on something outside this
codebase or genuinely polish-level, not a fresh milestone:**

1. **A country/regulator, an acquirer, and a bank** — fiscal transmission, a real
   (online-gateway) payment provider, and settlement reconciliation each need a named
   external counterparty and cannot proceed further without one (§7).
2. **A deployment target** — CD pipeline, Infrastructure-as-Code, and secrets-management
   integration all depend on which cloud (or on-prem) target gets chosen; building any
   of them speculatively risks rework once a real target exists.
3. **Explicit direction on the terminal/cashier UI** — a full separate application
   needing a chosen platform, framework, and offline-first UX before any code should
   be written. (The back-office admin UI is no longer in this category — see §9.)
4. **A Docker-capable environment** — the Testcontainers fallback path and the new
   `POS.Api` Dockerfile/compose service are both code-complete but unverified here for
   want of a Docker daemon.
5. **Confirm the standing judgement call** — §6 item 3 (cash is not a `Payment`) was
   made without an explicit domain-expert sign-off. Not urgent, but should not silently
   harden into unquestioned fact. A product/stakeholder conversation, not engineering
   work.

Everything else left in §9's known-shortcuts list is genuinely polish-level now (bulk
operations/pagination on the Users & Roles screen, and the standing `react-router-dom`
advisory acceptance) — no open design question left to resolve, just more screen work
if and when it's prioritised.

Before starting new work, run the full verification sequence to confirm the state
described here still holds:

```bash
dotnet build POS.sln -c Release
dotnet test tests/POS.UnitTests -c Release
dotnet test tests/POS.ArchitectureTests -c Release
dotnet test tests/POS.IntegrationTests -c Release
```

All four must be clean before trusting anything in this document as current. If
touching the frontend, also run (from `src/Frontend/POS.BackOffice`):

```bash
npm run build
```

---

## 9. Back-office frontend

**The React back-office application** at `src/Frontend/POS.BackOffice` (Vite + React 19
+ TypeScript + react-router-dom) now has a screen for every module with a tested
backend, verified end to end against a live `POS.Api` and a real SQL Server — not just
built, but actually clicked through in a browser each milestone: provisioned a tenant,
logged in, created a product and a barcode then renamed and deactivated it, created a
supplier, raised/approved/sent a purchase order, recorded an invoice (blocked by the
three-way match, then overridden), created and dispatched a supplier return and
recorded its credit note, recorded a stock adjustment, ran all five reconciliation
reports, recorded and rejected an expense (and watched the domain correctly refuse
self-approval), invited a user and assigned/revoked a role (and watched self-revocation
get refused), and saved a per-tenant settings override that survived a page reload.
Confirmed the refresh-token cookie is genuinely `HttpOnly` (`document.cookie` is empty
after login), that a hard reload survives via silent refresh, and that signing out
clears the cookie server-side (a stolen pre-logout cookie value can no longer refresh).
Zero console errors across every session.

**What it covers:**

| Screen | Talks to |
|---|---|
| Login (`/login`) | `POST /auth/login` (access token only — refresh token is a cookie the browser handles automatically), transparently refreshing on a 401 or on page load via `POST /auth/refresh` |
| Dashboard (`/`) | Nothing — just confirms who's signed in and where |
| Products (`/products`) | `GET/POST/PUT /catalog/products`, `DELETE /catalog/products/{id}`, `POST /catalog/products/{id}/barcodes` |
| Purchasing (`/purchasing`) | Suppliers, orders, invoices (record/match/approve/override-block), supplier returns (create/dispatch/credit-note) — every `/purchasing/*` route except goods receipts |
| Inventory (`/inventory`) | `GET /inventory/warehouses/{id}/balances`, `POST /inventory/adjustments` |
| Expenses (`/expenses`) | `GET/POST /expenses`, `POST .../approve`, `POST .../reject` |
| Reconciliation (`/reconciliation`) | All five `GET /reports/*` endpoints |
| Users & Roles (`/users`) | `GET/POST /users`, `POST /users/{id}/roles(/revoke)`, `GET/POST /roles`, `GET /permissions` |
| Settings (`/settings`) | `GET/PUT /settings/{purchasing,inventory}-policy` |

**Cosmetic addition, previously undocumented (found during the 2026-08-23
re-verification, not new this session):** `LoginPage.tsx` and `DashboardPage.tsx` render
an animated background (`src/components/VantaBackground.tsx`,
`VantaHeroBackground.tsx` — vanta.js's `NET` effect over a `three.js` canvas, each
wrapping the well-known UMD/CJS interop quirk where Vite doesn't always unwrap
`module.exports.default` cleanly), and the dashboard's stat tiles count up from 0 on
load (`src/hooks/useCountUp.ts`, `setInterval`-driven rather than
`requestAnimationFrame` specifically so a backgrounded tab still finishes the
animation). Purely visual — no API surface, no behaviour change, nothing for a test
suite to cover — which is presumably why it shipped without a corresponding note here.
Flagging it now so it isn't mistaken for undocumented functional work later.

**The Mecodex brand kit (`src/Frontend/POS.BackOffice/Mecodex-Brand-Assets/`) is now
wired in, not just committed.** `public/favicon.svg` is the kit's dark rounded-square
network-mark favicon (previously a generic placeholder SVG); `public/favicon.ico` is
the kit's ICO, added as an `<link rel="alternate icon">` fallback in `index.html` for
browsers/contexts that don't take an SVG favicon. The same square mark now also
replaces the placeholder inline "P" SVG both `LoginPage.tsx` (`login-brand__mark`) and
`AppLayout.tsx` (`app-sidebar__mark`) used to render inline — both now `<img
src="/favicon.svg">`, one asset instead of three near-duplicate inline SVGs.
`index.html`'s `<title>` changed from the scaffold default `pos-backoffice` to
`Mecodex POS`. The kit's full logo lockups (wordmark, dark/light-bg variants) and social
assets remain unused — nothing in the current UI has a slot for a wordmark next to the
mark, only the icon-sized badge this swap covers.

**Architecture worth knowing before extending it:**

- **`src/api/accessToken.ts`** holds the access token in a module-level variable —
  never `localStorage`, never `sessionStorage`. It is lost on every hard reload by
  design; `src/auth/AuthContext.tsx`'s bootstrap effect re-mints one via
  `POST /auth/refresh` (the `HttpOnly` cookie does the proving) before rendering any
  protected route, which is why `ProtectedRoute`/`LoginPage` both gate on a new
  `isLoading` flag instead of deciding instantly — otherwise a reload would flash a
  redirect to `/login` before that silent refresh had a chance to resolve.
- **`src/auth/tokenStorage.ts`** now caches only non-sensitive profile fields
  (tenant/user id, display name, email, subdomain) so the top bar has something to
  show immediately on reload — no token of any kind lives there any more.
- **`src/api/client.ts`** is the one place every screen talks to the API through — a
  `fetch` wrapper that attaches the bearer token and `credentials: "include"` (required
  for the refresh-token cookie to round-trip), and on a 401 refreshes ONCE (via a
  module-level `refreshInFlight` promise, shared with the bootstrap flow above) before
  retrying, rather than each of several concurrently-racing requests independently
  trying to rotate the SAME refresh token. That matters specifically because
  `RefreshTokenService`'s reuse detection (ADR 005) treats a second rotation of an
  already-consumed token as theft and revokes the whole family.
- **`src/api/organization.ts`** (`useOrganization`) is the shared hook every screen
  needing a company/branch/warehouse picker uses, backed by
  `GET /api/v1/organization`.
- **`[FromBody]` is required, not decorative, on any request-body parameter whose
  TYPE is also registered in the API's DI container** (see `SettingsEndpoints.cs`,
  §6 item 16) — this bit the settings screens specifically and is the one lesson
  from an earlier milestone most likely to recur if a future screen's request body
  happens to share a type with something the host registers as a service.

**Known shortcuts a production build must close, not silently inherit:**

1. **`UsersPage.tsx` has no bulk operations or pagination** — fine at the scale a
   handful of provisioned users/roles sits at, but `GET /users` and `GET /roles` both
   cap at whatever the backend returns unpaged, same as every other list endpoint in
   this codebase (e.g. `GET /purchasing/orders` caps at 200). Not urgent; flagged so it
   doesn't quietly become a problem at a real tenant's scale.
2. ~~The `npm audit` finding on `react-router-dom` is accepted, not fixed.~~
   **Resolved, re-checked 2026-08-23**: `npm audit` in `src/Frontend/POS.BackOffice`
   now reports **0 vulnerabilities**. The RSC-scoped advisory this item used to accept
   (present on `react-router-dom` 7.18.2 at the time this item was written) is gone
   from the currently-installed version — no package version bump or code change was
   needed on this side, the advisory itself was withdrawn/superseded upstream. No
   further action needed unless a future `npm audit` turns something up again.
