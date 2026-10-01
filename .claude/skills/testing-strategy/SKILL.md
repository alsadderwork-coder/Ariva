---
name: testing-strategy
description: What tests each change needs in Ariva (unit, integration with Testcontainers, API end-to-end, Playwright functional, security, golden replay) and how to run them. Load when writing or fixing tests.
---
# Testing in Ariva

Layers and where they live:
1. Unit (`Platform/Backplane/Ariva.UnitTests`): domain formulas and invariants (table-driven from docs/domain/formulas.md), services with Moq, architecture and security tests (layering, forbidden dependencies, endpoint inventory, entity binding, unsafe code, headers, limits).
2. Integration (`Ariva.IntegrationTests`): Testcontainers PostgreSQL with TimescaleDB, Kafka, Redis; script runner, NHibernate mappings, COPY writers, consumers, outbox, replay.
3. API end-to-end (`Platform/Testing/Ariva.E2E/tests/api`): every endpoint with 200, 400, 401, 403 per role (security/permission-matrix.json), cross-site 403, attack payloads (tests/support/payloads.ts), TOTP flows, idempotency.
4. Functional (`Platform/Testing/Ariva.E2E/tests/functional`): every screen per role, RTL, no console errors, no CSP violations, XSS probes on any input rendered back.
5. Golden replay: seed 9303 reproduces 18:05, 18:20 to 18:30 and 19:10 exactly; output hash stable.
6. Security gate: `node scripts/security/scan.mjs` and its self-test.

Run: `node scripts/verify.mjs backend | integration | web | e2e | all`. Names: `MethodName_Should_ExpectedResult_When_Condition`. Never weaken an assertion to pass; never mark a story passing with a skipped test unless the skip names the story that enables it.
