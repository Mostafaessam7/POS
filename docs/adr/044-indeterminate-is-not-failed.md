# ADR 044 — An unknown payment outcome is its own state, not a failure

**Status:** Accepted · **Date:** 2026-07-22 · **Phase:** 6

## Context

When a payment request times out, three things may have happened: it never arrived, it arrived and was declined, or it arrived and succeeded and the response was lost. The three are indistinguishable from where we stand.

Most systems collapse this into `Failed`, because the code path that catches the exception is the error path. That single choice produces the double charge: `Failed` tells the cashier to retry, and if the first attempt had actually succeeded the customer pays twice. Recording it as success is worse — goods leave the shop unpaid.

## Decision

`PaymentStatus.Indeterminate` is a distinct, non-final state, and every transport-level failure maps to it: timeouts, socket errors, unrecognised exceptions, and **cancellation**. Cancellation is included deliberately; cancelling our wait does not cancel the acquirer's processing.

It is resolved by *asking*, never by inferring. `IPaymentProvider.QueryAsync` sits on the required interface rather than behind a capability flag, because a provider that cannot be asked what it did leaves us permanently unable to resolve these — that is a disqualifying property for an integration, not an optional feature.

`PaymentOutcomeStatus.NotFound` is treated as a clean negative: the provider has no record, so the request never arrived, so no money moved, so the payment may safely be marked `Failed`. It is the *only* inference permitted.

## Consequences

Some payments sit unresolved and need human attention. That is the honest state of affairs, and surfacing it is the point — the exceptions queue in the reconciliation report exists for exactly these.

A provider that cannot be queried — such as the manual card provider, where only a paper receipt roll knows the truth — returns `Unknown` forever and its payments must be closed by a person. That is a genuine limitation of that integration and an argument for integrated terminals, not a defect in the model.

A background sweep that resolves indeterminate payments is required and **is not yet built**.
