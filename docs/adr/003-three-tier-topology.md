# ADR 003 — Three-tier terminal, store, cloud topology

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 0

## Context

The requirement is a multi-store chain with central HQ where terminals must sell fully offline. A two-tier terminal-to-cloud design makes every till independently responsible for reaching the internet.

## Decision

Terminal (SQLite, authoritative for its own transactions) to optional Store Server (cache and relay) to Cloud (SQL Server, system of record). The store server tier is optional and configurable per site.

## Consequences

A single-till convenience store runs terminal-to-cloud with no server. A department store with forty tills gets local resilience and one WAN connection instead of forty. The cost is a third deployment target and a third place for data to be stale; mitigated by making the store server a pure relay with no authoritative state of its own.
