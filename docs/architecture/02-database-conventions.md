# Database Conventions

## Types

| Concept | Type | Why |
|---|---|---|
| Money | `decimal(19,4)` | Never `float`. Four places handles unit prices before rounding |
| Quantity | `decimal(18,6)` | Weighed goods; six places survives unit conversion |
| Percentage | `decimal(9,4)` | Tax and discount rates |
| Identity | `uniqueidentifier` (UUID v7) | Time-sortable; no coordination needed offline |
| Timestamp | `datetimeoffset(7)` | Never `datetime`. Offset matters across a DST boundary |
| Business date | `date` | A trading day, not an instant |
| Currency | `char(3)` | ISO 4217 |
| Code / slug | `nvarchar(64)` | |
| Name | `nvarchar(256)` | |

`uniqueidentifier` as a clustered primary key is normally a mistake, because
random v4 GUIDs cause page splits. **UUID v7 is time-ordered**, so it behaves like
an identity column for insert locality while remaining offline-generatable.

## Naming

- Tables plural: `Products`, `SaleLines`
- Columns PascalCase
- Primary key `Id`
- Foreign key `{Entity}Id`
- Index `IX_{Table}_{Columns}`; unique `UX_{Table}_{Columns}`
- Check constraint `CK_{Table}_{Rule}`

## Indexing

Every tenant-scoped table leads with `TenantId` in its indexes. The query filter
appends `TenantId = @t` to every query, so an index that does not lead with it is
largely useless.

The hottest read in the entire product is barcode resolution — it runs on every
line item of every sale. It gets a covering index.

## Cascade behaviour

Default to `Restrict`. `Cascade` is appropriate only within an aggregate
(`Product` → `ProductVariant`).

Deleting a category must not silently delete its subtree and orphan every product
beneath it. That is the sort of thing discovered in production.

## Filtered unique indexes

**Mandatory on every soft-deletable table.**

```sql
CREATE UNIQUE INDEX UX_Barcodes_Tenant_Value
    ON Barcodes (TenantId, Value)
    WHERE IsDeleted = 0;
```

Without the filter, a barcode or SKU can never be reused after deletion. Merchants
reuse them constantly. The symptom reaches support as "the system says this
barcode exists but I cannot find the product" — because they cannot; it is
soft-deleted.

There is an integration test asserting every such index carries the filter.

## Migrations

- **Never `Database.Migrate()` at startup.** Multiple API instances race on the
  migration history table. Generate idempotent SQL, apply as a gated deployment
  step.
- Per-module `DbContext`, per-module history table:
  `__EFMigrationsHistory_Catalog`. Modules version independently.
- **Expand/contract, always.** Rolling deployments run the old and new application
  versions simultaneously, so every migration must be backward-compatible with the
  previous version. Add nullable → backfill → make required in a later release.
  Adding a non-nullable column without a default breaks the instances still
  serving traffic.
- Seed data is managed separately from schema and applied idempotently.
- Migration scripts are reviewed like code. A generated migration containing a
  `DROP COLUMN` is a data-loss incident waiting for a deployment window.

## Immutability of financial records

Sales, payments, and stock movements are **never updated or deleted**. A void is a
new document referencing the original. A refund is a new document referencing the
original.

This is not a stylistic preference. It is what makes the audit trail defensible to
a tax authority, and what makes "what did this look like on 14 March" answerable.
Enforce with a trigger or a check where the platform allows it, and never expose an
update path in the API.
