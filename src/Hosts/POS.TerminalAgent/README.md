# POS.TerminalAgent

A .NET worker service installed on each till. It is the reason this is a
"web-based POS" that can actually sell during an outage.

## Why this exists

The brief asks for a web-based, offline-first POS that drives a cash drawer,
receipt printer, barcode scanner, scale and card terminal. A browser cannot do
that:

- **WebUSB / WebHID / Web Serial are Chromium-only.** No Safari, no Firefox, no
  iOS. Apple has consistently declined to implement them.
- **ESC/POS printers and cash drawers need raw byte-level device access.** The
  kick-drawer command is a byte sequence sent to the printer's DK port.
- **P2PE payment terminals** expose a local TCP or serial SDK, not a web API.
- **IndexedDB is not a durable transaction store.** Safari evicts it after seven
  days of inactivity. It is not a place to keep the only copy of a cash sale.

So the browser stays a thin client, and this agent is its server.

```
  React PWA  ──HTTPS──▶  Terminal Agent (localhost:5001)
                              ├── SQLite  (durable local store)
                              ├── Hardware (printer, drawer, scanner, pinpad)
                              └── Sync worker ──▶ Store server ──▶ Cloud
```

Offline capability collapses from a distributed-systems problem into a local API
call. The same React app, pointed at the cloud API instead of localhost, serves
back-office users with **zero install**. One frontend, two backends.

## Deployment notes

- **Local HTTPS certificate trust is a Phase 2 deliverable, not an afterthought.**
  Browsers reject self-signed certificates on localhost without provisioning; the
  installer adds a per-machine certificate to the trust store. Discovering this
  during a pilot is painful.
- **SQLite runs in WAL mode with a busy timeout.** Without WAL, the sync worker
  writing while the UI reads produces `SQLITE_BUSY`.
- **Agent updates are staged and health-gated.** You are shipping software to
  thousands of unattended machines; a bad update bricks tills during trading
  hours. Never deploy during business hours, always support automatic rollback.
