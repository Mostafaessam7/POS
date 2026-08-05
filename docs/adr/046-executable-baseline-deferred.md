# ADR 046 — The executable baseline is deliberately deferred, and is the highest-priority milestone before production readiness

**Status:** Accepted · **Date:** 2026-07-22 · **Phase:** 7 (recorded at the Phase 6 → 7 boundary)

## Context

Six phases of domain logic have been designed, written, and — for the zero-dependency
projects — genuinely compiled and tested. Approximately eleven projects have **never been
fed to a compiler**: `POS.Common`, the infrastructure half of every module, both hosts,
and three of the test projects. There is no composition root, no DI wiring, no EF
migration, no design-time factory, and no `SalesDbContext`, `FiscalDbContext` or
`PaymentDbContext`. The Phase 2 walking skeleton has never been executed end to end.

At the close of Phase 6 the architect's recommendation was to insert an **infrastructure
consolidation phase** before Phase 7: build the missing DbContexts and configurations,
stand up the composition root, produce the first migration, and actually run the walking
skeleton. The reasoning was that this is not a refactor — it is the point at which we
find out whether the design survives contact with EF Core, dependency injection and a
real database — and that every additional phase makes the discovery more expensive.

The product owner accepted that reasoning and, for scheduling reasons, chose to defer it
and continue with Phase 7 (Purchasing and supply chain).

## Decision

The executable baseline and infrastructure consolidation are **deliberately deferred**,
not overlooked. They are hereby recorded as the **highest-priority milestone before
production readiness** — ranking above any remaining feature phase in the roadmap.

Concretely, that milestone comprises:

1. `POS.Common` and every module's infrastructure project compiling.
2. `SalesDbContext`, `FiscalDbContext`, `PaymentDbContext` and `PurchasingDbContext`,
   with EF configurations and tenant query filters applied — verified by
   `TenantIsolationArchitectureTests`, which already guards this.
3. A composition root in `POS.Api` that wires something real, plus an
   `IDesignTimeDbContextFactory`.
4. The first EF migration, applied to a real SQL Server.
5. `POS.IntegrationTests` compiling — which requires the `ApiFixture` built on
   `WebApplicationFactory` and Testcontainers.
6. The Phase 2 walking skeleton executed end to end, on .NET 9, against a real database.
7. The three mandatory reconciliation reports (Sale↔Fiscal, Sale↔Payment,
   Payment↔settlement), which cannot be written without persistence.

**No phase of this project may be described as production-ready, and no deployment may
be attempted, until that list is complete.** This ADR exists so that a future reader
finds a decision rather than an omission.

## Consequences

The risk we are accepting is specific and worth naming precisely. It is *not* that the
domain code is wrong — it is compiled and tested where that is possible. It is that the
**seams between the domain and its infrastructure are entirely unvalidated**. Known
concrete suspects already on the register:

- `ArchUnitNET`'s fluent API is version-sensitive; the architecture tests are the most
  likely first-build casualty.
- `IX_StockMovements_Document` indexes a member of a complex property
  (`m.Reference.DocumentId`), which EF Core 8/9 may refuse at model-build time.
- Every package version in `Directory.Packages.props` is unvalidated, because `nuget.org`
  is unreachable from the build environment.
- The whole tree targets `net9.0` and uses `Guid.CreateVersion7()`, while verification has
  only ever run on a `net8.0` scratch copy with that call shimmed to `Guid.NewGuid()`.

The cost of deferral compounds. Each phase adds domain surface whose infrastructure half
is written blind, so the eventual consolidation will surface a larger batch of failures
at once, and diagnosing a hundred compilation errors across eleven projects is materially
harder than diagnosing ten across two. This ADR does not dispute the scheduling decision;
it records the bill.

Phases may continue to be marked "complete" against their gates on the understanding that
"complete" here means *the domain logic is implemented, compiled where possible, and
tested* — not *shipped*. That distinction is now explicit rather than assumed.
