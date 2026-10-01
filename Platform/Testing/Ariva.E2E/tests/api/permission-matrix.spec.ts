import { expect, test } from '@playwright/test';
import { rowsForRunningHosts } from '../support/permission-matrix';

// ARV-009: the anonymous column of security/permission-matrix.json against the running hosts. The role columns are
// exercised in-process by Ariva.UnitTests (PermissionMatrixTests) until real sign-in exists; ARV-010a adds them here
// with tokens issued by Ariva.Api.Main.
test.describe('permission matrix: anonymous callers', () => {
	for (const row of rowsForRunningHosts()) {
		test(`${row.method} ${row.host}${row.route} answers ${row.expected.anonymous}`, async ({ request }) => {
			const response = await request.fetch(row.url, { method: row.method });

			expect(response.status()).toBe(row.expected.anonymous);
		});
	}
});
