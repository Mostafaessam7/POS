# PROJECT STATUS — what remains

> **New chat session? Read this file first.** It's a short, prioritised list of what's
> actually missing, kept current as of the date at the bottom. For the full technical
> deep-dive (architecture, API surface, testing status, every decision made and why),
> read [HANDOVER.md](HANDOVER.md) — this file is the "what's left to do" summary that
> sits on top of it. If the two disagree, trust whichever has the later date.

## Quick start (verify this file is still accurate)

```bash
dotnet build POS.sln -c Release
dotnet test tests/POS.UnitTests -c Release
dotnet test tests/POS.ArchitectureTests -c Release
dotnet test tests/POS.IntegrationTests -c Release
```
```bash
cd src/Frontend/POS.BackOffice && npm run build
```
All must be clean. Test tenant for manual poking: subdomain `testtenant-019fc883`,
`admin@example.com` / `ChangeMe123!` (SQL Server must already have this tenant seeded —
see HANDOVER.md §2 if not).

---

## 1. The biggest remaining gap: no real cashier hardware app

A **browser-based register screen now exists** — `/register` in the React back-office
(`src/Frontend/POS.BackOffice/src/pages/RegisterPage.tsx`), backed by a real
synchronous checkout API (`POST /api/v1/sales`, `src/Hosts/POS.Api/Endpoints/SalesEndpoints.cs`).
It opens/closes cash shifts, rings up a sale against live catalog prices, posts stock,
issues a fiscal document, and records electronic-tender payments — all verified against
a real SQL Server, not just unit-tested.

**What it is NOT**: an offline-first, hardware-integrated till. Specifically missing:

- **No offline capability at all.** The register endpoint requires the browser to be
  online; there is no local storage / service worker / sync-on-reconnect. The *backend*
  already has a separate offline path (`POST /sync/batches` — a disconnected terminal
  uploads a completed sale later), but nothing on the frontend drives it.
- **No hardware integration** — barcode scanner is supported only as "types into a
  search box and sends Enter" (works with any USB/BT scanner in keyboard-wedge mode,
  which is most of them, but there's no dedicated scan API). No receipt printer, no
  cash drawer kick, no card-terminal (PIN pad) integration.
- **No real card authorization.** Selecting "Card" at checkout just records the tender
  as already-settled — see §2, `ManualCardProvider` is the only provider that exists.
- Building an actual dedicated terminal app (native / Electron / fixed-hardware browser
  kiosk) is explicitly out of scope until someone picks a platform — see HANDOVER.md §7.

**Known simplifications in the checkout endpoint itself** (documented in
`SalesEndpoints.cs`'s own remarks — read those before touching it):
- Price resolution uses `ProductVariant.DefaultPrice` only — no `PriceList` resolution
  (branch/customer-group/effective-date priority). Nothing in the codebase resolves a
  price list yet; this would be the first.
- No promotions (data-driven campaigns) wired into the UI — the `PricingPipeline`
  supports a `PromotionStage`, but nothing populates it from a `Promotion` table because
  no such table/admin screen exists yet. Manual line and order discounts (see below)
  **are** wired end-to-end, this is specifically about rule-driven promotions.
- Receipt numbering is "max sequence + 1, guarded by the unique index, retry on
  conflict" rather than a dedicated gap-free allocator. Safe, but not how a
  high-concurrency till would want to do it.
- `TerminalId` is a random GUID generated once per browser and stored in
  `localStorage` (`src/Frontend/POS.BackOffice/src/api/terminal.ts`) — there's no
  concept of a provisioned `Terminal` row being checked against it.
- **Void has no fiscal/payment reversal** — `POST /{id}/void` reverses stock
  (`StockPostingKind.CustomerReturn`) and marks the sale `Voided`, but does not issue a
  credit note against the original fiscal document or reverse the recorded payment. Fine
  for the current no-real-fiscal-authority/no-real-acquirer state (see §2 item 1); will
  need revisiting once either exists.
- **Held sales are frozen once parked** — `POST /{id}/resume` loads a suspended sale
  back into the register UI read-only (no add/remove lines, no re-editing discounts);
  the cashier can only pick a payment method and complete or cancel the resume. Editing
  a held basket requires cancelling the resume and re-ringing it as a new sale.
- Void reason is logged, not persisted — `Sale` has nowhere to store it yet (no
  `VoidReason` column), so `POST /{id}/void`'s `Reason` field only reaches the log.

## 2. Everything else, in priority order

1. **A country/regulator, a card acquirer, a bank** — fiscal transmission, a real
   online-gateway payment provider, and settlement reconciliation are all
   code-complete up to the point where they need a named external counterparty. None
   of this can proceed further without a business decision (see HANDOVER.md §7).
2. **A deployment target.** No CD pipeline, no Infrastructure-as-Code, no
   secrets-management integration. Depends on which cloud (or on-prem) gets chosen.
3. **A Docker-capable environment.** `Dockerfile` / `docker-compose.yml` exist and are
   reviewed but have never actually been built — no session so far has had a Docker
   daemon available.

None of the remaining engineering-only items from the previous list are open — voids,
held sales, discounts, and Users & Roles pagination all shipped this session (see §1 and
§3). What's left is entirely business-blocked (items 1–3 above). The `npm audit`
advisory on `react-router-dom` this file used to list as a fourth item is gone —
re-checked 2026-08-23, `npm audit` in `src/Frontend/POS.BackOffice` now reports **0
vulnerabilities** (previously one accepted RSC-scoped advisory).

**Regression found and fixed today (2026-08-05):** `dotnet test tests/POS.IntegrationTests`
was NOT actually clean as this file claimed — 6 of 136 tests in
`UserManagementApiTests.cs` failed with a `JsonException` deserializing `GET /users` and
`GET /roles`. Root cause: when Users & Roles pagination shipped (previous session), both
endpoints started returning a `PagedResponse<T>` envelope (`{ Items, Page, PageSize,
TotalCount }`) instead of a bare array, but the integration tests were never updated and
still deserialized the response as `List<T>` directly. Fixed by changing every affected
`ReadFromJsonAsync<List<...>>()` call in `UserManagementApiTests.cs` to
`ReadFromJsonAsync<PagedResponse<...>>()` and reading `.Items`. All 136 integration tests
pass again, confirmed by a full rerun (not just the affected file). Lesson: a backend
response-shape change must grep for every test deserializing that route, not just update
the endpoint and its own describing test. Frontend (`npm run build`) also reconfirmed
clean; `npm audit`'s `react-router-dom` finding is unchanged (still the one accepted RSC
advisory, still not applicable to this SPA).

## 3. What's solid (don't re-litigate these)

- **Backend**: 9 modules, full transaction lifecycle including checkout, hold/resume,
  void, and manual line/order discounts; 336 unit / 15 architecture / 136+ integration
  tests, all green.
- **Register UI** (`/register`, `RegisterPage.tsx`): category filters, barcode-scan
  search, quantity steppers, quick-cash buttons, cash/card toggle, receipt — plus, as of
  this session: **Hold** (park the current basket), a **Held sales** panel to resume a
  parked basket and complete or cancel it, per-line and order-level **discount** inputs
  wired through to the pricing pipeline, and a **Recent sales** panel to **void** a
  completed sale with a reason.
- **Users & Roles** (`/users`): both `GET /users` and `GET /roles` are server-side
  paginated (`?page=&pageSize=`, default 20/page) and the screen renders a pager for
  each table once there's more than one page.
- **Back-office frontend**: every module has a screen (Dashboard, Products, Purchasing,
  Inventory, Expenses, Reconciliation, Users & Roles, Settings, Register/POS).
- **Localization**: full Arabic + English with RTL layout mirroring
  (`src/Frontend/POS.BackOffice/src/i18n/`). All chrome and all page content is
  translated, including every string added this session (hold/resume/void/discount/pager
  labels).
- **Theming**: light/dark mode toggle, persisted, respects system preference on first
  load (`src/Frontend/POS.BackOffice/src/theme/`).
- **Auth**: HttpOnly refresh-token cookie, real server-side logout, access token never
  touches `localStorage`.

## 4. Decisions adopted (workspace-level, affecting this project)

| Decision | What it means here |
|---|---|
| **POS and PosFlow are separate products — do not merge** | PosFlow is the mature POS product; this one stays independent with its own roadmap. Neither should be refactored toward the other |
| **Azure** is the primary deployment target | Not wired here yet |
| **Azure Key Vault** for production secrets | Not wired here yet |
| **No Redis here** | Redis was scoped to PosFlow / Gym Manager / RealEstateCRM only. This project does not carry the load that justifies it |
| **App Insights (backend) + Sentry (frontend)** | Not installed here yet. Current observability is Seq (logs) + Jaeger (traces) via OpenTelemetry |
| **Amber Commerce theme** | This product's identity on the shared `MeCodex/design-system` token architecture |

## 5. Recent work not covered above (2026-08-29)

These landed after the sections above were last revised, and were undocumented until this pass:

- **Shared design system, Amber Commerce theme** — colour now comes from
  `MeCodex/design-system`. Token *names* are identical across every product theme, so components
  stay portable. The existing light/dark toggle and its system-preference behaviour are unchanged.
- **Baseline security response headers** added.
- **Vulnerability scan actually gates now.** It previously could pass with vulnerabilities present:
  `dotnet list package --vulnerable` exits 0 even when it finds something, so a step that only
  checked the exit code reported problems and went green. It now inspects the output.
- **Dependabot** configured.
- **BackOffice documented** — see the commit for detail.

## 6. Deliberately deferred (and why)

| Item | Why |
|---|---|
| **Offline-first hardware till** | The single biggest gap, and known — see §1. A browser register exists and works against a real API; an offline, hardware-integrated till is a different product surface, not an increment |
| **Merging with PosFlow** | Explicit decision: two separate products |
| **Redis** | Scoped to the three products that need it. Adding it here would be complexity without a load problem to solve |
| **`scaffold.sh` / `generate-projects.py` kept, not deleted** | One-time bootstrap scripts whose output (`POS.sln` + 32 `.csproj`) is committed. Checked before deciding: only the initial commit ever touched those files, so re-running is not destructive, and the scripts document how the project structure was generated. The README already states they are not part of the build loop |

---

*Re-verified 2026-08-29, during the workspace-wide cleanup pass. `dotnet build POS.sln -c Release`
(**0 warnings**, 0 errors), `POS.UnitTests` (**336/336**), `POS.ArchitectureTests` (**15/15**),
`POS.IntegrationTests` against a real SQL Server (**136/136**), and `npm run build` in
`src/Frontend/POS.BackOffice` (clean; the same pre-existing "chunk larger than 500 kB" advisory,
which is a warning not an error). Every number in this file still holds. Added sections 4-6 above:
the last four commits — the shared design system, security headers, Dependabot, and the
vulnerability-scan gate fix — were documented in neither this file nor HANDOVER.md until now.*

*Previously re-verified 2026-08-27, independently, in a fresh review session (no HANDOVER.md/
PROJECT_STATUS.md claim was taken on faith — each was checked against the actual
repository). `dotnet build POS.sln -c Release` (0 warnings, 0 errors), `POS.UnitTests`
(336/336), `POS.ArchitectureTests` (15/15), `POS.IntegrationTests` against a real local
SQL Server (136/136), `npm run build` in `src/Frontend/POS.BackOffice` (clean, same
pre-existing chunk-size advisory), and `npm audit` (0 vulnerabilities) — every number in
this file and HANDOVER.md still holds exactly. Three commits landed after the
2026-08-23 entry below (`b42706b`, `4a3d477`, `378915d`, dated 2026-08-24 and
2026-08-26): the "Mecodex" brand-asset kit was added and wired in as the app's favicon/
logo mark (tab title now "Mecodex POS"). These are cosmetic only — no API, schema, or
test-surface change — and were already correctly logged in HANDOVER.md §9 by the
commits themselves; this file did not previously mention them, which is now fixed.
Docker Desktop's daemon is not running in this review environment, so the Testcontainers
fallback path and the API `Dockerfile`/`docker-compose.yml` remain unbuilt/unverified
here too, same as every prior entry — the integration suite passed via a reachable local
SQL Server instance instead, per its documented fallback order. No regressions, no
factual corrections needed to either file's substance beyond this note and one internal
cross-reference fixed in HANDOVER.md §6 (item 1 there still said "should become a real
ADR" after §1 had already recorded it as ratified ADR 057 — now reconciled).*

*Re-verified 2026-08-23, no code changes since the entry below — this was a clean
re-run, not a work session. All four suites plus the frontend build were run again from
scratch: `dotnet build POS.sln -c Release` (0 warnings, 0 errors), `POS.UnitTests`
(336/336), `POS.ArchitectureTests` (15/15), `POS.IntegrationTests` against a real SQL
Server (136/136), and `npm run build` in `src/Frontend/POS.BackOffice` (clean; one
pre-existing "chunk larger than 500kB" advisory, not an error). Every number in this
file and in HANDOVER.md still holds. The one thing that changed on its own:
`npm audit` now reports 0 vulnerabilities — the `react-router-dom` advisory §2 item 4
used to accept has since been resolved upstream (see the note above, in place of the
old item 4). Also found, while re-checking, a real documentation gap unrelated to this
regression: two frontend files existed with no mention anywhere in HANDOVER.md or
README.md — `VantaBackground.tsx`/`VantaHeroBackground.tsx` (an animated login/dashboard
background) and `useCountUp.ts` (the animated count-up on the dashboard's stat tiles).
Purely cosmetic, no API/behaviour change, but genuinely undocumented until now — see
HANDOVER.md §9.

*Previously updated: 2026-08-05, after finding and fixing a real regression left by the
previous session's Users & Roles pagination work: `UserManagementApiTests.cs` still
deserialized `GET /users`/`GET /roles` as a bare list instead of the new `PagedResponse<T>`
envelope, so 6 of 136 integration tests were silently failing despite this file claiming
"all green" — see the note above. All four suites plus the frontend build are
independently reconfirmed clean as of this update.*

*Previously updated: 2026-08-04, after implementing voids, hold/resume, manual line/order
discounts, and Users & Roles pagination — the four engineering-only gaps from the
previous version of this file — and wiring all four into the actual UI (not just the
API). Also fixed a matching persistence bug in `Sale.Open()`
(`AmountTendered`/`ChangeGiven` never initialized, same class of bug as the earlier
`Shift.Open()` fix, found by the first-ever real `Sale.Suspend()` hitting the database)
and an EF Core cascade-tracking bug where a newly-added `Tender` on an
already-tracked `Sale` (the resume/complete-held path) was misclassified as `Modified`
instead of `Added` — see the remarks in `Sale.cs` and `SalesEndpoints.cs`.*
