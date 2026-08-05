# ADR 006 — Tenant is a security boundary; company, branch and warehouse are authorization boundaries

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 1

## Context

The product is multi-tenant SaaS and also multi-company, multi-branch, multi-warehouse. Conflating these levels is the most common architectural error in multi-tenant business software and produces either a security hole or an unmanageable permission system.

## Decision

Four levels with two boundary types. Tenant is a security boundary: crossing it is never legitimate, it is enforced by infrastructure (query filter, write interceptor, generated test suite), and no permission can grant access across it. Company, Branch and Warehouse are authorization boundaries governed by scoped permissions. Isolation model is shared database, shared schema, with TenantId. TenantId is resolved only from the signed token claim.

## Consequences

Company must exist separately from Tenant because a franchise group or a multi-country retailer legitimately runs several legal entities under one account, each with its own tax registration and invoice sequence. Adding the level now costs one column; retrofitting it means backfilling a foreign key across every financial record already issued. Connection resolution is a swappable strategy so a database-per-tenant enterprise tier is a configuration change rather than a rewrite.
