/*
  Versioned scripts: the only way the production schema changes (tables, indexes, roles, hypertables, continuous
  aggregates, compression and retention policies). ARV-006.

  - Name them NNNN_lower_snake_case.sql with the next free number. 0000 (this file) is never applied.
  - The migration job (Ariva.Api.Main --migrate) applies pending scripts in ascending order, each in its own
    transaction with its schema_version row (name, SHA-256, time, login). Start a script with the line
    "-- ariva:no-transaction" when PostgreSQL refuses a statement inside a transaction (CREATE INDEX CONCURRENTLY).
  - Never edit a script that has shipped. The runner and every host stop when a recorded checksum differs, and
    checksums.lock (checked by Ariva.UnitTests) makes an edit fail the build before it gets that far.
  - Use the snake_case names the NHibernate conventions produce (Platform/Backplane/CLAUDE.md, Persistence).
*/
