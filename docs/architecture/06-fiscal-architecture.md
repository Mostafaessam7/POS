# Fiscal Architecture — Country-Agnostic Core with Pluggable Jurisdictions

**Phase:** 5 (foundation) · **Date:** 21 July 2026 · **ADRs:** 031, 032, 033

---

## The principle

The core domain contains **no fiscal rules and no country names**. It holds an
`IFiscalProfile` and asks it questions. Adding Egypt or ZATCA means adding an
assembly; it does not mean touching `Sale`, `Inventory`, or any existing plugin.

The test for whether this holds: **a `country == "XX"` comparison anywhere outside a
country plugin is a defect in the abstraction.** If a jurisdiction cannot be expressed
through the seams, the fix is a new capability flag or a new seam — never a special
case in the core.

---

## Where fiscal behaviour extends — the seam catalogue

Six narrow interfaces rather than one fat provider, because jurisdictions vary along
*independent* axes. Portugal needs chained signing but only periodic filing. Egypt
needs per-document transmission but no chaining. A no-mandate country needs numbering
and nothing else. One interface would force every plugin to implement — and every
reviewer to read — methods that throw `NotSupportedException`.

| # | Seam | Responsibility | Optional? |
|---|---|---|---|
| 1 | `IFiscalNumberingStrategy` | Allocates the legal document number and series | Required |
| 2 | `IFiscalDocumentBuilder` | Maps neutral context → statutory payload (UBL, FatturaPA, CFDI, JSON) | Required |
| 3 | `IFiscalSigner` | Cryptographic signature, stamp, or hash chaining | Optional |
| 4 | `IFiscalTransmitter` | Clearance or reporting to the authority, idempotent | Optional |
| 5 | `IFiscalQrGenerator` | Receipt QR payload | Optional |
| 6 | `IFiscalArchiveExporter` | Periodic statutory files (SAF-T, JPK) | Optional |

Plus two supporting seams:

| Seam | Responsibility |
|---|---|
| `IFiscalProfileRegistry` | Resolves profile by code; **errors** rather than falling back to GENERIC |
| `IFiscalSequenceAllocator` | Durable, monotonic number allocation surviving power loss mid-sale |

**Absence is expressed as `null`, not as a throwing stub.** `Signer`, `Transmitter`,
`QrGenerator` and `ArchiveExporter` are nullable on `IFiscalProfile`, so "this
jurisdiction has no such concept" is visible at compile time rather than at runtime.

### Capabilities — how the core branches without knowing countries

```
FiscalCapabilities
├── OfflineIssuance          Permitted | PermittedWithDeferredClearance | Prohibited
├── TransmissionModel        None | PostAuditReporting | Clearance | PeriodicFiling | CertifiedDevice
├── TransmissionDeadline     TimeSpan? — drives an operational alarm, not just retry
├── RequiresSignature        bool
├── RequiresDocumentChaining bool — document N needs N−1's hash
├── RequiresQrCode           bool
├── RequiresCertifiedDevice  bool — the legal record is hardware, we reconcile against it
└── CorrectionByCreditNoteOnly bool
```

Capabilities are resolved **per document type, not per country**. In Saudi Arabia a
simplified B2C invoice may be issued at the till and reported afterwards, while a
standard B2B invoice must be cleared first. Modelling capability at country
granularity would force the stricter rule onto ordinary retail and destroy offline
selling for no legal reason.

---

## The pipeline

One jurisdiction-neutral orchestrator, `FiscalisationPipeline`, knows the ordering
rules and nothing else:

```
validate → resolve document type → OFFLINE GATE → allocate number
   → build payload → sign → transmit (if clearance) → QR → persist
```

Two ordering constraints are load-bearing:

**The offline gate runs before numbering.** A gap-free series must not burn a number
on a document we are about to refuse — a gap is itself a compliance finding in most
regimes.

**QR generation runs after transmission.** In clearance regimes the QR may incorporate
the authority's response; in reporting regimes it does not. The pipeline honours both
by ordering, not by branching on country.

---

## The offline collision, stated plainly

This is the one place where the platform's defining constraint (sell with no network)
meets a legal constraint that contradicts it. Under a clearance model the authority
must approve *before* issuance. No architecture obtains a government signature
without connectivity.

The conflict is narrower than it looks. Most POS volume is B2C, and most regimes treat
B2C simplified invoices permissively. The strict path generally applies to B2B
invoices naming a registered buyer.

| Situation | Behaviour |
|---|---|
| Offline, `Permitted` | Issue normally; transmit when connectivity returns |
| Offline, `PermittedWithDeferredClearance` | Issue; must clear within deadline; rejection → credit note |
| Offline, `Prohibited` | **Refuse this document type.** Offer a simplified receipt instead |
| Offline, signature required, `CanSignOffline == false` | Refuse — never issue unsigned |

The system never issues a document it knows to be invalid and reconciles later.
Offline B2C retail remains fully available in every regime examined; B2B under
clearance genuinely requires connectivity, and that is a product limitation to state
in sales material rather than an engineering problem to solve.

---

## Jurisdiction mapping — how the named regimes land on the seams

Drawn from published requirements. Each is a **design hypothesis to be validated when
the plugin is built**, not a compliance statement, and each needs local tax counsel
sign-off before go-live.

| Jurisdiction | Model | Offline (B2C) | Signing | Chaining | QR | Archive |
|---|---|---|---|---|---|---|
| **GENERIC** (no mandate) | None | Permitted | — | — | — | — |
| **Egypt** (ETA) | Post-audit reporting | Permitted | Yes | No | Receipt-level | — |
| **Saudi Arabia** (ZATCA Ph.2) | Simplified: reporting (24h)<br>Standard: **clearance** | Simplified: Permitted<br>Standard: **Prohibited** | Yes, device CSID → `CanSignOffline = true` | Previous-invoice hash | TLV base64 | — |
| **UAE** (e-invoicing programme) | Expected reporting/PEPPOL-style | Expected Permitted | Yes | TBD | TBD | — |
| **Italy** (SdI) | B2B **clearance**; retail via RT device | B2B **Prohibited** | Yes | No | No | — |
| **Portugal** (AT) | Periodic filing + certified software | Permitted | Yes, **chained** | **Yes** | ATCUD + QR | **SAF-T (PT)** |
| **Poland** (KSeF) | Clearance-style; online cash registers for retail | B2B **Prohibited**; retail via certified device | Yes | No | — | **JPK** |
| **LatAm** (MX CFDI, BR NF-e, CL DTE) | **Clearance**, often via accredited intermediary | **Prohibited** | Yes | Varies | Yes | Varies |

Two structural patterns fall out, and both are already modelled:

**Certified-device regimes** (Italy RT, Poland online registers) invert the
relationship: the legal record is produced by *hardware we drive*, and our document is
a commercial copy. `RequiresCertifiedDevice` marks this; the integration work is
device drivers plus a reconciliation report, not an API client.

**Intermediary regimes** (Mexico's PACs, Brazil's accredited providers) route through
an accredited third party. This needs no new seam — `IFiscalTransmitter` targets the
intermediary instead of the authority.

---

## What a new plugin costs

Minimum viable jurisdiction: implement `IFiscalNumberingStrategy` and
`IFiscalDocumentBuilder`, declare capabilities, register in DI. That is the GENERIC
profile, and it is a real working implementation — deliberately, so the abstraction is
exercised end to end rather than being an untested extension model whose only
implementation does nothing.

A full mandate plugin (ZATCA-class) adds a signer, an idempotent transmitter, and a QR
generator: roughly four classes, a certificate provisioning flow, and a conformance
test suite against the authority's sandbox.

```
src/Modules/Fiscal/
├── POS.Fiscal.Abstractions/     seams, capabilities, context, errors  ← plugins depend only on this
├── POS.Fiscal.Domain/           FiscalDocument aggregate
├── POS.Fiscal/                  pipeline, store contracts
├── POS.Fiscal.Generic/          the no-mandate profile
└── POS.Fiscal.{Sa,Eg,It,Pt,Pl}/ future — one per regime
```

Dependency direction: plugins → Abstractions only. Nothing depends on the pipeline
except the composition host. This is why `FiscalErrors` lives in Abstractions rather
than beside the pipeline.

---

## Deliberate non-goals

- **No runtime assembly scanning from a plugins folder.** Loading unsigned code that
  produces legally binding documents is a supply-chain risk far larger than the
  deployment convenience it buys, and plugins ship on our release cadence anyway.
- **No tax *calculation* engine here.** Rate determination stays in Catalog/Sales.
  This module concerns the legal *document*, not what the tax is.
- **No claim of compliance.** The seams are shaped to accommodate these regimes. Actual
  compliance requires per-country counsel, conformance testing against each authority's
  sandbox, and in several cases formal software certification.

---

## Open risks

**The decomposition is a bet.** Six seams derived from published requirements, not from
a shipped plugin. The first real implementation is the test, and I would build ZATCA
first precisely because it exercises the most seams — device signing, chaining, QR,
and both transmission models in one jurisdiction.

**Certified-device regimes may need more than a flag.** Italy and Poland may prove to
need their own host-side abstraction for device drivers. Deferred until a real
requirement exists rather than speculatively designed.

**Deadline monitoring is unbuilt.** `TransmissionDeadline` is modelled and
`IsOverdue` exists, but the alarm that tells support a store is 20 hours into a
24-hour obligation is Phase 5 work.

**Referential integrity across the Sale/FiscalDocument boundary is not enforced.**
Deliberate (ADR 033), but it makes a reconciliation report — sales without documents,
documents without sales — mandatory rather than optional.
