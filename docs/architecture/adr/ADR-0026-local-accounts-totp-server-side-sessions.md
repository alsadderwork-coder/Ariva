# ADR-0026: Local username and password accounts with TOTP, server-side sessions

- Status: Accepted
- Date: 2026-10-01
- Source: Product owner decision, 2026-10-01 (grilling of ARV-010: "username and password; take the best practice for all the others")

## Context

ARV-010 assumed local users without saying so, treated access tokens as the session (so revocation lagged up to 15 minutes), left TOTP recovery, signing keys, the refresh cookie topology and the step-up contract open, and was too large for one agent session. AMAN offers email and password, NFC card with a one-time code, and an optional external identity provider (IShield); it keeps sessions in Redis, returns refresh tokens in the response body, signs with one shared symmetric key and trims passwords before checking them.

## Decision

1. **Identity source:** local Ariva accounts with a username and password; email is optional. Border supervisors get Ariva accounts separate from AMAN. Authentication sits behind an `ISvcAuthenticator` boundary so OIDC federation (an airport's Entra ID, an authority's identity provider, IShield) can be added in Phase 1 without changing sessions or authorization.
2. **Passwords:** PBKDF2-SHA256 with 600,000 iterations; 12 to 128 characters, any Unicode, no composition rules, no periodic expiry, a bundled blocklist (sites may be offline) plus context words; never trimmed. Admin-created users receive a temporary password and a `pending` scope until they change it and enrol TOTP. Password reset is admin-only in Phase 0 (on-prem SMTP is not guaranteed).
3. **Second factor:** TOTP (RFC 6238, SHA1, 6 digits, 30 seconds, one step either side) for every human user, all four roles. Secrets encrypted with Data Protection; replay guard on the user row; ten single-use recovery codes; one break-glass account per deployment created only by the installer job, alerting on every use.
4. **Brute force:** per-IP limit of 10 login attempts a minute and a per-account lock after 10 consecutive failures for 15 minutes (passwords and second factor together), equal timing for unknown users, admin unlock.
5. **Tokens:** ES256 JWT access tokens, 15 minutes, issued only by Ariva.Api.Main with a `kid`; other hosts hold the public key; keys rotate every 90 days with overlap. Separate audiences for users, integration clients and devices (ADR on integration auth unchanged).
6. **Sessions:** server-side `UserSession` rows in PostgreSQL, cached through FusionCache with backplane invalidation; every request checks the `sid`, so logout, disabling a user or a role change applies within 5 seconds. Absolute lifetime 12 hours; idle 30 minutes for SystemAdministrator and 4 hours for operational roles. Wall screens and passenger displays use device credentials, never user sessions.
7. **Refresh:** opaque 256-bit tokens stored hashed, rotated on every use within a family; a reused token within 30 seconds returns the already-issued successor once (two tabs), after that the family is revoked. The token lives only in the `__Secure-ariva_rt` cookie (HttpOnly, Secure, SameSite=Strict, Path=/api/auth); the refresh endpoint also requires a custom header and an allowlisted Origin. The web app keeps access tokens in memory and lets one tab refresh for all (Web Locks API, BroadcastChannel).
8. **Topology:** the web app and the APIs share one host; `/api` and `/hubs` route through the ingress, so the cookie needs no cross-site rules.
9. **Step-up:** `[RequiresRecentMfa(15)]` on a fixed list of critical actions, answered with RFC 9470 `insufficient_user_authentication` and a `mfa_required` problem code; `POST /api/auth/step-up` re-verifies TOTP and refreshes `auth_time`.

## Consequences

- One extra cache read per request (memory hit in almost all cases) buys immediate revocation.
- Users manage two credentials (AMAN and Ariva) until federation lands; the boundary keeps that change small.
- The installer job owns break-glass creation, so the deployment guide gains a sealed-credential procedure.
- The ingress serves web and APIs on one host; ARV-062 updates the chart.
- ARV-010 is split into ARV-010a (passwords, lockout, tokens), ARV-010b (sessions, refresh, revocation), ARV-010c (TOTP, recovery, break-glass) and ARV-010d (step-up).

## Alternatives considered

- OIDC federation in Phase 0: no customer identity provider is available for the demo; deferred to Phase 1.
- Stateless access tokens only: revocation lag of up to 15 minutes is not acceptable for a border system.
- Refresh token in the response body or web storage (AMAN's approach): exposed to script injection; rejected (CWE-384, CWE-79).
- Argon2id: preferred by OWASP but not in the .NET base library; PBKDF2 at 600,000 iterations is FIPS-friendly and matches AMAN's algorithm family. Revisit if a vetted Argon2 package is approved.
