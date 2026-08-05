# ADR 050 — Approval thresholds are configuration; separation of duties is an invariant

**Status:** Accepted · **Date:** 2026-07-22 · **Phase:** 7

## Context

Purchase order approval is where a retailer's money starts leaving. Every chain wants its own rules: this one approves anything under 500 automatically, that one wants a director on anything over 10,000, a third wants two signatures on capital items.

The temptation is to make the whole thing configurable, because all of it looks like policy. It is not. Some of it is policy and some of it is control, and the difference is whether a tenant should be able to switch it off.

## Decision

Thresholds are data. `ApprovalPolicy` carries the value above which approval is required and an ordered list of thresholds mapping order value to required `ApprovalLevel`. A tenant edits these freely, and a policy requiring no approval at all is legitimate.

Separation of duties is an invariant, enforced inside `PurchaseOrder.Approve`:

- The person who raised an order **cannot** approve it. This is checked in the aggregate, not in a handler, not in a filter, and not in the UI, because those are the three places a control gets bypassed by a new call site.
- An approver below the required level is refused. Levels are ordered, so a director satisfies a requirement for a supervisor; the reverse is not true.
- The same person cannot approve twice, so a two-signature policy cannot be satisfied by one person clicking twice.

The one escape hatch is `ApprovalPolicy.AllowSelfApproval`, and it exists for a real case: an owner-operated single shop where the person raising the order *is* the only person. It is off by default, it is explicit, and turning it on is a recorded configuration decision rather than an absence of code.

Rejection returns the order to an editable state rather than a terminal one. Rejection almost always means "fix the quantity and resubmit", and forcing a fresh order loses the discussion. A reason is mandatory: a rejection without one is an instruction to guess.

## Consequences

`ApprovalPolicy` is passed into `Submit` and `Approve` rather than held on the order. The aggregate stays free of configuration lookup, and a policy change does not retroactively alter orders mid-flight — but it does mean every call site must supply the policy, and supplying the wrong one is a mistake the domain cannot catch.

Approvals are recorded on the order as a list, with approver, level, timestamp and outcome, including rejections. The order carries its own approval history, which is what an auditor asks for.

Delegated authority, absence cover, and value bands per category are not modelled. These are real and common, and they are deferred rather than designed around.
