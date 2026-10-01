#!/bin/bash
# Local development only (ARV-003): a read-only role for the postgres-dev MCP server (restricted mode) and for
# people inspecting data. Runs once, when the data volume is first initialised. Production roles come from the
# versioned scripts in Ariva.Infra/Timescale/Scripts (ARV-006).
set -euo pipefail
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v readonly_password="$ARIVA_READONLY_PASSWORD" <<'SQL'
CREATE ROLE ariva_readonly LOGIN PASSWORD :'readonly_password' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT;
ALTER ROLE ariva_readonly SET default_transaction_read_only = on;
ALTER ROLE ariva_readonly SET statement_timeout = '30s';
GRANT CONNECT ON DATABASE ariva TO ariva_readonly;
GRANT USAGE ON SCHEMA public TO ariva_readonly;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO ariva_readonly;
ALTER DEFAULT PRIVILEGES FOR ROLE ariva IN SCHEMA public GRANT SELECT ON TABLES TO ariva_readonly;
SQL
