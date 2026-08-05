# ADR 031 — Fiscalisation as a pluggable capability

**Status:** Accepted · **Date:** 2026-07-21 · **Phase:** 5

## Context

The platform is intended for thousands of businesses across many jurisdictions. Fiscal and e-invoicing rules differ enormously — Egypt's ETA, Saudi Arabia's ZATCA, Italy's SdI, Portugal's chained signing and SAF-T, Poland's KSeF and JPK, and the various Latin American clearance regimes all impose different document formats, signing schemes, numbering rules, and submission models. Mandates also change on legislated dates, and phase in by taxpayer size.

Embedding any of this in the core domain would mean the Sale aggregate — the single most important type in the system — accreting jurisdiction-specific fields and branches indefinitely, with every country's mandate change becoming a core release.

## Decision

The core domain is jurisdiction-agnostic and contains no fiscal rules whatsoever. Fiscalisation is expressed as an `IFiscalProfile` composed of six narrow, independently optional seams: numbering, document building, signing, transmission, QR generation, and archive export. Each jurisdiction ships as a separate assembly implementing only the seams it needs, registered in DI at composition time.

Profiles are selected per `Company` via an explicit `FiscalProfileCode`, not derived from country code, because a country may run several regimes concurrently and mandates phase in by date and taxpayer.

Behavioural differences the core must respect are exposed as `FiscalCapabilities` data — offline issuance, transmission model, deadlines, signature, chaining, QR, certified device — so the orchestrating pipeline branches on capability rather than on country. A `country == "XX"` check anywhere outside a country plugin is a defect in the abstraction, not a shortcut.

Plugins are ordinary referenced assemblies, not runtime-scanned from a folder. Loading unsigned code that produces legally binding documents is a supply-chain risk out of proportion to the deployment convenience.

## Consequences

Adding a jurisdiction is additive: a new project, a DI registration, no change to Sales, Inventory, or any existing plugin. The generic profile is a real implementation rather than a null object, so the abstraction is exercised end to end from day one — an extension model whose only implementation does nothing is untested.

Six interfaces cost more indirection than one, and a reader must consult the capability model to understand what actually happens for a given company. That is accepted: the alternative concentrates every jurisdiction's complexity into the aggregate least able to absorb it.

The abstraction is a bet that these six seams are the right decomposition. It is drawn from the published requirements of the named regimes and will be validated by the first real plugin. Where it proves wrong, the correct response is a new capability flag or a seventh seam, not a special case in the core.
