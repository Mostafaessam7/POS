# ADR 017 — Business date is assigned, never derived from the calendar

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 1

## Context

A store trading until 02:00 books those sales to the previous business day. Deriving the trading day from DateTime.Today silently corrupts every daily report, cash reconciliation, and Z-report, and the corruption is invisible until someone tries to balance a drawer weeks later.

## Decision

BusinessDate is a distinct type assigned at shift open from the branch's time zone and configured rollover hour, and carried on every transaction. There is deliberately no BusinessDate.Today. A source-scanning test fails the build on any direct system clock access outside IClock.

## Consequences

Reports and reconciliation are correct for late-trading sites. The cost is that the business date must be threaded through transaction creation, which the shift aggregate handles.
