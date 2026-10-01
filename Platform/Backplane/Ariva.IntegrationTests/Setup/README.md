# Integration test setup

Integration tests run against real dependencies started by Testcontainers, so Docker must be available
(`node scripts/verify.mjs integration` locally; the `ci` workflow runs them on every pull request).

- `PostgresFixture` starts one PostgreSQL container per run on the TimescaleDB image used by
  `docker-compose.dev.yml` and creates the sample schema through the production persistence registration
  (`AddArivaPersistence`), mapping the entities in `Persistence/Samples` instead of Ariva.Core's.
- Tests share the container through the `postgres` collection and isolate their data with unique codes, so they
  can run in any order.
- Kafka and Redis fixtures arrive with the stories that need them (ARV-008, ARV-020).
