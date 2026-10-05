import { expect, test } from '@playwright/test';
import { accounts, call, databaseAvailable, signIn } from '../support/accounts';
import { hosts } from '../support/hosts';

// ARV-063: the dynamic scan sent a NUL byte (%00) as siteCode and four endpoints answered 500, because the value reached
// PostgreSQL, which refuses NUL in text. Every controller now answers 400 for a NUL in the query string, a route value,
// a form field or a JSON string (NulCharacterFilter, NulRejectingStringConverter), and a siteCode that cannot be a site
// code is a site that does not exist (404, SiteScopedAttribute) wherever it bypasses the filter.

test.skip(!databaseAvailable, 'needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');

let admin: string;

test.beforeAll(async () => {
	admin = (await signIn(accounts().emailAdmin)).accessToken;
});

for (const route of ['/api/v1/admin/devices/health', '/api/v1/admin/displays', '/api/v1/admin/zone-profiles', '/api/v1/report-schedules', '/api/v1/admin/airports']) {
	test(`${route} answers 400 for a NUL byte as siteCode`, async () => {
		const response = await call('GET', `${hosts.main}${route}?siteCode=%00`, { token: admin });

		expect(response.status()).toBe(400);
	});
}

for (const route of ['/api/v1/admin/users?text=%00', '/api/v1/admin/airports?text=ab%00', '/api/v1/admin/devices?siteCode=DMO&text=%00', '/api/v1/admin/alert-rules?siteCode=DMO&text=%00']) {
	test(`${route} answers 400, not 500`, async () => {
		const response = await call('GET', `${hosts.main}${route}`, { token: admin });

		expect(response.status(), await response.text()).toBe(400);
	});
}

test('a NUL byte in an airport name is refused before the insert', async () => {
	const response = await call('POST', `${hosts.main}/api/v1/admin/airports`, { token: admin, data: { iataCode: 'NUL', name: 'Null\u0000port', timeZoneId: 'Asia/Dubai' } });

	expect(response.status(), await response.text()).toBe(400);
	expect(await response.text()).toContain('NUL character');
});

function rule(name: string) {
	return {
		siteCode: 'DMO',
		name,
		zones: ['D-EG'],
		metric: 'QueueLength',
		comparator: 'GreaterThan',
		threshold: 40,
		minQueueLength: null,
		clearThreshold: null,
		sustainMinutes: 2,
		clearAfterMinutes: 2,
		severity: 'Warning',
		ownerRole: 'BorderShiftSupervisor',
		escalateAfterMinutes: null,
		escalateToRole: null,
		escalationContact: null,
		notifyByEmail: false,
		enabled: false
	};
}

test('a NUL byte in a JSON string is refused, not passed to the database', async () => {
	const runId = Date.now().toString(36);
	// The same rule without the NUL byte is accepted, so the refusal below is the byte's alone.
	const clean = await call('POST', `${hosts.main}/api/v1/admin/alert-rules`, { token: admin, data: rule(`E2E nul control ${runId}`) });
	expect(clean.status(), await clean.text()).toBe(201);

	const response = await call('POST', `${hosts.main}/api/v1/admin/alert-rules`, { token: admin, data: rule(`E2E nul \u0000 byte ${runId}`) });

	expect(response.status(), await response.text()).toBe(400);
});
