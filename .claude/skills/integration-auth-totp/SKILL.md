---
name: integration-auth-totp
description: How AODB and immigration systems authenticate to Ariva (client id, secret and TOTP, AMAN-compatible) and how Ariva authenticates outbound (TotpClientCredentials and others), with the hardening AMAN lacks. Load for any Integration API or connector work.
---
# Integration authentication

Normative spec: docs/architecture/integration.md. Reference implementation to port (read-only): ../Aman/Platform/Backplane/Aman.Api.Integration/Controllers/V1/AuthController.cs, ../Aman/Platform/Backplane/Aman.Infra/Services/Authentication/SvcIntegrationAuth.cs, SvcIntegrationAuthCache.cs, Aman.Api.Common/Attributes/IntegrationPermissionAttribute.cs.

## Keep from AMAN
Request shape `{ clientId, clientSecret, totpCode }` on `POST /api/v1/auth`; response `{ accessToken, expiresAt, sessionId }`; JWT with session id; cached per-session permissions; per-request `X-TOTP-Code` header when the client policy requires it; Otp.NET (`Totp`, `VerificationWindow`).

## Fix (do not copy AMAN's weaknesses)
1. Secrets: store PBKDF2-SHA256 hashes (`Rfc2898DeriveBytes.Pbkdf2`, 600,000 iterations, 16-byte salt); compare with `CryptographicOperations.FixedTimeEquals`.
2. TOTP replay: `VerifyTotp(code, out long step, window)` then `TryConsumeTimeStep(clientId, step)` (Redis `SET NX` with expiry, database fallback); reject any step not newer than the last accepted one.
3. One generic `invalid_client` response for every failure; no oracle.
4. Rate limits per client and per IP (pre-auth limiter on IP, post-auth on client id; IP partitions can be spoofed if forwarded headers are misconfigured, so trust only configured proxies); lockout after 10 failures for 15 minutes with an alert.
5. Times from `TimeProvider.GetUtcNow()`.
6. Separate signing keys and audiences: `ariva-users`, `ariva-integration`, `ariva-devices`; authorization policies pin the scheme so an integration token can never call a user endpoint.
7. TOTP seeds encrypted at rest with Data Protection (keys persisted in PostgreSQL through a custom IXmlRepository and protected with a certificate from a Kubernetes secret).
8. Source CIDR allowlist per client; site binding and scopes on every endpoint; idempotency keys; audit with payload hash.

## Outbound
`OutboundEndpoint` registry (admin with step-up MFA). Handlers: TotpClientCredentials (the AMAN connector: generate the code with Otp.NET, cache the token until 60 seconds before expiry, add `X-TOTP-Code` when required), OAuth2ClientCredentials, ApiKeyHeader, HmacSignature, MutualTls. Named `IHttpClientFactory` clients, resolved-IP CIDR check, no redirects, timeouts and circuit breaker (Microsoft.Extensions.Http.Resilience).

## Tests
Token exchange, wrong secret, wrong code, replayed code within the window, lockout, wrong scope, other site, expired token, integration token on a user endpoint (must be 401 or 403), outbound handler against the simulator's mock AMAN.
