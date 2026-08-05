# ADR 022 — Materialised path for the category hierarchy

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 3

## Context

The dominant catalogue query is everything in a category and all its descendants. It runs on the catalogue screen, in category reports, and in promotion evaluation. With a naive ParentId this needs a recursive CTE on every read.

## Decision

Store a materialised path with leading and trailing slashes, plus ParentId and Depth. Subtree queries become an indexed prefix seek.

## Consequences

Reads are cheap and constant. Moving a subtree rewrites the path of every descendant, which is rare (hierarchies are restructured seasonally at most) and is a bounded single-transaction update. A closure table was considered and rejected as more join complexity than a hierarchy rarely deeper than four levels justifies.
