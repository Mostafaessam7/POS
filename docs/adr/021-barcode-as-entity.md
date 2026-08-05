# ADR 021 — Barcode is an entity, unique per tenant, filtered on soft delete

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 3

## Context

One variant routinely has several barcodes: the manufacturer's EAN, a case code, a supplier code, and an internally generated one. Modelling it as a column forces merchants to pick one, and they will paste four codes separated by commas.

## Decision

Barcode is a first-class entity with a symbology and check-digit validation. Uniqueness is scoped to the tenant, not global, and the unique index is filtered on IsDeleted = 0.

## Consequences

Different suppliers reuse codes and merchants generate their own, so global uniqueness would be wrong. The filter is what allows a barcode to be reused after a product is deleted, which merchants do constantly. Without it the symptom reaches support as the system claiming a barcode exists for a product the user cannot see.
