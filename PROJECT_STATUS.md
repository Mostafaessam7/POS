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
4. **`npm audit` advisory on `react-router-dom`** — accepted, not fixed, scoped to RSC
   (not in use here). Re-check when upgrading react-router-dom.

None of the remaining engineering-only items from the previous list are open — voids,
held sales, discounts, and Users & Roles pagination all shipped this session (see §1 and
§3). What's left is entirely business-blocked (items 1–3 above) or a low-priority
housekeeping note (item 4).

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

---

*Last updated: 2026-08-05, after finding and fixing a real regression left by the
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
