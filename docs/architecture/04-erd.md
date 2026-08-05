# Entity Relationship Diagram — Phases 1 to 3

Covers the Identity, Catalog, and Sync modules as built. Sales, Inventory, and
Payments arrive in Phases 4–6 and are shown as stubs where they are already
referenced.

Every table below except `Tenants` carries `TenantId`. It is omitted from the
diagram for readability, but its absence on a table would be a bug — the query
filter is applied by marker interface, so a missing `ITenantScoped` means a
missing filter.

## Identity and organisation

```mermaid
erDiagram
    TENANTS ||--o{ COMPANIES : "owns"
    COMPANIES ||--o{ BRANCHES : "operates"
    BRANCHES ||--o{ WAREHOUSES : "holds stock in"
    BRANCHES ||--o{ TERMINALS : "runs"

    TENANTS ||--o{ USERS : "employs"
    USERS ||--o{ ROLE_ASSIGNMENTS : "granted"
    ROLES ||--o{ ROLE_ASSIGNMENTS : "assigned via"
    ROLES ||--o{ ROLE_PERMISSIONS : "bundles"
    PERMISSIONS ||--o{ ROLE_PERMISSIONS : "granted by"

    USERS ||--o{ REFRESH_TOKENS : "holds"
    TERMINALS ||--o{ REFRESH_TOKENS : "bound to"
    USERS ||--o{ AUTHORIZATION_GRANTS : "requests"
    USERS ||--o{ AUTHORIZATION_GRANTS : "approves"

    TENANTS {
        uuid Id PK
        string Name
        string Subdomain UK "branding and login routing only, never authorization"
        int Status
    }

    COMPANIES {
        uuid Id PK
        uuid TenantId FK
        string LegalName
        string TaxRegistrationNumber "separate legal entity per franchise or country"
        string BaseCurrency
    }

    BRANCHES {
        uuid Id PK
        uuid CompanyId FK
        string Code UK "appears in receipt numbers, immutable once trading"
        string TimeZoneId "IANA, required to derive business date"
        int BusinessDayStartHour "trading day rollover, e.g. 4 for a late bar"
    }

    WAREHOUSES {
        uuid Id PK
        uuid BranchId FK
        int Kind "SalesFloor / StockRoom / Transit / Quarantine"
    }

    TERMINALS {
        uuid Id PK
        uuid BranchId FK
        string Code "two digits in the receipt number"
        string CertificateThumbprint "device credential, rotate to lock out a stolen till"
        bigint LastReceivedSequence "ordering authority, never trust terminal clocks"
        bigint ReceiptSequence "gap-free per terminal"
        int Status
    }

    USERS {
        uuid Id PK
        string Email UK
        string PasswordHash
        string PasswordAlgorithm "parameters stored so hashes upgrade on login"
        string PinHash "separate credential, terminal-only, never back office"
        int PermissionVersion "bump invalidates cached permissions instantly"
        int FailedLoginCount
        datetime LockedUntil
    }

    ROLES {
        uuid Id PK
        string Name
        bool IsSystemRole
    }

    PERMISSIONS {
        uuid Id PK
        string Code UK "module.resource.action"
        string Module
    }

    ROLE_PERMISSIONS {
        uuid RoleId PK "also FK to ROLES"
        uuid PermissionId PK "also FK to PERMISSIONS"
    }

    ROLE_ASSIGNMENTS {
        uuid Id PK
        uuid UserId FK
        uuid RoleId FK
        int ScopeType "Company / Branch / Warehouse -- never Tenant"
        uuid ScopeId "'Manager at Branch 12', not 'Manager'"
    }

    REFRESH_TOKENS {
        uuid Id PK
        uuid UserId FK
        string TokenHash "hashed at rest, it is a credential"
        uuid FamilyId "reuse of any member revokes the whole family"
        string DeviceFingerprint
        int Status
        uuid ReplacedByTokenId FK
    }

    AUTHORIZATION_GRANTS {
        uuid Id PK
        uuid RequestedByUserId FK
        uuid ApprovedByUserId FK "manager override records BOTH principals"
        string PermissionCode
        string CommandFingerprint "binds approval to the exact operation shown"
        string Reason "mandatory, drives the override-frequency fraud report"
        bool IsConsumed "single use"
        bool ApprovedOffline
    }
```

## Catalog

```mermaid
erDiagram
    CATEGORIES ||--o{ CATEGORIES : "parent of"
    CATEGORIES ||--o{ PRODUCTS : "classifies"
    BRANDS ||--o{ PRODUCTS : "brands"
    UNITS_OF_MEASURE ||--o{ PRODUCTS : "sold in"
    TAX_GROUPS ||--o{ PRODUCTS : "taxed by"
    TAX_GROUPS ||--o{ TAX_RATES : "versioned by date"

    PRODUCTS ||--|{ PRODUCT_VARIANTS : "always has at least one"
    PRODUCT_VARIANTS ||--o{ BARCODES : "scanned by"
    PRODUCT_VARIANTS ||--o{ PRODUCT_VARIANT_ATTRIBUTES : "described by"
    VARIANT_ATTRIBUTES ||--o{ VARIANT_ATTRIBUTE_OPTIONS : "offers"
    VARIANT_ATTRIBUTE_OPTIONS ||--o{ PRODUCT_VARIANT_ATTRIBUTES : "selected as"

    PRICE_LISTS ||--o{ PRICE_LIST_ENTRIES : "contains"
    PRODUCT_VARIANTS ||--o{ PRICE_LIST_ENTRIES : "priced by"

    CATEGORIES {
        uuid Id PK
        uuid ParentId FK
        string Slug
        string Path "materialised, '/electronics/audio/' -- prefix seek, not recursive CTE"
        int Depth
    }

    PRODUCTS {
        uuid Id PK
        uuid CategoryId FK
        uuid BrandId FK
        uuid UnitOfMeasureId FK
        uuid TaxGroupId FK
        string Name
        int Kind "Stocked / Weighed / Service / Bundle"
        bool IsActive "deactivate, never delete -- sale lines reference this"
    }

    PRODUCT_VARIANTS {
        uuid Id PK
        uuid ProductId FK
        string Sku UK
        decimal DefaultPriceAmount "decimal(19,4) -- never float"
        string DefaultPriceCurrency
        decimal AverageCost "weighted average, margin reporting only, never a selling price"
    }

    BARCODES {
        uuid Id PK
        uuid VariantId FK
        string Value "UNIQUE per tenant WHERE IsDeleted = 0 -- filtered, so codes can be reused"
        int Symbology "EAN13 / UPC / GS1-128 / Internal"
        bool IsPrimary "exactly one per variant"
        bool IsDeleted
    }

    VARIANT_ATTRIBUTES {
        uuid Id PK
        string Name "Size, Colour -- typed, not a JSON blob"
        int DisplayOrder
    }

    VARIANT_ATTRIBUTE_OPTIONS {
        uuid Id PK
        uuid AttributeId FK
        string Value
    }

    PRODUCT_VARIANT_ATTRIBUTES {
        uuid VariantId PK "also FK to PRODUCT_VARIANTS"
        uuid AttributeId PK "also FK to ATTRIBUTES"
        uuid AttributeOptionId FK
    }

    PRICE_LISTS {
        uuid Id PK
        uuid BranchId FK "null means chain-wide"
        string Currency
        datetime EffectiveFrom
        datetime EffectiveTo "date-effective, never overwritten"
        int Priority "higher wins, branch beats chain"
        int Version "recorded on the sale line so a price is traceable years later"
    }

    PRICE_LIST_ENTRIES {
        uuid PriceListId PK "also FK to PRICE_LISTS"
        uuid VariantId PK "also FK to PRODUCT_VARIANTS"
        decimal Amount
    }

    TAX_GROUPS {
        uuid Id PK
        string Code
    }

    TAX_RATES {
        uuid Id PK
        uuid TaxGroupId FK
        decimal Percentage
        datetime EffectiveFrom
        datetime EffectiveTo "returns must use the ORIGINAL rate"
        bool IsInclusive "jurisdictional: EU/MEA inclusive, North America exclusive"
    }
```

## Sync

```mermaid
erDiagram
    TERMINALS ||--o{ SYNC_BATCHES : "uploads"
    SYNC_BATCHES ||--o{ SYNCED_RECORDS : "contains"
    TERMINALS ||--o{ TERMINAL_SYNC_CURSORS : "tracks position in"
    MASTER_DATA_VERSIONS ||--o{ TERMINAL_SYNC_CURSORS : "acknowledged up to"

    SYNC_BATCHES {
        uuid Id PK
        uuid TerminalId FK
        bigint FirstSequence
        bigint LastSequence "terminal-local counter is the ordering authority"
        string ProtocolVersion "versioned from message one -- three versions run live"
        datetime ReceivedAt "server clock, the only trustworthy timestamp"
        int Status
    }

    SYNCED_RECORDS {
        uuid Id PK
        uuid TerminalId FK
        bigint TerminalSequence "UNIQUE with TerminalId -- the idempotency guarantee"
        uuid RecordId "UUID v7 minted on the terminal, stable across retries"
        string RecordType
        uuid BatchId FK
    }

    MASTER_DATA_VERSIONS {
        uuid Id PK
        string EntityType "Product / PriceList / TaxGroup / PermissionBundle"
        bigint Version "monotonic per tenant and type"
        datetime PublishedAt
    }

    TERMINAL_SYNC_CURSORS {
        uuid Id PK
        uuid TerminalId FK
        string EntityType
        bigint AcknowledgedVersion "advanced on ACK, never on send"
    }

    OUTBOX {
        uuid Id PK
        string Type
        string Payload
        int Status "Pending / Processed / DeadLettered"
        int AttemptCount
        datetime NextAttemptAt "exponential backoff plus jitter"
    }
```

## Sync direction, stated as a constraint

```mermaid
flowchart LR
    subgraph Cloud["Cloud (SQL Server)"]
        M[Master data<br/>products, prices, tax, users]
        T[Transactional store<br/>sales, movements, shifts]
    end

    subgraph Store["Store server (optional)"]
        C[Cache and relay]
    end

    subgraph Till["Terminal (SQLite)"]
        LM[Master data replica<br/>READ ONLY]
        LO[Outbox<br/>APPEND ONLY]
    end

    M -->|"versioned snapshots, DOWN only"| C --> LM
    LO -->|"append-only facts, UP only"| C --> T

    style LM fill:#e8f4ea
    style LO fill:#fdf0e6
```

Neither side ever mutates the same row, so there are effectively **no merge
conflicts**. A store-level price override is not an edit to the central record —
it is a new master-data record published downward. Any proposal for bidirectional
sync of a mutable products table with last-write-wins should be rejected; that is
the design that produces the classic bug where a store's price silently reverts
overnight.

## Inventory (Phase 4)

The ledger and its projection. `STOCK_MOVEMENTS` is append-only and authoritative;
`STOCK_BALANCES` is derived and rebuildable. Note that neither carries `IsDeleted` —
an append-only ledger row cannot be soft-deleted without contradiction.

```mermaid
erDiagram
    WAREHOUSES ||--o{ STOCK_MOVEMENTS : "location of"
    PRODUCT_VARIANTS ||--o{ STOCK_MOVEMENTS : "moved"
    WAREHOUSES ||--o{ STOCK_BALANCES : "holds"
    PRODUCT_VARIANTS ||--o{ STOCK_BALANCES : "valued as"

    STOCK_TRANSFERS ||--o{ STOCK_TRANSFER_LINES : "contains"
    STOCKTAKES ||--o{ STOCKTAKE_LINES : "contains"
    WAREHOUSES ||--o{ STOCK_TRANSFERS : "source"
    WAREHOUSES ||--o{ STOCKTAKES : "counted"

    STOCK_MOVEMENTS {
        uuid Id PK
        uuid TenantId FK
        uuid WarehouseId FK
        uuid VariantId FK
        int Type "Receipt / Sale / TransferOut / Wastage / StocktakeAdjustment"
        decimal QuantityDelta "SIGNED delta, never an absolute balance - ADR 025"
        decimal UnitCostAmount "precision 19,6 - sub-penny, or averages drift"
        string UnitCostCurrency
        decimal TotalCostAmount
        int DocumentType "loose reference - no FK into Sales, per ADR 002"
        uuid DocumentId
        string DocumentNumber
        datetime OccurredAt
        date BusinessDate "trading day, not calendar day - ADR 017"
        uuid TerminalId FK
        uuid UserId FK
        string ReasonCode "required for manual movement types"
    }

    STOCK_BALANCES {
        uuid Id PK
        uuid TenantId FK
        uuid WarehouseId FK
        uuid VariantId FK
        decimal QuantityOnHand "may be NEGATIVE by design - ADR 027"
        decimal AverageUnitCostAmount "full precision, rounded for display only"
        decimal TotalValueAmount
        bytes RowVersion "used only on the cost-changing path - ADR 026"
        datetime LastMovementAt
    }

    STOCK_TRANSFERS {
        uuid Id PK
        uuid TenantId FK
        uuid SourceWarehouseId FK
        uuid DestinationWarehouseId FK
        uuid InTransitWarehouseId FK "stock belongs here between the legs - ADR 028"
        string TransferNumber UK
        int Status "Draft / InTransit / ReceivedWithVariance / Completed"
        datetime DispatchedAt
        datetime ReceivedAt
        string VarianceWriteOffReason "variance persists until explicitly disposed"
    }

    STOCK_TRANSFER_LINES {
        uuid Id PK
        uuid StockTransferId FK
        uuid VariantId FK
        decimal QuantitySent
        decimal QuantityReceived "null until receipt; shortfall stays in transit"
    }

    STOCKTAKES {
        uuid Id PK
        uuid TenantId FK
        uuid WarehouseId FK
        string StocktakeNumber UK
        int Scope "Full / Cycle"
        bool IsBlind "counter cannot see expected quantity - ADR 029"
        int Status "Counting / PendingReview / Posted"
        date BusinessDate
    }

    STOCKTAKE_LINES {
        uuid Id PK
        uuid StocktakeId FK
        uuid VariantId FK
        decimal CountedQuantity
        decimal ExpectedQuantity "captured AT COUNT TIME, not at posting"
        int RecountCount
    }
```

### Index rationale

| Index | Serves |
|---|---|
| `IX_StockMovements_Balance` (Tenant, Warehouse, Variant, OccurredAt) | Balance rebuild and reconciliation — the module's defining query |
| `IX_StockMovements_BusinessDate` | Daily stock reporting and period close |
| `IX_StockMovements_Document` | "Why did this sale not reduce stock?" — the commonest support query |
| `UX_StockBalances_Warehouse_Variant` | Guarantees the relative-UPDATE fast path touches exactly one row |
| `IX_StockBalances_Negative` (filtered) | Cheap negative-stock exception report |
| `IX_StockTransfers_Status` | Finds transfers stuck in transit, which are lost or stolen |

## Sales (Phase 5)

```mermaid
erDiagram
    SHIFTS ||--o{ SALES : "contains"
    SHIFTS ||--o{ CASH_MOVEMENTS : "records"
    SALES ||--|{ SALE_LINES : "has"
    SALES ||--o{ TENDERS : "paid by"
    SALE_LINES ||--o{ PRICE_ADJUSTMENTS : "explained by"
    SALES ||--o| SALES : "reverses"

    SHIFTS {
        uuid Id PK
        uuid TenantId FK
        uuid BranchId FK
        uuid TerminalId FK
        uuid CashierId FK
        decimal OpeningFloat
        date BusinessDate "fixed at open, never derived per sale"
        decimal ExpectedCash
        decimal CountedCash
        decimal Variance "recorded, never silently corrected"
        int Status
    }

    CASH_MOVEMENTS {
        uuid Id PK
        uuid ShiftId FK
        int Kind "Drop / Pickup / PettyCash / Correction"
        decimal Amount "SIGNED delta, order-independent"
        uuid PerformedBy FK
    }

    SALES {
        uuid Id PK "UUID v7, minted at the terminal"
        uuid TenantId FK
        uuid BranchId FK
        uuid TerminalId FK
        uuid ShiftId FK
        string ReceiptSeries "gap-free per terminal, legal artefact"
        bigint ReceiptSequence
        date BusinessDate
        int Status "Open/Suspended/Completed/Cancelled/Voided"
        uuid OwningTerminalId "exclusive ownership while suspended"
        decimal TotalExclusiveTax
        decimal TotalTax
        decimal RoundingAdjustment "cash rounding, kept separate from tax"
        decimal TotalInclusiveTax
        uuid ReversesSaleId FK "null unless this is a void or refund"
    }

    SALE_LINES {
        uuid Id PK
        uuid SaleId FK
        int LineNumber "resequenced on removal, no gaps"
        uuid VariantId "loose reference, NO FK into Catalog"
        string Description "snapshot"
        decimal UnitPrice "snapshot"
        decimal TaxRate "snapshot"
        bool TaxInclusivePricing
        decimal UnitCostAtSale "snapshot, for margin at the time"
        int PriceListVersion "snapshot"
        decimal NetAmount
        decimal TaxAmount
        decimal GrossAmount
    }

    PRICE_ADJUSTMENTS {
        uuid Id PK
        uuid SaleLineId FK
        int Sequence "execution order"
        int Stage "BasePrice..Rounding"
        decimal Amount
        uuid SourceId "promotion or coupon"
        uuid AuthorisedBy FK "who approved the discount"
    }

    TENDERS {
        uuid Id PK
        uuid SaleId FK
        int Method "Cash/Card/GiftCard/Loyalty/StoreCredit"
        decimal Amount
        string Reference "masked PAN only, never full card data"
        uuid PaymentId "loose reference, Phase 6"
    }
```

Everything on a sale line is a **snapshot**. There is deliberately no foreign key from
`SALE_LINES` into Catalog, because catalog data is mutable and a sale is a historical
fact — a live reference would let a price list edit rewrite what a customer was
charged last March.

### Index rationale — Sales

| Index | Purpose |
|---|---|
| `UX_Sales_Terminal_Receipt` on (TenantId, TerminalId, ReceiptSeries, ReceiptSequence) | Enforces gap-free per-terminal numbering; the uniqueness constraint IS the fiscal guarantee |
| `IX_Sales_BusinessDate` on (TenantId, BranchId, BusinessDate) | Every daily report and Z report filters on this |
| `IX_Sales_Status_Suspended` filtered `WHERE Status = 1` | Suspended-sale lookup is frequent and the set is tiny; a filtered index keeps it in memory |
| `IX_SaleLines_Variant` on (TenantId, VariantId) | Product sales history and margin reporting |
| `IX_Sales_Shift` on (ShiftId) | Shift close must total its own sales |

---

## Fiscal (Phase 5 foundation)

The fiscal module is **country-agnostic** (ADR 031). Nothing in this schema encodes a
jurisdiction's rules; the jurisdiction supplies the payload and the status transitions
through plugin seams, and the schema stores the outcome.

```mermaid
erDiagram
    FISCAL_DOCUMENTS ||--o{ FISCAL_TRANSMISSION_ATTEMPTS : "records"
    FISCAL_DOCUMENTS ||--o| FISCAL_DOCUMENTS : "superseded by"

    FISCAL_DOCUMENTS {
        uuid Id PK
        uuid TenantId FK
        uuid CompanyId FK "the taxable person — profile is selected here"
        uuid BranchId FK
        uuid TerminalId FK
        uuid SaleId "loose reference, NO foreign key into Sales"
        string ProfileCode "GENERIC, SA_ZATCA_P2, ... — config, not country"
        int DocumentType "simplified / standard / credit / debit"
        string Series
        bigint Sequence
        string FormattedNumber
        string ContentType "application/xml, application/json"
        varbinary Content "opaque statutory payload"
        string CanonicalHash
        string PreviousDocumentHash "null unless the regime chains"
        string SignatureAlgorithm
        string SignatureValue
        string CertificateThumbprint
        string AuthorityIdentifier "assigned on acceptance"
        string QrPayload
        datetime IssuedAt
        date BusinessDate
        bool IssuedOffline "persisted, not inferred"
        int Status "Issued/Pending/Accepted/Rejected/Superseded"
        datetime TransmittedAt
        datetime TransmissionDueBy "null when no deadline applies"
        uuid SupersededByDocumentId FK
    }

    FISCAL_TRANSMISSION_ATTEMPTS {
        uuid Id PK
        uuid FiscalDocumentId FK
        int AttemptNumber
        datetime AttemptedAt
        string Outcome
        string AuthorityIdentifier
        string MessageCode
        string MessageText
        bool IsRetryable "distinguishes our bad payload from their bad afternoon"
    }
```

`SaleId` carries **no foreign key**, deliberately. Fiscal never references Sales and
Sales never references Fiscal (ADR 033), so the database cannot enforce the
relationship. That is a real cost, and it is paid knowingly: the alternative couples
sale completion to a government web service. The mitigation is a reconciliation report
— sales without documents, documents without sales — which is **mandatory, not
optional**, and is still unbuilt.

`Content` is stored as bytes rather than parsed columns because the core must be able
to store, hash, queue and archive a payload it cannot interpret. Parsing it would
require the core to understand UBL, FatturaPA and CFDI — precisely what ADR 031 forbids.

### Index rationale — Fiscal

| Index | Purpose |
|---|---|
| `UX_FiscalDocuments_Series` on (TenantId, CompanyId, TerminalId, Series, Sequence) | The gap-free guarantee. Uniqueness here *is* the legal numbering constraint, the same way it is for receipts |
| `IX_FiscalDocuments_Pending` filtered `WHERE Status IN (0,1)` | The transmission worker's queue. Filtered because the pending set is tiny relative to history and should stay resident |
| `IX_FiscalDocuments_Overdue` on (TransmissionDueBy) filtered `WHERE TransmissionDueBy IS NOT NULL AND Status IN (0,1)` | Deadline monitoring. This is an **alarm**, not a queue: a store 20 hours into a 24-hour obligation needs support paged before the deadline, not a retry loop |
| `IX_FiscalDocuments_Sale` on (TenantId, SaleId) | Receipt reprint, and the reconciliation report that substitutes for the missing foreign key |
| `IX_FiscalDocuments_BusinessDate` on (TenantId, CompanyId, BusinessDate) | Periodic statutory filing and archive export |

**Not yet built.** There is no `FiscalDbContext`, no EF configuration, and no
migration. The schema above is the design the pipeline implies; it has not been
expressed in code. Recorded in the debt register rather than left implicit.

## Payments (Phase 6)

Payment is its **own aggregate**, not a child of Sale (ADR 041), for the same reason
FiscalDocument is: a sale is complete when the customer has been charged and handed a
receipt, and a payment's lifecycle continues afterwards — settlement lands the next
banking day, a chargeback can arrive months later. Nesting the one inside the other
would force the shorter-lived aggregate to stay loaded for the longer-lived one.

```mermaid
erDiagram
    PAYMENTS ||--o{ PAYMENT_ATTEMPTS : "records"
    PAYMENTS ||--o| PAYMENTS : "refunds"

    PAYMENTS {
        uuid Id PK
        uuid TenantId FK
        uuid BranchId FK
        uuid TerminalId FK
        uuid SaleId "loose reference, NO foreign key into Sales"
        int Kind "Payment / Refund"
        int Status "Initiated/Authorised/Captured/Settled/Declined/Failed/Indeterminate/Voided"
        decimal Amount
        string Currency
        decimal CapturedAmount
        decimal RefundedAmount "accumulated across partial refunds"
        string IdempotencyKey "terminal-generated, unique per tenant"
        string ProviderCode "MANUAL_CARD, ... — config, not a branch in code"
        string ProviderReference "acquirer's handle, null until the provider answers"
        string AuthorisationCode
        string MaskedPan "last four only — never a PAN"
        string Scheme
        int EntryMode "Chip/Contactless/Swipe/Manual/Unknown"
        string InstrumentToken "acquirer token, meaningless outside their vault"
        uuid OriginalPaymentId FK "set on refunds only"
        datetime InitiatedAt
        datetime CompletedAt
        datetime SettledAt
        date BusinessDate
        bool AuthorisedOffline "persisted, not inferred"
        string FailureCode
        string FailureMessage
        binary RowVersion "optimistic concurrency"
    }

    PAYMENT_ATTEMPTS {
        int AttemptNumber
        datetime AttemptedAt
        string Outcome
        string Detail "provider text, kept for disputes"
    }
```

`SaleId` carries **no foreign key**, on the same reasoning as Fiscal and at the same
cost: the database cannot enforce it, so a **Sale ↔ Payment reconciliation report** is
mandatory. That is now the third mandatory reconciliation (Sale↔Fiscal, Sale↔Payment,
Payment↔settlement) and **none of the three is built**. This is the largest documented
debt Phase 6 leaves behind.

There is no `Pan`, `Cvv`, `ExpiryMonth` or `TrackData` column, and there is no way to
add one quietly: `CardDataArchitectureTests` fails the build on those identifiers
(ADR 045). `MaskedPan` holds last four; `InstrumentToken` is the acquirer's token,
which is worthless to an attacker who does not also hold the acquirer's vault.

`PAYMENT_ATTEMPTS` has no surrogate key in the design because it is an owned collection
of the payment, ordered by `AttemptNumber` within it. It exists for support and dispute
evidence, not for querying across payments.

### Index rationale — Payments

| Index | Purpose |
|---|---|
| `UX_Payments_Idempotency` on (TenantId, IdempotencyKey) **unique** | The thing that actually stops a double charge. An application-level "have I seen this key" check races between the lookup and the insert; a unique index does not (ADR 043) |
| `IX_Payments_Sale` on (TenantId, SaleId) | Receipt reprint, refund-to-original-tender lookup, and the reconciliation that substitutes for the missing foreign key |
| `IX_Payments_Unresolved` filtered `WHERE Status = Indeterminate` | The resolution sweep's work queue. Filtered because unresolved payments are rare and must stay resident — every row here is a customer who may have been charged for a sale we have no record of (ADR 044) |
| `IX_Payments_Settlement` on (TenantId, ProviderCode, BusinessDate) INCLUDE (ProviderReference, Amount) | Drives the daily settlement reconciliation without touching the base table |
| `IX_Payments_Original` on (TenantId, OriginalPaymentId) filtered `WHERE OriginalPaymentId IS NOT NULL` | Sums prior refunds when validating a new one |

**Not yet built.** There is no `PaymentDbContext`, no EF configuration, and no
migration — the same gap Sales and Fiscal have. The schema above is the design the
aggregate implies. `RefundedAmount` is persisted rather than derived by summing linked
refunds specifically so that the over-refund check is a single-row read under the
aggregate's own concurrency token, not a query whose result is stale the moment it
returns.

---

## Purchasing & Expenses (Phase 7)

```mermaid
erDiagram
    SUPPLIERS ||--o{ SUPPLIER_PRODUCT_CODES : "lists"
    SUPPLIERS ||--o{ PURCHASE_ORDERS : "receives"
    PURCHASE_ORDERS ||--o{ PURCHASE_ORDER_LINES : "contains"
    PURCHASE_ORDERS ||--o{ PURCHASE_ORDER_APPROVALS : "records"

    SUPPLIERS {
        uniqueidentifier Id PK "UUIDv7"
        uniqueidentifier TenantId FK
        uniqueidentifier CompanyId FK "not branch — the relationship is the legal entity's"
        string Code "normalised uppercase, unique per company"
        string Name
        string Currency "orders inherit; cannot be overridden"
        int PaymentTermDays "current terms — orders snapshot them"
        int LeadTimeDays
        decimal MinimumOrderValue
        bool IsActive "deactivated, never deleted"
        binary RowVersion
    }

    SUPPLIER_PRODUCT_CODES {
        uniqueidentifier VariantId FK
        string Code "the supplier's code, not ours"
        decimal PackSize "our units per supplier order unit"
        string Description
    }

    PURCHASE_ORDERS {
        uniqueidentifier Id PK "UUIDv7"
        uniqueidentifier TenantId FK
        uniqueidentifier CompanyId FK
        uniqueidentifier BranchId FK
        uniqueidentifier WarehouseId "delivery destination"
        uniqueidentifier SupplierId FK
        string OrderNumber "unique per company"
        string Currency "inherited from supplier"
        string Status "Draft|PendingApproval|Approved|Sent|PartiallyReceived|Received|Closed|Rejected|Cancelled"
        int AgreedPaymentTermDays "snapshotted at raise — ADR 048"
        int AgreedLeadTimeDays "snapshotted at raise"
        date ExpectedDeliveryDate "computed once, from the lead time in force that day"
        uniqueidentifier RaisedByUserId "cannot also approve — ADR 050"
        datetime RaisedAt
        datetime SentAt
        binary RowVersion
    }

    PURCHASE_ORDER_LINES {
        int LineNumber "unique within the order"
        uniqueidentifier VariantId FK
        decimal QuantityOrdered
        decimal QuantityReceived "accumulates across partial receipts"
        decimal QuantityCancelled "explicit short-shipment closure — ADR 051"
        decimal UnitPrice
        string SupplierCode "as printed on the order"
    }

    PURCHASE_ORDER_APPROVALS {
        uniqueidentifier ApproverUserId
        string ApproverLevel "Supervisor|Manager|Director"
        bool Approved "false rows are rejections, kept"
        datetime DecidedAt
        string Reason "mandatory on rejection"
    }
```

`OutstandingQuantity` and `OverReceivedQuantity` are **derived, not stored** —
`max(0, ordered − received − cancelled)` and `max(0, received − ordered)`. Storing them
creates two numbers that can disagree with the three they come from, and the disagreement
is always discovered by someone trying to reorder (ADR 051).

Terms are duplicated onto every order deliberately. They are historical facts about an
agreement, not current attributes of a party (ADR 048).

```mermaid
erDiagram
    GOODS_RECEIPTS ||--o{ GOODS_RECEIPT_LINES : "contains"
    GOODS_RECEIPTS ||--o{ GOODS_RECEIPT_LANDED_COSTS : "carries"

    GOODS_RECEIPTS {
        uniqueidentifier Id PK "UUIDv7"
        uniqueidentifier TenantId FK
        uniqueidentifier BranchId FK
        uniqueidentifier WarehouseId
        uniqueidentifier PurchaseOrderId "loose reference — one order, many receipts"
        uniqueidentifier SupplierId
        string ReceiptNumber "unique per branch"
        string Currency
        string SupplierDeliveryNote "their reference, for disputes"
        uniqueidentifier ReceivedByUserId
        datetime ReceivedAt
        date BusinessDate
        string Status "Draft|Posted"
        datetime PostedAt "immutable once set — ADR 006"
        binary RowVersion
    }

    GOODS_RECEIPT_LINES {
        int PurchaseOrderLineNumber "which order line this satisfies"
        uniqueidentifier VariantId FK
        decimal QuantityReceived
        decimal UnitPrice "what the supplier charged on this delivery"
    }

    GOODS_RECEIPT_LANDED_COSTS {
        string Type "Freight|Duty|Insurance|Handling|Other"
        decimal Amount
        string Reference "haulier invoice, C88, etc."
        string AllocationBasis "Value|Quantity|Even"
    }
```

There is **no landed unit cost column**. It is computed at posting —
`(line value + allocated share) ÷ quantity` — and handed to Inventory, which stores it on
the movement. Persisting it here as well would create a second copy of a figure that must
match the stock ledger, and the two would eventually diverge.

`PurchaseOrderId` carries no foreign key, on the now-familiar reasoning: it keeps the
modules independently deployable, and it means a **receipt ↔ order reconciliation** is the
database's substitute for the constraint it cannot enforce.

```mermaid
erDiagram
    PURCHASE_INVOICES ||--o{ PURCHASE_INVOICE_LINES : "contains"
    SUPPLIER_RETURNS ||--o{ SUPPLIER_RETURN_LINES : "contains"

    PURCHASE_INVOICES {
        uniqueidentifier Id PK "UUIDv7"
        uniqueidentifier TenantId FK
        uniqueidentifier CompanyId FK
        uniqueidentifier SupplierId FK
        uniqueidentifier PurchaseOrderId "loose reference"
        string SupplierInvoiceNumber "unique per supplier per company — stops double payment"
        string Currency
        date InvoiceDate
        date DueDate "from the order's snapshotted terms"
        string Status "Recorded|Matched|Blocked|Approved|Paid"
        string BlockReason "the variances, in words"
        uniqueidentifier ApprovedByUserId "also the overrider, if overridden"
        datetime ApprovedAt
        binary RowVersion
    }

    PURCHASE_INVOICE_LINES {
        int PurchaseOrderLineNumber
        uniqueidentifier VariantId FK
        decimal Quantity "matched against receipts — ADR 053"
        decimal UnitPrice "matched against the order — ADR 053"
    }

    SUPPLIER_RETURNS {
        uniqueidentifier Id PK "UUIDv7"
        uniqueidentifier TenantId FK
        uniqueidentifier BranchId FK
        uniqueidentifier SupplierId FK
        uniqueidentifier OriginalGoodsReceiptId "nullable — not every return has one"
        string ReturnNumber
        string Currency
        string Reason "Damaged|WrongItem|Overstock|Expired|QualityRejection|Other"
        string Status "Draft|Dispatched|PartiallyCredited|Credited|Cancelled"
        datetime DispatchedAt
        string CreditNoteNumber "the supplier's reference"
        decimal CreditedAmount "as received, not as expected — ADR 054"
        date CreditNoteDate
        binary RowVersion
    }

    SUPPLIER_RETURN_LINES {
        uniqueidentifier VariantId FK
        decimal Quantity
        decimal UnitCost "from the balance at dispatch, not from the original receipt"
    }
```

`CreditedAmount` is stored separately from the value of the return lines precisely so the
two can disagree. `CreditShortfall` — expected minus credited — is the number that
recovers money, and it only exists because nothing forces them to match (ADR 054).

```mermaid
erDiagram
    EXPENSES {
        uniqueidentifier Id PK "UUIDv7"
        uniqueidentifier TenantId FK
        uniqueidentifier CompanyId FK
        uniqueidentifier BranchId FK "unlike suppliers — the electricity bill is the site's"
        string ExpenseNumber
        string Category "Freight|CustomsDuty|Rent|Utilities|...|Other"
        decimal Amount "net"
        decimal TaxAmount "separate — usually recoverable, so not a cost"
        string Currency
        date IncurredOn
        string Description
        uniqueidentifier SupplierId "nullable"
        uniqueidentifier LinkedGoodsReceiptId "nullable; only Freight and CustomsDuty may set it"
        uniqueidentifier RecordedByUserId "cannot also approve — ADR 055"
        string Status "Recorded|Approved|Rejected"
        uniqueidentifier ApprovedByUserId
        binary RowVersion
    }
```

`IsCapitalised` is derived from `LinkedGoodsReceiptId IS NOT NULL`, so an expense cannot
be attached twice or counted twice. There is no "capitalisable" flag column: whether a
category may reach stock is a property of the category, held as a closed list in code
rather than as data a user can edit (ADR 055).

### Index rationale — Purchasing & Expenses

| Index | Purpose |
|---|---|
| `UX_Suppliers_Code` on (TenantId, CompanyId, Code) **unique** | Code is normalised uppercase on write so lookups are predictable and "acme" cannot coexist with "ACME" |
| `UX_SupplierProductCodes_Variant` on (SupplierId, VariantId) **unique** | One supplier code per variant. The reverse is not unique — one supplier code legitimately covers several variants |
| `UX_PurchaseOrders_Number` on (TenantId, CompanyId, OrderNumber) **unique** | Order numbers are quoted to suppliers; duplicates make every phone call ambiguous |
| `IX_PurchaseOrders_Outstanding` filtered `WHERE Status IN (Sent, PartiallyReceived)` | The goods-inwards work queue and the replenishment feed. Filtered because closed orders vastly outnumber open ones and only open ones are ever asked about |
| `IX_PurchaseOrders_Supplier` on (TenantId, SupplierId, RaisedAt) | Supplier spend analysis and the "what is coming from them" question |
| `IX_GoodsReceipts_Order` on (TenantId, PurchaseOrderId) | Substitutes for the absent foreign key; also assembles the receipt set for three-way matching, where an incomplete set produces a false block |
| `IX_GoodsReceipts_Unposted` filtered `WHERE Status = Draft` | Deliveries booked in but never posted — stock physically present and invisible to the system |
| `UX_PurchaseInvoices_SupplierNumber` on (TenantId, CompanyId, SupplierId, SupplierInvoiceNumber) **unique** | The commonest expensive mistake in accounts payable is paying the same invoice twice. A constraint, not a rule someone has to remember (ADR 053) |
| `IX_PurchaseInvoices_Blocked` filtered `WHERE Status = Blocked` | The exceptions queue. Every row is money held back and a supplier who will telephone |
| `IX_PurchaseInvoices_Due` on (TenantId, Status, DueDate) | Payment run selection and ageing |
| `IX_SupplierReturns_Uncredited` filtered `WHERE Status IN (Dispatched, PartiallyCredited)` | **The report that recovers real money** — goods gone back, credit not received (ADR 054) |
| `IX_Expenses_Branch` on (TenantId, BranchId, IncurredOn) | Per-site expense reporting, which is how expenses are almost always asked about |
| `IX_Expenses_Unapplied` filtered `WHERE LinkedGoodsReceiptId IS NOT NULL` | Freight linked to a delivery but never applied as a landed cost — a gap ADR 055 creates and does not close |

**Not yet built.** There is no `PurchasingDbContext`, no `ExpensesDbContext`, no EF
configuration and no migration. Everything above is the design the aggregates imply, and
it joins Sales, Fiscal and Payments in the deferred infrastructure milestone (ADR 046).
