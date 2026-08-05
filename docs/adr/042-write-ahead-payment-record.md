# ADR 042 — The payment record is committed before the provider is called

**Status:** Accepted · **Date:** 2026-07-22 · **Phase:** 6

## Context

The natural way to write a payment call is to ask the provider first and record the answer: it avoids a useless row when the card is declined, and the code reads in the order the story happens.

It also creates a state from which there is no recovery. If the process dies between the provider approving and the response being written — a crash, a pod eviction, a power cut at the till — money has moved and no local evidence exists that it ever did. No query we can run finds it, because there is nothing to find. It surfaces days later as an unexplained line in a settlement file, with a customer who has been charged and has no receipt.

## Decision

The `Payment` row is created and **committed** before any request leaves the building. `IPaymentStore.AddAndCommitAsync` is named for the durability guarantee rather than the operation, because calling it `AddAsync` invites someone to batch the commit with the response and quietly undo the whole design.

The ordering is asserted by a test that inspects a recorded timeline (`The_payment_record_is_committed_before_the_provider_is_called`), not by inspecting the end state — an end-state assertion passes against a completely unsafe implementation.

## Consequences

The worst case degrades from *money moved with no record* to *a record exists whose outcome is unknown*. The second is recoverable: it can be found by querying for stuck payments and resolved by asking the provider (ADR 044).

The price is orphan rows for declines and abandoned attempts, plus one extra round trip to the database on every payment. Both are trivial against the alternative.
