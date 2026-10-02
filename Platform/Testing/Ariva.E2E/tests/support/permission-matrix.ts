import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { hosts, type HostName } from './hosts';

// security/permission-matrix.json (ARV-009): every endpoint and the status each caller gets. Ariva.UnitTests checks it
// against the hosts and the role seed; the E2E suites read the same rows so both sides test one specification.

export type Caller = 'anonymous' | 'BorderShiftSupervisor' | 'TerminalDutyManager' | 'HandlerStationManager' | 'SystemAdministrator';

export interface MatrixRow {
	host: string;
	method: string;
	route: string;
	access: 'anonymous' | 'authenticated' | 'permission' | 'device' | 'simulation';
	permissions?: string[];
	body?: unknown;
	expected: Record<Caller, number>;
}

interface Matrix {
	roles: Exclude<Caller, 'anonymous'>[];
	routeValues: Record<string, string>;
	endpoints: MatrixRow[];
}

const here = path.dirname(fileURLToPath(import.meta.url));
const file = path.resolve(here, '..', '..', '..', '..', '..', 'security', 'permission-matrix.json');

export const matrix: Matrix = JSON.parse(fs.readFileSync(file, 'utf8'));

/** The route template with its parameters filled from routeValues: {id:guid} becomes routeValues.guid, {siteCode} routeValues.siteCode. */
export function pathOf(route: string): string {
	return route.replace(/\{([a-zA-Z]+)(?::([a-z]+))?\}/g, (_, name: string, type?: string) => {
		const value = matrix.routeValues[type ?? name];
		if (!value) throw new Error(`permission-matrix.json has no routeValues.${type ?? name} for ${route}`);
		return value;
	});
}

/** Rows for the hosts this E2E run starts, with the URL to call. */
export function rowsForRunningHosts(): (MatrixRow & { url: string })[] {
	return matrix.endpoints
		.filter((row) => row.host in hosts)
		.map((row) => ({ ...row, url: `${hosts[row.host as HostName]}${pathOf(row.route)}` }));
}
