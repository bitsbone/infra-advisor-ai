---
title: Auth API
description: Understand identity, credential lifecycle, administrative boundaries, and privacy-safe authentication telemetry
docType: reference
audience:
  - application-developer
  - security-engineer
maturity: stable
verifiedOn: 2026-08-27
sidebar:
  order: 6
---

The Auth API owns users and bearer tokens. Agent and client services consume validated identity; they do not implement their own password rules or infer administrative access.

## Endpoint groups

| Access | Endpoints | Purpose |
|---|---|---|
| Public | `/register`, `/login`, `/forgot-password`, `/reset-password` | Account and credential lifecycle |
| Authenticated | `/me` | Current identity |
| Administrator | list/create/update/delete users; set password | Controlled account management |
| Runtime | `/health`, `/livez`, `/readyz` | Diagnostics and shallow probes |

Use the running OpenAPI schema for exact bodies. JWT `sub` is the stable user identity consumed by other services.

## Registration and bootstrap

Normal registration enforces the configured email domain and never grants administrator access. The first-user-becomes-admin behavior was removed because a database reset could reopen an elevation race. Initial administration comes from explicit bootstrap environment variables or an existing administrator.

Service accounts can bypass the human email-domain rule only through the administrator path.

## Password and token boundaries

Passwords are validated centrally and stored only as bcrypt hashes. JWTs are returned to the client but never persisted or logged by the API. Password-reset tokens are random, stored as hashes, expire, and are cleared after use.

`/forgot-password` returns the same successful response whether an account exists, preventing email enumeration. SMTP failures log only an error type because exception text can include recipient or server details.

An administrator can set a user's password. The handler clears outstanding reset tokens and logs only actor and target UUIDs. It returns no credential material. Existing JWTs are not revoked by a password change; that is an explicit current limitation, not an implied security property.

Administrators cannot remove or modify their own account/role through the guarded endpoints, reducing accidental lockout.

## Persistence

The service requires PostgreSQL and creates/migrates its small schema at startup. SQLite is not a supported local substitute because UUID, PostgreSQL SQL, and concurrency behavior are part of the implementation.

## Observability

FastAPI and PostgreSQL operations are traced through `ddtrace`, with log/trace and DBM correlation where configured. Logs may retain stable user IDs for administrative audit events but must not contain email addresses, passwords, reset tokens, JWTs, SMTP credentials, or exception text that can echo them.

App and API Protection is enabled on the public Agent APIs, not this Auth API in the current architecture. Do not infer security coverage for one service from another service's ConfigMap.

## Verify a change

Test success and indistinguishable failure behavior, password byte/length bounds, hashing, token expiry and one-time use, domain enforcement, admin authorization, self-protection rules, and reset-token invalidation. Use a disposable PostgreSQL database for SQL changes.

The UI's admin surface must clear password fields after success or closure and never persist them in local storage or telemetry.
