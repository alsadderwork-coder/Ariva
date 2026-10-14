import { expect, test } from '@playwright/test';
import { expectApiSecurityHeaders } from '../support/api-assertions';
import { apiHosts, probes } from '../support/hosts';

// The Kubernetes probes are the only anonymous endpoints (security/allowlist.json, SEC-052). They must answer
// 200 without credentials on every host, with a status word and the probe's name only and the API security headers.
// ARV-125a: readiness says "Degraded" (still 200) while a readiness check is Degraded and "Unhealthy" (503) while one
// is Unhealthy; the body never names a check, a dependency or a detail (CWE-200). In this run every dependency is up,
// so every probe says "Healthy"; the Degraded and Unhealthy bodies are proven in ReadinessTests (unit).
test.describe('health probes', () => {
	for (const host of apiHosts) {
		for (const probe of probes) {
			test(`${host.name} ${probe} returns 200 without credentials`, async ({ request }) => {
				const response = await request.get(`${host.url}${probe}`);

				expect(response.status()).toBe(200);
				expect(response.headers()['content-type']).toContain('application/json');
				const body = await response.json();
				expect(Object.keys(body)).toEqual(['status', 'probe']);
				expect(body).toEqual({
					status: 'Healthy',
					probe: probe.replace('/health/', '')
				});
				expectApiSecurityHeaders(response);
			});
		}
	}
});
