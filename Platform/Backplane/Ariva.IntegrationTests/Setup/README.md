# Integration test setup

Testcontainers for PostgreSQL with TimescaleDB, Kafka and Redis will be added by the backlog (story ARV-007), together with the `Testcontainers.PostgreSql`, `Testcontainers.Kafka` and `Testcontainers.Redis` package versions in `Directory.Packages.props`. Shared fixtures (container lifetime, connection strings, script runner) go in this folder.

Until then the tests in this project are skipped.
