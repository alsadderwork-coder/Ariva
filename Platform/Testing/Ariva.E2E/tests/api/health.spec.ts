import { expect, test } from '@playwright/test';
import { expectApiSecurityHeaders } from '../support/api-assertions';
import { apiHosts, probes } from '../support/hosts';

// The Kubernetes probes are the only anonymous endpoints (security/allowlist.json, SEC-052). They must answer
// 200 without credentials on every host, with a status word only and the API security headers.
test.describe('health probes', () => {
	for (const host of apiHosts) {
		for (const probe of probes) {
			test(`${host.name} ${probe} returns 200 without credentials`, async ({ request }) => {
				const response = await request.get(`${host.url}${probe}`);

				expect(response.status()).toBe(200);
				expect(response.headers()['content-type']).toContain('application/json');
				expect(await response.json()).toEqual({
					status: 'Healthy',
					probe: probe.replace('/health/', '')
				});
				expectApiSecurityHeaders(response);
			});
		}
	}
});
