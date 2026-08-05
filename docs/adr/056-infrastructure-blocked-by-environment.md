# ADR 056 — The infrastructure milestone is blocked by the environment, not by the design

**Status:** Accepted · **Date:** 2026-07-22 · **Phase:** Infrastructure consolidation

## Context

ADR 046 deferred the executable baseline and named it the highest-priority milestone before production readiness. Attempting it establishes that a substantial part of it **cannot be built in this environment at all**, and the reason is worth recording precisely so nobody repeats the attempt.

Measured, not assumed:

| Capability | State |
|---|---|
| .NET SDK | 8.0.129 only; the solution targets `net9.0` |
| ASP.NET Core shared framework | **8.0.29 present** — DI, hosting, routing, logging, configuration, JSON all usable |
| `api.nuget.org` | HTTP 403, blocked at the egress proxy |
| Local NuGet cache | Does not exist |
| Offline package folders | Do not exist |
| EF Core / `Microsoft.Data.SqlClient` assemblies | Absent from disk, and on no reachable feed |
| SQL Server | Not installed; no listening port |
| Docker daemon | Not present |

No allowed domain mirrors NuGet. EF Core cannot be reconstructed from source, because building it requires the very package graph that cannot be fetched.

## Decision

The milestone is split by what the environment can actually verify, and the two halves are reported separately rather than blended.

**Built and executed:**

- The cross-module reconciliation reports (Sale ↔ Fiscal, Sale ↔ Payment, receipt ↔ stock ledger, supplier return ↔ credit note), as pure functions over plain projections. These need no packages and close the largest standing debt in the project.
- A composition root and HTTP host — `POS.WalkingSkeleton` — that references **no packages at all** and runs on the shared framework alone.
- The walking skeleton itself, executed end to end against a live process.

**Not built, and deliberately not faked:**

- `DbContext` classes, Fluent API configuration, `IDesignTimeDbContextFactory`, migrations, SQL Server integration, the Testcontainers `ApiFixture`, and the ArchUnitNET architecture tests.

Writing EF Core configuration that cannot be compiled would produce hundreds of lines carrying the *appearance* of the milestone and none of its value. The entire point of ADR 046 was that unexecuted infrastructure is a guess. Committing unexecutable infrastructure would make the guess larger and harder to see.

`POS.Api` is untouched: it references eight unobtainable packages and fourteen module projects. It remains unbuildable, and pretending otherwise by stripping its dependencies would replace a known gap with a misleading one.

## Consequences

`InMemoryStore` exists in the walking-skeleton host. It is **not** a repository and must not become one — ADR 009 stands. It holds concrete aggregate types, has no interface, is confined to one host, and is deleted when EF Core becomes available.

Building through the real project chain, with analyzers enabled, surfaced **17 violations** in Phase 7 code that the shim test runner had masked by disabling analyzers, `TreatWarningsAsErrors` and documentation generation. All are now fixed. This is the single most valuable thing the attempt produced, and it is direct evidence for ADR 046's central claim: code verified only through a bypass is not verified.

The remaining work needs an environment with a NuGet feed, a .NET 9 SDK, and a SQL Server instance. Nothing in the architecture blocks it.
