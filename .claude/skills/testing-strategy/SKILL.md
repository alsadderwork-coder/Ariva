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
7. Mutation (Stryker.NET, `Ariva.UnitTests/stryker-config.json`, wiki 16): the engines' unit tests must kill real faults; break below 70 percent. In the engines (Ariva.Core: Queueing, Desks, Availability, Border, Flights, Validation and the domain entities), never declare an `out` variable or a pattern variable (`x is { } v`, `x is not { } v`, `TryGetValue(k, out var v)`) inside a condition (`if`, `while`, `&&` or `||` operand, ternary): declare it in its own statement first (`Desk desk = null;` then `TryGetValue(k, out desk)`, or `var v = x; if (v is null)`, or `HasValue` and `Value`). A mutant that short-circuits such a condition leaves the variable unassigned (CS0165), Stryker cannot tell which mutant broke the build and drops every mutant of the method (safe mode), so the method is not measured at all. Likewise keep each interpolated string passed to `string.Create` whole (no `$"a" + $"b"`: CS1620) and give `HashCode.Combine` typed locals rather than a nested call (CS0411). The checkpoint's mutation step fails when Stryker reports safe mode or prints no score (ARV-069a, `scripts/mutation-run.mjs`).

Run: `node scripts/verify.mjs story --specs <files> --integration "<filter>"` per story, `checkpoint` every 5 stories (`docs/harness/test-cadence.md`); the single scopes `backend | integration | web | e2e | all` still exist. Names: `MethodName_Should_ExpectedResult_When_Condition`. Never weaken an assertion to pass; never mark a story passing with a skipped test unless the skip names the story that enables it.
