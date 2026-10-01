import { expect, test } from '@playwright/test';
import { accounts, clientAddress, databaseAvailable, signIn } from '../support/accounts';
import { matrix, rowsForRunningHosts, type Caller } from '../support/permission-matrix';

// ARV-009 and ARV-010a: security/permission-matrix.json against the running hosts. Anonymous callers send no token;
// each role column signs in as the E2E account with that role (tokens issued by Ariva.Api.Main) and sends its bearer
// token. Rows with a body send it as JSON. Every request has its own client address, so the per-address sign-in
// limit never decides a row.

test.describe('permission matrix: anonymous callers', () => {
	for (const row of rowsForRunningHosts()) {
		test(`${row.method} ${row.host}${row.route} answers ${row.expected.anonymous}`, async ({ request }) => {
			// An anonymous row with a body reaches the service (sign-in), which needs the database.
			test.skip(!databaseAvailable && row.access === 'anonymous' && row.body !== undefined, 'needs the E2E database');
			const response = await request.fetch(row.url, {
				method: row.method,
				data: row.body,
				headers: { 'X-Forwarded-For': clientAddress() }
			});

			expect(response.status()).toBe(row.expected.anonymous);
		});
	}
});

test.describe('permission matrix: signed-in roles', () => {
	test.skip(!databaseAvailable, 'sign-in needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');

	for (const role of matrix.roles) {
		test(`${role} gets the matrix status on every endpoint`, async ({ request }) => {
			const token = await signIn(request, accounts()[role]);
			const mismatches: string[] = [];

			for (const row of rowsForRunningHosts()) {
				const response = await request.fetch(row.url, {
					method: row.method,
					data: row.body,
					headers: { Authorization: `Bearer ${token.accessToken}`, 'X-Forwarded-For': clientAddress() }
				});
				const expected = row.expected[role as Caller];
				if (response.status() !== expected) mismatches.push(`${row.method} ${row.host}${row.route}: expected ${expected}, got ${response.status()}`);
			}

			expect(mismatches).toEqual([]);
		});
	}
});
