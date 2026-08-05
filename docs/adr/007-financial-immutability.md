# ADR 007 — Financial records are immutable

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 1

## Context

Correcting a mistaken sale by updating the record destroys the audit trail, breaks any report already run, and is indefensible to a tax authority.

## Decision

Sales, payments, and stock movements are never updated or deleted. A void is a new document referencing the original. A refund is a new document referencing the original. Soft delete is applied to master data and configuration only, never to transactional records.

## Consequences

Every historical question is answerable, including what the data looked like on a given date. Reports are reproducible. The cost is more rows and slightly more complex queries, which is a good trade. The API exposes no update path for these entities.
