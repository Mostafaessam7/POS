# Coding Standards

Rules here are either enforced by the build or exist because breaking them has a
specific, known cost. Anything that is merely taste belongs in `.editorconfig`,
not in this document.

## Enforced by the build

These fail CI. They are not advisory.

| Rule | Mechanism |
|---|---|
| Warnings are errors | `Directory.Build.props` |
| Nullable reference types enabled | `Directory.Build.props` |
| Domain projects reference nothing but SharedKernel | ArchUnit rule 1 |
| Modules do not reference each other | ArchUnit rule 2 |
| No `DateTime.Now` / `UtcNow` / `Today` outside `IClock` | Source-scan test |
| Entities have no public parameterless constructor | ArchUnit rule 5 |
| Aggregate collections are exposed as `IReadOnlyList` | ArchUnit rule 7 |
| No `HttpContext` in handlers | ArchUnit rule 9 |
| No bare `FromSqlRaw` outside the tenant-safe helper | ArchUnit rule 10 |
| Unique indexes on soft-deletable entities are filtered | Integration test |

## Money

**Money is `decimal`, always. `float` and `double` are banned in any financial
path.** Binary floating point cannot represent `0.10`; summing it a hundred times
gives `9.999999999999831`. That is a drawer that does not balance, discovered at
close of trade by someone who cannot explain it.

Use the `Money` type rather than a bare decimal. It carries currency and refuses
to add GBP to USD.

**`Money.Round()` is the only rounding entry point in the system.** It rounds half
away from zero — commercial rounding — not .NET's default banker's rounding. See
ADR 024. Scattering `Math.Round` calls produces amounts that differ by a penny
depending on which code path computed them, and reconciling that later is
genuinely difficult.

**Quantities are `decimal`, not `int`.** Weighed goods exist. `1.347 kg` is a
normal sale line. Integer quantities look harmless in week one and require a
migration across every transaction table in month nine.

## Time

**Never call the system clock directly.** Inject `IClock`. The test that enforces
this scans source files, because this is the rule people break first and the
consequences are untestable time-dependent behaviour.

Three distinct notions of "now" exist and must not be conflated:

| Concept | Type | Source |
|---|---|---|
| Instant something happened | `DateTimeOffset` UTC | `IClock.UtcNow` |
| Local wall-clock time at the store | `DateTimeOffset` + branch time zone | Converted |
| **Business date** | `BusinessDate` | Assigned at shift open |

The business date is **not** derivable from the calendar date. A store trading
until 02:00 books those sales to the previous business day. See `BusinessDate`.

Store `DateTimeOffset`, not `DateTime`. `DateTime` loses offset information and
the ambiguity surfaces during a daylight-saving transition, when a chain has an
hour of transactions it cannot order.

## Identity

`Guid.CreateVersion7()` for anything created on a terminal. Time-sortable, so it
keeps index locality; requires no coordination, so a till offline for a week can
still mint IDs.

**Never use database identity columns for records created offline.** Two
disconnected terminals both allocating `Id = 501` is not a theoretical problem.

Human-facing fiscal numbering (`ReceiptNumber`) is a **separate concern** and is
gap-free per terminal. Tax authorities require gap-free sequences; a UUID does not
satisfy that, and a chain-wide gap-free sequence is impossible offline.

## Nullability and failure

Expected failures return `Result<T>`. Faults throw.

```csharp
// Expected: the caller must handle this. Insufficient stock is a business
// outcome, not an exceptional condition.
Result<Sale> Complete(...)

// Fault: the caller cannot meaningfully recover. A cross-tenant write is a bug
// or an attack, and there is no scenario where continuing is preferable.
throw new InvalidOperationException(...)
```

The distinction is whether a correct caller could reasonably anticipate it. Using
exceptions for expected failures makes the happy path unreadable and is expensive
at checkout volumes; using `Result` for programmer errors buries bugs.

## Domain model

- Private setters. State changes go through methods that enforce invariants.
- Private parameterless constructor for EF only.
- Static factory methods over public constructors — they can validate and return
  `Result`.
- Collections exposed as `IReadOnlyList`, mutated only through aggregate methods.
- Domain events raised inside the aggregate, dispatched after `SaveChanges`.
- **No infrastructure references in domain projects.** Enforced by ArchUnit, and
  the reason each module has a separate `.Domain` project rather than a folder.

## Persistence

- **No repository pattern over EF Core.** `DbContext` is already a unit of work
  and `DbSet<T>` is already a repository. Wrapping it adds a layer that mostly
  forwards calls and blocks `Include`, projection, and split queries. The
  exception is `IStockLedger`, which has genuinely different implementations for
  SQL Server and SQLite. See ADR 009.
- Configuration in `IEntityTypeConfiguration<T>` classes, never in
  `OnModelCreating`.
- `decimal(19,4)` for money, `decimal(18,6)` for quantities.
- Every unique index on a soft-deletable entity **must** be filtered on
  `IsDeleted = 0`.
- `AsNoTracking()` for reads that do not save. The change tracker is not free at
  checkout volumes.
- Explicit `Include`. Lazy loading is disabled — it produces N+1 queries that
  only appear under production data volumes.

## Async

- `async` all the way down. No `.Result`, no `.Wait()`, no `.GetAwaiter().GetResult()`.
- `CancellationToken` on every async method that crosses a boundary.
- `ConfigureAwait` is unnecessary in ASP.NET Core — there is no synchronisation
  context.

## Naming

- Commands are imperative: `CompleteSale`, `AdjustStock`.
- Queries are nouns: `GetProductBySku`.
- Domain events are past tense: `SaleCompleted`, `StockAdjusted`.
- Permissions follow `module.resource.action[.qualifier]`.
- Boolean members read as assertions: `IsActive`, `HasVariants`, `CanRefund`.

## Comments

Comment **why**, not **what**. `// increment counter` above `counter++` is noise.

The comments worth writing are the ones recording a decision that looks wrong
until you know the constraint:

```csharp
// 404, not 403: a 403 confirms the resource exists, which is itself a small
// information leak across a security boundary.
```

Every non-obvious trade-off in this codebase carries a comment like that, and a
matching ADR where the reasoning runs longer than a paragraph.

## Tests

- Testcontainers for anything touching the database. The EF in-memory provider
  does not enforce constraints, does not do relational semantics, and passes tests
  that fail against SQL Server. See ADR 005 in the Phase 0 set.
- Test names state the behaviour:
  `Sale_after_midnight_books_to_the_previous_trading_day`.
- Shouldly for assertions, not FluentAssertions — version 8 moved to a paid
  licence. See ADR 010.
- Every bug fix ships with the test that would have caught it.
