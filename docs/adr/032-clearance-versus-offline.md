# ADR 032 — Clearance-model fiscalisation versus offline-first selling

**Status:** Accepted · **Date:** 2026-07-21 · **Phase:** 5

## Context

The platform's defining constraint is that a terminal must keep selling with no network (ADR 003). Several jurisdictions impose the opposite constraint: under a clearance model the tax authority must approve an invoice **before** it is issued to the customer. Italy's SdI for B2B, ZATCA standard invoices, and most Latin American regimes work this way.

These two requirements are in direct conflict, and the conflict is real rather than an artefact of our design. No architecture can obtain a government signature without connectivity.

Crucially, the conflict is narrower than it first appears. Most retail is B2C, and most regimes treat B2C simplified invoices far more permissively — ZATCA allows simplified invoices to be issued at the till and reported afterwards; Egypt's B2C flow is post-audit. The strict path usually applies to B2B invoices naming a registered buyer, which is a minority of POS volume.

## Decision

Offline legality is modelled explicitly as `OfflineIssuance` — `Permitted`, `PermittedWithDeferredClearance`, or `Prohibited` — and is a property of the **document type within a profile**, not of the country.

The pipeline checks this gate *before* allocating a number, since a gap-free series must not burn a number on a document about to be refused. Where a document type is `Prohibited` and the terminal is offline, the sale is refused for that document type and the cashier is offered a lawful alternative, typically a simplified receipt. The system never issues a document it knows to be invalid and reconciles later.

Signing is gated separately via `IFiscalSigner.CanSignOffline`, because a signer backed by a device-provisioned key can operate offline while one calling a server-held certificate or remote HSM cannot.

## Consequences

The collision is surfaced as an explicit, testable decision rather than discovered in production. A jurisdiction can be assessed for supportability by filling in a capability table before any code is written.

Offline selling remains fully available for ordinary B2C retail in every regime examined, which preserves the platform's core value proposition. B2B invoicing under clearance regimes genuinely requires connectivity, and this is a product limitation to be stated in sales material, not an engineering problem to be solved.

`PermittedWithDeferredClearance` carries residual business risk: a document issued offline may be rejected after the customer has left, requiring a credit note and reissue. Rejection rates must be monitored, since a systematic mapping error would surface as a backlog of rejected documents rather than as an error at the till.

Terminals under a deadline obligation need an operational alarm before the deadline expires, not a queue that silently retries. This is a monitoring requirement, tracked as work rather than resolved here.
