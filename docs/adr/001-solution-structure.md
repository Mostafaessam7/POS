# ADR 001 — Two projects per module

**Status:** Accepted · **Date:** 2026-07-20

## Context

Two properties are in tension:

- Clean Architecture's dependency rule wants layers separated so Domain cannot
  reference Infrastructure.
- Modular monolith boundaries want modules separated so Sales cannot reach into
  Catalog's internals.

Satisfying both naively gives `modules × layers` projects. Ten modules and four
layers is forty projects, a multi-minute build, and heavy ceremony to add a field.

## Options

**A. Four projects total** (`Domain / Application / Infrastructure / Api`).
Layer separation compile-time enforced. Module separation does not exist —
every module's code shares assemblies, so boundary enforcement is impossible
by construction. This is what most tutorials teach.

**B. Four projects per module.** Both axes enforced. ~40 projects. Correct for a
30-engineer organisation, disproportionate now.

**C. Two projects per module.** `<Module>.Domain` (zero dependencies) and
`<Module>` (application + infrastructure).

## Decision

Option C.

The only compile-time enforced boundary is Domain purity — and that is the
boundary that matters, because it is the one that rots silently. A developer
under deadline pressure adds a `[Column]` attribute to an entity and nobody
notices for six months. Module boundaries, by contrast, are visible in code
review and are additionally covered by ArchUnit tests.

Application and Infrastructure share a project. This follows directly from
ADR 009 (no repository pattern): if handlers inject `DbContext`, the application
layer references EF Core. Adding an abstraction purely to preserve a diagram is
the "unnecessary abstraction" the project brief prohibits.

## Consequences

**Accepted cost.** Nothing mechanically prevents a handler writing raw SQL or an
endpoint touching `DbContext` directly. ArchUnit rules cover most of this; code
review covers the rest.

**Migration path.** A → C → B is straightforward. A → B is not. Choosing C
preserves the option of splitting later without forcing it now.

**Revisit.** End of Phase 3, when Catalog and Inventory contain real code and the
actual coupling is visible. Module boundaries are cheap to move while the modules
are nearly empty and expensive afterwards.
