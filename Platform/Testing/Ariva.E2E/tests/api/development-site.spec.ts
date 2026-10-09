import crypto from 'node:crypto';
import { expect, test } from '@playwright/test';
import pg from 'pg';
import { databaseAvailable } from '../support/accounts';
import { developmentOnlySiteCode, developmentSiteProblem, hostDatabaseSettings, type Query } from '../support/development-site';

// ARV-139c (CWE-200): the global setup's guard against a database that holds the development-only site. It refuses such a
// database, fails closed when the database it reached has no site table (global setup runs after the hosts migrated
// theirs, so that is another database) or when the hosts' database settings are not in its environment, and lets this
// run's own database through (this run got here, so the guard passed it).

/** A database that answers the guard's two queries as given. */
function fake(tablePresent: boolean, sites: number): Query {
	return async (sql) => (sql.includes('to_regclass') ? [{ present: tablePresent }] : [{ n: sites }]);
}

test.describe('development-only site guard', () => {
	test('refuses a database that holds the site', async () => {
		expect(await developmentSiteProblem(fake(true, 1))).toContain(`holds the development-only site ${developmentOnlySiteCode}`);
		expect(await developmentSiteProblem(fake(true, 1))).toContain('node scripts/dev-down.mjs --volumes');
	});

	test('fails closed when the database it reached has no site table', async () => {
		expect(await developmentSiteProblem(fake(false, 0))).toContain("it is not the hosts' database");
	});

	test('passes a database without the site', async () => {
		expect(await developmentSiteProblem(fake(true, 0))).toBeNull();
	});

	test("fails closed without the hosts' database settings, and uses the migration login when there is one", () => {
		expect(() => hostDatabaseSettings({ Database__Name: 'postgres', Database__Migration__Username: 'postgres' })).toThrow(/Database__Port/);
		expect(() => hostDatabaseSettings({ Database__Port: '5433', Database__Migration__Username: 'postgres' })).toThrow(/Database__Name/);
		expect(() => hostDatabaseSettings({ Database__Port: '5433', Database__Name: 'postgres' })).toThrow(/Database__Migration__Username/);
		expect(
			hostDatabaseSettings({
				Database__Port: '5433',
				Database__Name: 'postgres',
				Database__Username: 'ariva_app',
				Database__Password: 'runtime',
				Database__Migration__Username: 'owner',
				Database__Migration__Password: 'migration'
			})
		).toEqual({ host: 'localhost', port: 5433, database: 'postgres', user: 'owner', password: 'migration' });
		expect(hostDatabaseSettings({ Database__Host: 'db', Database__Port: '5432', Database__Name: 'ariva', Database__Username: 'ariva_app', Database__Password: 'runtime' })).toEqual({
			host: 'db',
			port: 5432,
			database: 'ariva',
			user: 'ariva_app',
			password: 'runtime'
		});
	});

	test("refuses the hosts' database once it holds the site, and passes it otherwise", async () => {
		test.skip(!databaseAvailable, 'needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
		const db = new pg.Client(hostDatabaseSettings());
		await db.connect();
		try {
			const query: Query = async (sql, params) => (await db.query(sql, params)).rows;
			expect(await developmentSiteProblem(query)).toBeNull();

			// Inside a transaction that is rolled back: no other session of this run ever sees the row.
			await db.query('BEGIN');
			try {
				await db.query('INSERT INTO site (id, code, name) VALUES ($1, $2, $3)', [crypto.randomUUID(), developmentOnlySiteCode, 'E2E guard probe']);
				expect(await developmentSiteProblem(query)).toContain(`holds the development-only site ${developmentOnlySiteCode}`);
			} finally {
				await db.query('ROLLBACK');
			}

			expect(await developmentSiteProblem(query)).toBeNull();
		} finally {
			await db.end();
		}
	});
});
