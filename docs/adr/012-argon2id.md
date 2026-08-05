# ADR 012 — Argon2id for password hashing

**Status:** Accepted · **Date:** 2026-07-20 · **Phase:** 1

## Context

ASP.NET Core Identity defaults to PBKDF2. It is not broken, but it is cheap to accelerate on GPUs, and current OWASP guidance favours memory-hard algorithms.

## Decision

Argon2id via Konscious.Security.Cryptography, behind IPasswordHasher. Starting parameters 19 MiB memory, 2 iterations, parallelism 1. Algorithm and parameters are stored alongside each hash.

## Consequences

Argon2id consumes server memory per hash by design; parameters tuned too aggressively turn the login endpoint into a self-inflicted denial of service. Parameters must be load-tested and authentication endpoints rate-limited independently. Storing the parameters enables transparent hash upgrade on successful login, avoiding a forced password reset when the cost factor is raised.
