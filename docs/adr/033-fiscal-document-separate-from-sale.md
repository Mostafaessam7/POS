# ADR 033 — The fiscal document is a separate aggregate from the sale

**Status:** Accepted · **Date:** 2026-07-21 · **Phase:** 5

## Context

A sale and its invoice look like one thing and are not. A sale is a commercial fact: goods left, money arrived, stock moved. A fiscal document is a legal artefact describing that fact, and in mandate jurisdictions it has its own lifecycle — queued, transmitted, cleared, rejected, superseded — that can extend well beyond the moment the customer leaves.

The obvious modelling choice is to put fiscal fields on `Sale`. That would make sale completion depend on a government web service replying, and would accumulate jurisdiction-specific state on the aggregate the whole system depends on.

## Decision

`FiscalDocument` is a separate aggregate in its own module, referencing the sale by id only, with no foreign key — the same loose-reference pattern used by `StockDocumentReference` in Phase 4. The Fiscal module never references Sales, and Sales never references Fiscal.

The pipeline consumes a flat, serialisable `FiscalContext` snapshot rather than the `Sale` aggregate itself, so a plugin cannot mutate core domain state and the context can survive being queued for days on an offline terminal.

Fiscal documents are immutable once issued, per ADR 007. Rejection does not rewrite or delete the document, because in a reporting regime the customer already holds the printed receipt; correction is a new credit note referencing the original.

## Consequences

A sale completes when the commercial transaction completes, independent of fiscal state. Fiscal retry, clearance polling, and deadline monitoring operate as background concerns without touching Sales.

The cost is that answering "is this sale fiscally valid?" requires joining across a module boundary, and that referential integrity between the two is not enforced by the database. A reconciliation report — sales without documents, documents without sales — is therefore mandatory rather than optional, and is tracked as Phase 5 work.
