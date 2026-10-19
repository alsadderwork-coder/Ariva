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

## Implementation notes (ARV-093): cookie name prefixes

ASVS 5.0 V3.3.3 asks for the `__Host-` prefix on every cookie that is not meant for other hosts. `__Host-` requires `Secure`, no `Domain` and `Path=/`. The two server cookies keep `__Secure-` instead, because each is scoped to the one path that reads it:

- `__Secure-ariva_rt` (the refresh token, decision 7): HttpOnly, Secure, SameSite=Strict, no Domain (host-only), `Path=/api/auth` (`Ariva.Api.Common/Security/RefreshCookie.cs`, `Ariva.Infra/Security/RefreshTokens.cs`). The web app, its `/api` and its `/hubs` share one host (decision 8), so with `Path=/` the browser would send the refresh token with every page, script and style the web pod's nginx serves and with every API call and hub connection to Ariva.Api.Main, none of which reads it. With `/api/auth` it goes only to Ariva.Api.Main's nine authentication routes (login, refresh, logout, me, change-password, totp/enroll, totp/confirm, totp/recovery-codes and step-up), of which login, refresh and logout read it (`Controllers/Auth/Controller.cs`).
- `__Secure-ariva_jobs_hub` (the Cronz dashboard's hub ticket, ARV-060): HttpOnly, Secure, SameSite=Strict, no Domain, `Path={dashboard base}/ticker-notification-hub` (`Ariva.Api.Cronz/Jobs/JobsDashboard.cs`). It is an expiry and its HMAC under a key the process draws at random, honoured only on the hub path; the path keeps it off the dashboard's page and API requests.

What `__Secure-` leaves open that `__Host-` closes (cookie tossing): any host under the web host's registrable domain (the name one label below the public suffix, for example `example.ae` for `queue.ops.example.ae`), at any level and the apex included, can set a cookie of the same name with `Domain=` that domain or any domain between it and the web host. It can also give the cookie a longer path (`/api/auth/refresh`), which the browser sends first, so the attacker decides the order whichever of two same-named cookies the server reads. The server cannot tell the two apart. For the hub ticket that gains nothing: a ticket without the process key is refused, so at worst that browser's dashboard loses its live updates while the planted cookie lives. For the refresh cookie such a host can make every refresh of that browser fail while the planted cookie lives (the user signs in again and again), or, with an Ariva account of the attacker's own, sign the browser into the attacker's session, so that the user works in the attacker's account. Neither reads the user's own token: the cookie is HttpOnly, and a cookie set for a parent domain cannot read a host-only one. The user's own sign-out still works: logout revokes the session of the access token as well as the one the cookie names.

A compensating control, not a close: the go-live record (the security guide's checklist, wiki 13 section 7, item 22) names the registrable domain of every Ariva host name and records that every host under it, at every level, is run by the operator and serves no third-party or user content, that no DNS record under it is dangling (a record left pointing at a service no longer run, which someone else can take over) and no wildcard record points at a third-party service; or that Ariva has a registrable domain of its own. Ariva's other host names (the API hosts' own, such as `api-main-dev-ariva.dalilhub.tech` beside `dev-ariva.dalilhub.tech`, the Cronz host with TickerQ's bundled dashboard, and the simulator's outside production) are such siblings of the web host. What remains is accepted with the record: a host under that domain that is compromised can still toss the cookie. The way to close it, `__Host-ariva_rt` with `Path=/`, would send the token with every request on the web host. It changes the sign-in, refresh and logout code and every client that names the cookie, so it waits for the owner (the login freeze, in force since 2026-10-14). The hub ticket could move to `__Host-` with `Path=/` at little cost at the same time.

The web app's own cookie, `__Host-sidebar_state` (ARV-084), has no reason to be scoped and uses `__Host-`. The ingress affinity cookies (`ariva-<ingress>`, `templates/_helpers.tpl`) carry no prefix. They are Secure, SameSite=Strict, `Path=/` and set without a Domain, so `__Host-ariva-<ingress>` would fit them. That waits until ingress-nginx's annotation validation for the name is checked against a running controller (ARV-137).
