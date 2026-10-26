-- 0064 The environment a database belongs to (ARV-180; CWE-269, CWE-287).
--
-- A vm-local host migrates the database it is given (the versioned scripts and NHibernate SchemaUpdate, with the migration
-- login), and Ariva.Api.Main there seeds the development accounts, whose passwords are known. A run of the E2E suites or a
-- developer's host whose Database settings reach a deployed store (a tunnel, a port forward) would do both there before any
-- other check. The migration job records its environment (Application:Environment) here after the scripts; a vm-local host
-- reads it before it migrates or seeds anything and refuses a database that names another environment.
--
--   database_environment   one row (id 1): the environment the last migration recorded, when, and with which login. The
--                          migration job writes it with the migration login; the runtime role reads it and can never write
--                          it. A database migrated before this script has no row until its next migration.
CREATE TABLE database_environment (
    id           smallint      PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    environment  varchar(32)   NOT NULL CHECK (environment ~ '^[a-z0-9][a-z0-9-]{0,31}$'),
    set_on       timestamptz   NOT NULL,
    set_by       varchar(100)  NOT NULL
);

REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON database_environment FROM ariva_runtime;
GRANT SELECT ON database_environment TO ariva_runtime;
