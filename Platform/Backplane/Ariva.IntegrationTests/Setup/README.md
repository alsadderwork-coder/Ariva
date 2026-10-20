# Integration test setup

Integration tests run against real dependencies started by Testcontainers, so Docker must be available
(`node scripts/verify.mjs integration` locally; the `ci` workflow runs them on every pull request).

- `PostgresFixture` starts one PostgreSQL container per run on the TimescaleDB image used by
  `docker-compose.dev.yml` and creates the sample schema through the production persistence registration
  (`AddArivaPersistence`), mapping the entities in `Persistence/Samples` instead of Ariva.Core's.
- Tests share the container through the `postgres` collection and isolate their data with unique codes, so they
  can run in any order.
- Kafka and Redis fixtures arrive with the stories that need them (ARV-008, ARV-020).
- Every container image is a constant pinned by tag and digest (ARV-096): `PostgresFixture.Image` (the chart's TimescaleDB
  digest), `KafkaContainers.Image`, `RedisFixture.Image` (docker-compose.dev.yml's Redis, also used by the fault tests) and
  `FaultsFixture.ToxiproxyImage`. `node scripts/base-images.mjs --check` reads them by file and name, holds one digest per image
  and tag across the repository and refuses an image named by a literal; its `--resolve` proposes new digests.
  A machine that cannot pull ghcr.io's blobs (the cloud agent sessions) sets `ARIVA_TOXIPROXY_TAG_ONLY=true` to run the fault
  tests on a local build of the same Toxiproxy release, taken by its tag (`FaultsFixture.TagOnlyVariable`); a pipeline never sets it, and the fixture refuses it where
  `GITHUB_ACTIONS` or `TF_BUILD` is true.
