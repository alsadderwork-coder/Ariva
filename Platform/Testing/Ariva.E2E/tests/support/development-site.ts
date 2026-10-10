import pg from 'pg';

/**
 * ARV-139c (CWE-200): the development-only site NBJ-BC1 must never appear in an E2E run, its screenshots or its visual
 * baselines. The suite pins the seed setting off for api-main, but that only stops this run from seeding it: it cannot
 * remove a site that an earlier run on this machine seeded into the same database (Ariva.Api.Main run by hand with the
 * setting, which uses the dev-up database, or a database container left on its separate volume by run-ariva.ps1's
 * switch). Global setup therefore checks the hosts' database before any test, and fails closed.
 */
export const developmentOnlySiteCode = 'NBJ-BC1';

/** One query against the database: its rows. */
export type Query = (sql: string, params?: unknown[]) => Promise<Record<string, unknown>[]>;

const reset =
	'Stop any AppHost run started with run-ariva.ps1\'s switch for it (close its window, then docker stop its timescaledb container), ' +
	'reset the local services and their data (node scripts/dev-down.mjs --volumes, then node scripts/dev-up.mjs) and run the suite again.';

/**
 * The database the hosts of this run use: the Database__* variables Playwright passes them (playwright.config.ts starts
 * every host with this process's environment). Without the port and the name the hosts would fall back to their own
 * settings files, which this check cannot follow, so it stops instead of guessing (fail closed).
 */
export function hostDatabaseSettings(env: NodeJS.ProcessEnv = process.env): pg.ClientConfig {
	const migration = Boolean(env.Database__Migration__Username);
	const user = migration ? env.Database__Migration__Username : env.Database__Username;
	const missing = [
		...(env.Database__Port ? [] : ['Database__Port']),
		...(env.Database__Name ? [] : ['Database__Name']),
		...(user ? [] : ['Database__Migration__Username (or Database__Username)'])
	];
	if (missing.length > 0) {
		throw new Error(
			`The E2E run cannot check the hosts' database for the development-only site ${developmentOnlySiteCode}: set ${missing.join(', ')} ` +
				'(and the password) as the README says, so this check and the hosts use the same database.'
		);
	}
	return {
		host: env.Database__Host || 'localhost',
		port: Number(env.Database__Port),
		database: env.Database__Name,
		user,
		password: migration ? env.Database__Migration__Password : env.Database__Password
	};
}

/**
 * Why the database cannot be trusted for E2E, or null when it can. Global setup runs after the hosts started and migrated
 * their database (Playwright starts the web servers first), so a database without the site table is not the hosts' one:
 * that fails closed as well.
 */
export async function developmentSiteProblem(query: Query): Promise<string | null> {
	const [table] = await query("SELECT to_regclass('public.site') IS NOT NULL AS present");
	if (table?.present !== true) {
		return (
			"The database the E2E check reached has no site table, although the hosts have migrated theirs: it is not the hosts' " +
			'database, so the run cannot tell whether that one holds the development-only site. Point Database__Host, Database__Port and ' +
			'Database__Name at the database the hosts use.'
		);
	}
	const [row] = await query('SELECT count(*)::int AS n FROM site WHERE code = $1', [developmentOnlySiteCode]);
	if (Number(row?.n ?? 0) > 0) {
		return (
			`The E2E database holds the development-only site ${developmentOnlySiteCode} (docs/demo/nbj-bc1.md), so this run cannot ` +
			`trust it: its screens and visual baselines could show the site. ${reset}`
		);
	}
	return null;
}

/** Global setup's check: throws when the hosts' database cannot be reached, is not theirs, or holds the site. */
export async function refuseADatabaseWithTheDevelopmentOnlySite(env: NodeJS.ProcessEnv = process.env): Promise<void> {
	const db = new pg.Client(hostDatabaseSettings(env));
	try {
		await db.connect();
	} catch (error) {
		// Fail closed: a database this run cannot check is not trusted either.
		throw new Error(`The E2E run cannot check the hosts' database for the development-only site (${(error as Error).message}).`);
	}
	try {
		const problem = await developmentSiteProblem(async (sql, params) => (await db.query(sql, params)).rows);
		if (problem) throw new Error(problem);
	} finally {
		await db.end();
	}
}
