---
description: Add an Integration API endpoint for AODB or immigration systems (scope, site binding, idempotency, audit, tests)
argument-hint: <METHOD /api/v1/path> <scope> <purpose>
---
Add $ARGUMENTS to Ariva.Api.Integration following docs/architecture/integration.md and the integration-auth-totp skill:
[IntegrationScope], site binding, request model with limits and unknown-field rejection, idempotency key handling, audit entry, canonical command mapping, Result<T> per item for batches. Add the endpoint to wiki/08-Integration-Guide.md with a JSON example, and E2E tests: valid call, missing token 401, replayed TOTP rejected, wrong scope 403, other site 403, duplicate idempotency key returns the first result, oversized 413, malformed 400.
