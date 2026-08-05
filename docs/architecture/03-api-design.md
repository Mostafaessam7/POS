# API Design

## Shape

Minimal APIs, grouped by module, versioned in the path.

```
/api/v{version}/{module}/{resource}
```

Path versioning over header versioning: it is visible in logs, testable from a
browser, and cacheable. Header versioning is more RESTful in theory and harder to
debug at 3am.

Endpoints live in the module that owns them, in a vertical slice beside their
handler and validator — not in a central `Controllers` folder. When a feature
changes, everything that changes is in one folder.

## No mediator

There is no MediatR. Version 13 moved to a commercial licence in mid-2025, and
the pattern's main benefit — pipeline behaviours — is available from ASP.NET Core
endpoint filters, which are built in and involve no reflection at dispatch.

Handlers are plain classes registered in DI and invoked directly. Cross-cutting
concerns (validation, authorization, logging, transactions) are endpoint filters.
See ADR 011. Wolverine (MIT, and it brings a transactional outbox) is worth
revisiting if message dispatch becomes genuinely necessary.

## Errors: RFC 9457 Problem Details

Every error response is a `ProblemDetails`, with a stable machine-readable code
alongside the human-readable message.

```json
{
  "type": "https://api.pos.example/errors/insufficient-stock",
  "title": "Insufficient stock",
  "status": 409,
  "detail": "Only 3 units of SKU-1234 remain at Branch 12.",
  "instance": "/api/v1/sales/018f3c2a-.../lines",
  "code": "inventory.stock.insufficient",
  "traceId": "00-4bf92f...-01"
}
```

The `code` is what clients branch on. Message text is for humans and will be
translated; a client that parses it will break.

`traceId` is the W3C trace context, so a support ticket maps to a distributed
trace without asking the customer to reproduce.

### Status codes

| Code | Use |
|---|---|
| 200 | Success with a body |
| 201 | Created, with `Location` |
| 204 | Success, no body |
| 400 | Validation failure |
| 401 | Not authenticated |
| 403 | Authenticated, lacks permission **at this scope** |
| **404** | **Not found — or exists in another tenant** |
| 409 | Business rule conflict (insufficient stock, duplicate SKU) |
| 422 | Well-formed but semantically impossible |
| 429 | Rate limited, with `Retry-After` |

**404 rather than 403 across a tenant boundary is deliberate.** A 403 confirms the
resource exists, which lets an attacker enumerate valid identifiers.

## Authentication

Bearer token on every request. The token carries `sub`, `tenant_id`, `terminal_id`,
`branch_ids`, `company_ids`, and `perm_version`.

The refresh token is an **httpOnly, Secure, SameSite=Strict cookie**, never
`localStorage` — one XSS against `localStorage` is total account compromise. The
access token lives in memory only, so a page refresh triggers a silent refresh.

Because the API endpoints are bearer-authenticated and the cookie is only used by
the refresh endpoint, anti-forgery tokens are required only on that endpoint.
Documenting this explicitly saves an argument in every security review.

## Authorization

```csharp
app.MapPost("/api/v1/sales/{id}/refund", RefundHandler.Handle)
   .RequirePermission(Permissions.Sales.RefundApprove, scopeRouteKey: "branchId")
   .AddEndpointFilter<ValidationFilter<RefundRequest>>();
```

`scopeRouteKey` names the route value identifying the scope. Omitting it means the
permission is satisfied at any scope — appropriate for list endpoints, which then
filter results to the accessible scope set.

## Idempotency

Every state-changing endpoint accepts `Idempotency-Key`. Mandatory on sales,
payments, and refunds.

This is not optional in retail. Networks fail mid-request, cashiers press the
button twice, and the terminal's own retry logic will re-send. Without idempotency
keys the answer to "did that payment go through?" is a manual reconciliation.

The key is stored with the response for 24 hours; a repeat returns the original
response rather than re-executing.

## Pagination

Cursor-based, not offset:

```
GET /api/v1/catalog/products?cursor=eyJpZCI6...&limit=50
```

Offset pagination drifts when rows are inserted during traversal, and `OFFSET
50000` is a table scan. A UUID v7 cursor is stable and indexed.

## Sync endpoints

The sync API is separate and versioned independently, because terminals in the
field run whatever build was installed when the store opened. A chain will
routinely have three protocol versions live, and a flag-day upgrade across every
till simultaneously is not something a retailer will agree to.

```
POST /api/sync/v1/batch          upload transactional records
POST /api/sync/v1/master         pull master-data delta
POST /api/sync/v1/master/ack     acknowledge applied versions
GET  /api/sync/v1/bundle         signed offline permission bundle
```

Terminals authenticate with a **client certificate**, not a user token. The device
is a principal in its own right.

## Documentation

OpenAPI generated from the endpoint definitions, published per version. Examples
are checked in and asserted against in integration tests, so documentation drift
fails the build rather than being discovered by a customer.
