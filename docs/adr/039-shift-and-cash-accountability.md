# ADR 039 — Shift as the business-date anchor and the unit of cash accountability

**Status:** Accepted · **Date:** 2026-07-21 · **Phase:** 5

## Context

Two requirements land on the same object. Sales must be attributed to a trading day that may not match the calendar day, and cash in a drawer must be reconcilable to a named individual.

## Decision

`BusinessDate` is fixed once, at shift open, and every sale in the session inherits it. It is never derived per sale from the wall clock.

The shift is the unit of cash accountability. Opening float, mid-shift drops and pickups, and the closing count all attach to it. Cash movements are signed deltas, summed to give the expected drawer position — the same approach as the stock ledger in ADR 025, for the same reason.

Closing is **blind** by default: the cashier enters the counted amount without being shown the expected figure. Variance is computed, recorded, and never silently corrected.

## Consequences

A bar trading from 20:00 to 02:00 books the whole night to one trading day by construction, rather than splitting it across two daily reports. Getting this wrong corrupts every daily report and stays invisible until someone tries to balance a drawer.

Blind close preserves the control. Showing the expected figure first produces counts that agree with the system rather than with the drawer, which makes the entire count worthless.

Recording variance rather than correcting it means the books show reality. Variance by operator over time is one of the most reliable indicators of till fraud, and it only exists if the system refuses to tidy it away.

Signed cash movements mean the drawer position is order-independent and cannot disagree with itself.

The cost is that a shift must be open before trading can begin, which is an extra step at the start of a day and one more thing to recover if a terminal dies mid-shift. Shift recovery is outstanding work.
