# Backplane (.NET) rules

Load the `aman-conventions` skill for the full pattern set; this file is the short checklist.

## Layering (enforced by `Ariva.UnitTests/Architecture` and `Security` tests)

- `Ariva.Utilities` references nothing in Ariva. `Ariva.Core` references only Utilities. `Ariva.Infra` references Core. `Ariva.Di` references Core and Infra. Hosts reference `Ariva.Api.Common` (and through it Di). `Ariva.Business.Contracts` references nothing.
- Domain entities, value objects, enums, domain events, criteria, input models and view models live in `Ariva.Core/Domain/*`. Service interfaces (`ISvc*`) live in `Ariva.Core/Services`. Implementations (`Svc*`, `internal`) live in `Ariva.Infra/Services`.
- No Ariva assembly may reference an `Aman.*` assembly. Port code by copying the pattern, renaming the namespace and removing what Ariva does not need.

## Entities

- Inherit the ported `EntityBase` or `BaseAuditableEntity`; rich methods (`zone.MoveVertex(...)`, `profile.Publish(...)`), protected setters, invariants enforced in the entity, domain events raised through `IHasDomainEvents`.
- Ids are `Guid` (sequential); timestamps are UTC `DateTime` from `TimeProvider`.
- Credentials are never plain properties: hashes (`ClientSecretHash`, `PasswordHash`, `RefreshTokenHash`) or encrypted values (`TotpSecretEncrypted`).

## Services and controllers

- Services return `Result<T>`; validate with `Fx.Specification` before touching entities; no try/catch except at true boundaries (a global handler exists).
- Controllers: `Controllers/AdminArea/<Entity>/Controller.cs`, class `Controller`, route `AdminArea/<Entity>`, one service call per action, `[Permission(nameof(Permissions.X))]` on every action or the class. Operations screens use `Controllers/OpsArea/...`. Integration endpoints use `[IntegrationScope("...")]`.
- Bind `Create*Request` and `Update*Request` models, never entities. Every string in a request model has a maximum length.
- Every site-bound query or command goes through `ISiteScope` (CWE-863).
- Search endpoints: `Search<Criteria*>` with paging; sort and filter fields mapped through allowlists (CWE-89).

## Persistence

- NHibernate through `IStorageProvider` and `IUnitOfWork` (ported from AMAN). `Query<T>()`, `QueryAsNoTracking<T>()`, `ToFuture()` for count plus page.
- Raw SQL only through `ExecuteSqlAsync(sql, parameters)`. Never interpolate.
- Time series: Timescale hypertables written with Npgsql binary COPY (`Ariva.Infra/Timescale`). DDL only in `Timescale/Scripts/NNNN_*.sql`, applied by the script runner; scripts are immutable once merged.
- `SchemaUpdate` runs only when `Database:AllowSchemaUpdate` is true, which only `vm-local` sets.

## Messaging and streaming

- Publish through `ISvcMessageBus` (Confluent.Kafka implementation, outbox for domain events). Topic names come from `KafkaTopics` constants (`ariva.<context>.<event>.v1`); never build topic names from input.
- Consumers: `EnableAutoOffsetStore = false`, `StoreOffset` only after the effect is persisted (at-least-once), idempotent handlers keyed by `SourceEventId` or event id. Partition sensing events by zone id.
- The queue state engine in `Ariva.Core` is pure (no I/O): stream workers feed it events and persist its outputs. Load the `kafka-streaming` skill.

## Security baseline (already wired in Ariva.Api.Common)

Default deny fallback policy, security headers, request and JSON limits, rate limiting (429 with Retry-After), ProblemDetails without stack traces, trusted forwarded headers only, CORS allowlist. Do not bypass these in a host. Never set `ASPNETCORE_FORWARDEDHEADERS_ENABLED`.

## Tests

- `Ariva.UnitTests`: `MethodName_Should_ExpectedResult_When_Condition`, xUnit v3, FluentAssertions, Bogus, Moq. Domain logic and formulas get table-driven tests using the cases in `docs/domain/formulas.md`.
- `Ariva.IntegrationTests`: Testcontainers (PostgreSQL with TimescaleDB, Kafka, Redis) for persistence, scripts, consumers and the outbox.
- Every new endpoint is added to the E2E suite (`Platform/Testing/Ariva.E2E/tests/api`) with its authorization cases and attack payloads.
