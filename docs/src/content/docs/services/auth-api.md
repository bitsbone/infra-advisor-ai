---
title: Auth API
description: User registration, JWT authentication, self-service reset, and admin-managed passwords
---

**Port:** 8002 | **Framework:** FastAPI + SQLAlchemy + PostgreSQL | **Replicas:** 2

The Auth API handles user registration, authentication, self-service password reset, administrator-managed passwords, and admin-level user management. It issues JWT tokens that the browser includes on every subsequent API request.

## Endpoints

### Public endpoints

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/register` | Register a new user |
| `POST` | `/login` | Authenticate and receive a JWT |
| `POST` | `/forgot-password` | Request a password reset email |
| `POST` | `/reset-password` | Consume a reset token and set a new password |
| `GET` | `/health` | Service status |

### Authenticated endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/me` | Get current user profile |

### Admin-only endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/admin/users` | List all users |
| `POST` | `/admin/users` | Create a user (bypasses domain restriction) |
| `PUT` | `/admin/users/{user_id}/password` | Set a user's password and invalidate outstanding reset links |
| `DELETE` | `/admin/users/{user_id}` | Delete a user (cannot delete self) |
| `PATCH` | `/admin/users/{user_id}` | Toggle `is_admin` or `is_service_account` flag |

## Registration

By default, only `@datadoghq.com` email addresses can self-register (configurable via `ALLOWED_DOMAIN` env var). An initial administrator is provisioned explicitly with the `BOOTSTRAP_ADMIN_EMAIL` and `BOOTSTRAP_ADMIN_PASSWORD` environment variables; self-registration never grants administrator access.

```bash
curl -X POST https://infra-advisor-ai.kyletaylor.dev/auth/register \
  -H "Content-Type: application/json" \
  -d '{"email": "you@datadoghq.com", "password": "your-password"}'
```

Admin users can create accounts for any email domain via `POST /admin/users`.

Every route that creates or replaces a password applies the same server-side policy: 8 or more characters and no more than 72 UTF-8 bytes, matching bcrypt's safe input boundary.

## JWT authentication

Login returns a JWT token:

```bash
curl -X POST https://infra-advisor-ai.kyletaylor.dev/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email": "you@datadoghq.com", "password": "your-password"}'
```

```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6...",
  "user": {
    "id": "550e8400-...",
    "email": "you@datadoghq.com",
    "is_admin": true
  }
}
```

Include this token on every API request:
```
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6...
```

JWT tokens expire after 24 hours. There is no refresh endpoint — users log in again after expiry.

## Admin-managed passwords

An authenticated administrator can select **Password** beside any account in the web admin panel, enter the replacement twice, and submit it. The server requires a password of at least 8 characters and no more than bcrypt's 72-byte input limit, hashes it before storage, and clears any outstanding self-service reset token. The dialog clears both password fields after success, cancellation, or closure and does not persist them in browser storage.

This operation changes the credential used for the next login but does not revoke JWTs that were issued previously; those sessions remain valid until their normal 24-hour expiration. Use this control to provision or recover a known account when an administrator and the user have an approved secure channel for transferring the replacement credential. Use the email reset flow when the user can recover the account directly.

The response is only `{"updated": true}`. The password is never returned, placed in a URL, attached to a RUM event, or written to application logs. The Auth API emits a safe audit log containing the administrator and target user UUIDs, while automatic APM instrumentation records the route, status, duration, and database work without request-body capture. Operators can find successful events with `service:infra-advisor-auth-api "Admin set user password"` and investigate failures using the `PUT /admin/users/{user_id}/password` resource in APM.

## Password reset flow

The password reset flow uses email delivery via SMTP (Mailpit captures email in dev/demo):

```
1. POST /forgot-password {"email": "you@datadoghq.com"}
   → Always returns 200 (prevents email enumeration)
   → If email exists: generates cryptographically secure token (secrets.token_urlsafe(32))
   → Stores SHA-256 hash in DB with 1-hour expiry
   → Sends email via SMTP with reset link: {APP_BASE_URL}/?reset_token={token}

2. User clicks link → browser navigates to /?reset_token=...
   → UI detects ?reset_token parameter on load
   → Switches to "reset password" mode

3. POST /reset-password {"token": "...", "new_password": "..."}
   → Validates token hash exists and not expired
   → Enforces minimum 8-character password
   → Updates password hash, clears reset token
   → Returns new JWT (auto-login on reset)
```

SMTP configuration:

| Env var | Default | Description |
|---------|---------|-------------|
| `SMTP_HOST` | (none) | SMTP server hostname. If unset, delivery is skipped and a content-free warning is logged; reset tokens are never written to logs |
| `SMTP_PORT` | 587 | SMTP port |
| `SMTP_USER` | | SMTP username |
| `SMTP_PASSWORD` | | SMTP password |
| `SMTP_FROM` | | Sender address |
| `SMTP_TLS` | `true` | Set to `false` for Mailpit (no STARTTLS) |
| `APP_BASE_URL` | | Base URL for reset links (e.g., `https://infra-advisor-ai.kyletaylor.dev`) |

## Database schema

The `users` table in PostgreSQL:

| Column | Type | Notes |
|--------|------|-------|
| `id` | UUID | Primary key |
| `email` | TEXT | Unique, lowercased |
| `password_hash` | TEXT | bcrypt hash |
| `is_admin` | BOOLEAN | Default false |
| `is_service_account` | BOOLEAN | Default false (bypasses domain restriction) |
| `created_at` | TIMESTAMPTZ | Auto-set |
| `reset_token_hash` | TEXT | SHA-256 of current reset token, nullable |
| `reset_token_expires` | TIMESTAMPTZ | Token expiry (1 hour from creation), nullable |

The schema is created on startup via `init_db()` which uses `CREATE TABLE IF NOT EXISTS` and `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` — safe for both fresh installs and upgrades.

## Observability

**APM:** All HTTP requests traced via `ddtrace.auto`. SQL queries appear as child spans of each HTTP span.

**Reset-delivery privacy:** Password-reset links, tokens, recipient email addresses, SMTP responses, and exception messages are excluded from application logs. Successful delivery records only a fixed completion event; missing SMTP configuration records a fixed warning; failed delivery records only the exception type. The public `/forgot-password` response remains identical for existing and unknown accounts.

**DBM (Database Monitoring):** `DD_DBM_PROPAGATION_MODE=full` is set in the auth-api configmap. This injects full trace context into SQL comments, allowing Datadog DBM to correlate slow query samples and `EXPLAIN` plans back to the originating APM trace.

The Datadog monitoring role has read-only access to `pg_stat_statements` for query analytics.

**Log annotation:**
```yaml
ad.datadoghq.com/auth-api.logs: '[{"source": "auth-api", "service": "auth-api"}]'
```

## Mailpit (dev/demo SMTP capture)

In the dev/demo environment, [Mailpit](https://mailpit.axllent.org/) intercepts all outbound email. No real email is delivered.

- **SMTP:** `mailpit.infra-advisor.svc.cluster.local:1025` (no TLS, ClusterIP)
- **Web UI:** `https://infra-advisor-ai.kyletaylor.dev/mailpit` (bcrypt basic auth via `MP_UI_AUTH`)
- **Storage:** In-memory (email visible only until pod restart)
- **Webroot:** `MP_WEBROOT=/mailpit` so links/assets stay under the nginx-proxied sub-path

Mailpit is configured via `k8s/auth-api/configmap.yaml` (where auth-api points its SMTP client) and `k8s/mailpit/configmap.yaml` + `mailpit-secret` (Mailpit itself):

```yaml
# k8s/auth-api/configmap.yaml
SMTP_HOST: mailpit.infra-advisor.svc.cluster.local
SMTP_PORT: "1025"
SMTP_TLS: "false"
SMTP_FROM: infra-advisor-ai@demo.local
```

The basic-auth credentials are generated from `MAILPIT_UI_USERNAME` + `MAILPIT_UI_PASSWORD` env vars by `make create-mailpit-secret`, which `htpasswd -nbB`-hashes the password into the `MP_UI_AUTH` env value (Mailpit accepts the `$2a` / `$2b` / `$2y` bcrypt prefix variants).

**Defense in depth:** the password-reset inbox is sensitive (anyone who reads it can take over an account via `forgot-password` → token → `reset-password`). Layer a Cloudflare Access policy in front of `/mailpit/*` so basic auth isn't the only gate.
