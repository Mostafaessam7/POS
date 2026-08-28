# POS.BackOffice

The web back office for the POS system — the admin surface where products, purchasing, inventory,
expenses, reconciliation, users and settings are managed. React 19 + TypeScript on Vite.

> This is **not** the cashier application. The till-side experience is `POS.TerminalAgent`
> (`src/Hosts/POS.TerminalAgent`), which is still an early scaffold. The `/register` route in this
> app is a browser-based register screen that talks to the API over the network — see
> [Scope](#scope) before assuming it covers offline till behaviour.

## Running it

```bash
npm ci
npm run dev
```

The dev server expects the API at `http://localhost:9850`. Point it elsewhere with
`VITE_API_BASE_URL` (see `src/api/client.ts`):

```bash
VITE_API_BASE_URL=https://api.example.com npm run dev
```

Other scripts:

| Command | What it does |
| --- | --- |
| `npm run build` | Type-checks (`tsc -b`) then produces a production bundle. Both must pass. |
| `npm run lint` | Oxlint over the project. |
| `npm run preview` | Serves the built bundle locally to check a production build. |

## Structure

| Path | Contents |
| --- | --- |
| `src/pages/` | One component per route — Dashboard, Products, Purchasing, Inventory, Expenses, Reconciliation, Users, Settings, Login, Register. |
| `src/api/` | Typed fetch wrappers per resource, over a shared `client.ts`. |
| `src/auth/` | `AuthContext`, `ProtectedRoute`, and token storage. |
| `src/i18n/` | English and Arabic translation tables plus the language context. |
| `src/layouts/` | `AppLayout` — the authenticated shell (nav + outlet). |
| `src/components/` | Shared presentational pieces, including the Vanta background wrappers. |

### Auth

`client.ts` holds the access token in memory and refreshes it against
`/api/v1/auth/refresh` on a 401, retrying the original request once. Routes are wrapped in
`ProtectedRoute`, so an unauthenticated visitor is redirected to `/login` rather than seeing an
empty shell.

### Localisation

English and Arabic ship in `src/i18n/translations.ts`. Adding a language means adding a key
alongside `en`/`ar` — the context reads whatever is there.

## Scope

Worth being explicit about, because the route list makes this app look more complete than it is:

- **`/register` is online-only.** It is a normal web page making normal HTTP calls. It has no
  offline queue, no local database, and no hardware integration (receipt printer, cash drawer,
  PIN pad, dedicated scanner). A till that loses connectivity mid-sale stops working. Offline-first
  till behaviour is `POS.TerminalAgent`'s job — see the ADRs in `docs/adr/` and `PROJECT_STATUS.md`
  for the intended split and what is actually built today.
- Several back-office screens are backed by domain logic that is deliberately partial (payments go
  through a manual provider, fiscal profiles are generic, reconciliation compares but does not
  import bank statements). `PROJECT_STATUS.md` is the honest per-area status; prefer it over
  inferring completeness from the presence of a page.

## Notes

- `src/types/` holds hand-written declarations for `vanta` and `vanta/dist/vanta.halo.min`, which
  ship no types of their own. There is no `vite-env.d.ts` in this project, so `import.meta.env` is
  untyped — a misspelled `VITE_` variable reads as `undefined` at runtime rather than failing
  `tsc -b`. Worth adding one if more environment variables appear.
- `three` and `vanta` are pinned together — Vanta reaches into Three's internals, so bumping one
  without the other tends to break the background at runtime rather than at build time.
