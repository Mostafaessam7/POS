# POS Platform

Multi-tenant retail POS. Offline-capable terminals, central back office.

Repository: **https://github.com/Mostafaessam7/POS**

**This document is a quick orientation, not the source of truth.** For the current,
verified state of the system — what's built, what's tested, what's still missing, and
why — see **[HANDOVER.md](HANDOVER.md)**, which is actively maintained across every
milestone. For a prioritised list of exactly what remains, see
**[PROJECT_STATUS.md](PROJECT_STATUS.md)**. If anything here contradicts either of
those, trust them over this file.

**Current state, in one sentence:** the backend — nine modules, tenancy, auth,
permissions, the full transaction lifecycle, background jobs, edge hardening, and CI —
builds, migrates, and runs clean against a real SQL Server, with 336 unit, 15
architecture, and 136 integration tests passing, plus a React back-office frontend that
covers every module. Nothing here is aspirational text; run the commands below yourself.
Re-verified from scratch 2026-08-23 (all four suites plus `npm run build`, no code
changes since the 2026-08-05 state HANDOVER.md describes) — same numbers, still clean.
Independently re-verified again 2026-08-27 in a separate review session (build, all
three .NET suites, frontend build, and `npm audit`) — still 336/15/136 and 0
vulnerabilities. Three cosmetic-only commits landed in between (2026-08-24/26): the
"Mecodex" brand kit is now wired in as the app's favicon and login/sidebar mark; no
API, schema, or test surface changed. See HANDOVER.md §9 and PROJECT_STATUS.md for
details.

## Getting started

Requires the .NET 9 SDK and either a reachable local SQL Server or Docker (for
Testcontainers).

```bash
git clone https://github.com/Mostafaessam7/POS.git && cd POS
dotnet build POS.sln -c Release
dotnet test tests/POS.UnitTests -c Release
dotnet test tests/POS.ArchitectureTests -c Release
dotnet test tests/POS.IntegrationTests -c Release
```

All four must be clean. The integration suite finds a database in this order: the
`POS_TEST_SQL` environment variable, then a local SQL Server on the default instance,
then Testcontainers (needs Docker) — so it runs unmodified whether or not Docker is
present.

To run the API itself:

```bash
docker compose up -d               # sqlserver, redis, seq, jaeger, mailhog, and the api itself
```

or, against dependencies you already have running locally:

```bash
dotnet ef database update --project src/Modules/Identity/POS.Identity   # repeat per module
dotnet run --project src/Hosts/POS.Api
```

| Service | URL |
|---|---|
| API | http://localhost:8080 (containerised) / see `launchSettings.json` for `dotnet run` |
| Scalar / OpenAPI | `/scalar` and `/openapi/v1.json` — enabled in Development, or via `Api:EnableOpenApiDocs` |
| Seq (logs) | http://localhost:5341 |
| Jaeger (traces) | http://localhost:16686 |
| MailHog | http://localhost:8025 |

`scaffold.sh` and `generate-projects.py` are the one-time bootstrap scripts that
originally generated the `.sln`/`.csproj` files. The solution is checked in now; neither
script is part of the normal build/test loop above.

## Layout

```
src/Shared/       SharedKernel (no dependencies), Common (cross-cutting), Contracts
src/Modules/      One folder per module, two projects each
src/Hosts/        Composition roots (POS.Api). No business logic.
tests/            Architecture, unit, integration
docs/adr/         Architecture Decision Records (57 of them — see docs/adr/README.md for an index)
```

Each module has two projects:

- `POS.<Module>.Domain` — entities, value objects, domain events. **Zero dependencies.**
- `POS.<Module>` — application and infrastructure. References Domain and EF Core.

Only the Domain boundary is enforced by the compiler, because it is the one that
rots silently. Everything else is enforced by `tests/POS.ArchitectureTests`,
which runs on every commit. See `docs/adr/001-solution-structure.md` for why.

## Rules that will fail your build

| # | Rule |
|---|---|
| 1 | Domain must not reference EF Core, ASP.NET Core, or `System.Text.Json` |
| 2 | Modules must not reference each other — use `POS.Contracts`, or a Sync-owned seam (`ISyncRecordHandler`, `IMasterDataSource`) for the one pair of directions that pattern covers |
| 5 | Nothing may reference a Host |
| 6 | Entities must not expose a public parameterless constructor |
| 7 | Aggregates must not expose mutable collections |
| 8 | No `DateTime.Now` / `UtcNow` / `Today` — inject `IClock` |
| 10 | Handlers must not reference `HttpContext` |

Rule 8 is the one worth internalising. A POS has three distinct notions of "now":

- **UtcNow** — wall clock, for ordering within a trusted environment.
- **BusinessDate** — the trading day, set at shift open. A store trading until
  02:00 books those sales to the *previous* day. Deriving this from
  `DateTime.Today` silently corrupts every daily report.
- **Terminal time** — an offline till's clock may be days out. Display only;
  order by terminal sequence.

## Conventions

- `decimal` for all money and all quantities. Weighted goods exist; binary
  floating point does not represent 0.10 exactly.
- Round only through `Money.Round()`. Never call `Math.Round` on a monetary value.
- `Result<T>` for expected failures (validation, not found, business rules).
  Exceptions for genuine faults.
- Financial records are immutable. Voids and refunds are new documents
  referencing the original, never updates.
- Stock is an append-only movement ledger. There is no settable quantity column.
- A parameter whose type is ALSO registered as a DI service must be explicitly marked
  `[FromBody]` if it is meant to bind the request body — minimal API silently prefers
  the DI registration otherwise, and the endpoint appears to work while quietly
  ignoring every request. `PurchasingPolicyOptions`/`InventoryPolicyOptions` are
  exactly this shape (registered as a deployment-wide default AND used as a settings
  request/response body); see `SettingsEndpoints.cs` for the reference fix.

## Testing

Integration tests run against **real SQL Server** — a local instance if one is
reachable, Testcontainers otherwise. `Microsoft.EntityFrameworkCore.InMemory` is not
used and should not be added: it does not enforce constraints, does not support
transactions, and translates LINQ differently from SQL Server. Tests that pass against
it produce confidence that has not been earned.

The integration database resets (via `Respawn`) at the start of every test run, so a
persistent local SQL Server never accumulates state across separate `dotnet test`
invocations.

## Licence notes

Two dependencies in common use changed licence recently and are deliberately
avoided:

- **MediatR** 13.0.0+ requires a commercial licence. Not used — endpoint filters
  cover the cross-cutting concerns natively.
- **FluentAssertions** 8+ requires a paid Xceed licence for commercial use. We
  use **Shouldly** (MIT).

CI (`.github/workflows/ci.yml`) fails the build on a dependency licence change via
`nuget-license`, and separately fails on any known-vulnerable package. Verify current
terms before adding anything.

## What's not here yet

The back-office admin UI (`src/Frontend/POS.BackOffice`) exists and covers every module,
including a browser-based register screen — see HANDOVER.md §9. What's still missing is
a real **terminal/cashier UI**: an offline-first, hardware-integrated (barcode scanner,
receipt printer, cash drawer, card terminal) application, out of scope until a target
platform is chosen. There is no CD pipeline, no Infrastructure-as-Code, and no
secrets-management integration; a `Dockerfile` for the API exists
(`src/Hosts/POS.Api/Dockerfile`) but has not been build-verified in an environment with
Docker available. A handful of integrations are intentionally unbuilt pending a business
decision this codebase cannot make for itself: which country's tax authority, which card
acquirer, which bank's settlement file format. See **[PROJECT_STATUS.md](PROJECT_STATUS.md)**
for the complete, current list with reasons and complexity estimates for each.
