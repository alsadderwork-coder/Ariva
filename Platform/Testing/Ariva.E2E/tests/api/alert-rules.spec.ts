import { expect, test } from '@playwright/test';
import { accounts, call, databaseAvailable, login, signIn, totpCode } from '../support/accounts';
import { expectNoLeak } from '../support/api-assertions';
import { hosts } from '../support/hosts';

// ARV-037: alert rules through the API. The demo seed's R-001 to R-005 are on DMO; an administrator creates, edits and
// deletes a rule there (codes go on from the seed's, enums travel by their exact names, deletes are soft, need a second
// factor in the last 15 minutes and the code is not given again); typed values only: numbers, other cases, padding or
// comma-joined names for an enum, a comparator that does not fit the metric, zones outside the published profile are
// refused, hostile names are kept as plain text; callers of other sites see nothing, roles a caller does not hold are
// refused, and handlers read but never write.

test.skip(!databaseAvailable, 'alert rules need the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const api = `${hosts.main}/api/v1/admin/alert-rules`;
let administrator: string;
let ruleId: string;
let ruleCode: string;

const rule = (overrides: Record<string, unknown> = {}) => ({
	siteCode: 'DMO',
	name: 'E2E visitors wave',
	zones: ['A-VIS', 'D-VIS'],
	metric: 'Nowcast',
	comparator: 'GreaterThan',
	threshold: 20,
	minQueueLength: 5,
	clearThreshold: 15,
	sustainMinutes: 2,
	clearAfterMinutes: 2,
	severity: 'Warning',
	ownerRole: 'BorderShiftSupervisor',
	escalateAfterMinutes: 10,
	escalateToRole: 'TerminalDutyManager',
	escalationContact: null,
	notifyByEmail: false,
	enabled: true,
	...overrides
});

const number = (code: string) => Number(code.slice(2));

test.beforeAll(async () => {
	const { userName, password, totpSecret } = accounts().alertAdmin;
	const signedIn = await login(userName, password, undefined, undefined, { code: totpCode(totpSecret!) });
	expect(signedIn.status(), await signedIn.text()).toBe(200);
	administrator = (await signedIn.json()).accessToken;
});

test('the demo seed gives DMO the prototype rules R-001 to R-005', async () => {
	const page = await call('GET', `${api}?siteCode=DMO&pageSize=500`, { token: administrator });
	expect(page.status(), await page.text()).toBe(200);
	const body = await page.json();
	const seeded = body.data.filter((r: any) => /^R-00[1-5]$/.test(r.code));
	// Another run may have deleted some; a fresh database has all five.
	expect(seeded.length).toBeGreaterThan(0);
	const r001 = seeded.find((r: any) => r.code === 'R-001');
	if (r001) expect(r001).toMatchObject({ metric: 'Nowcast', comparator: 'GreaterThan', threshold: 15, severity: 'Critical', ownerRole: 'BorderShiftSupervisor' });
});

test('an administrator creates, edits and deletes a rule; the code is never given again', async () => {
	const created = await call('POST', api, { token: administrator, data: rule() });
	expect(created.status(), await created.text()).toBe(201);
	const view = await created.json();
	ruleId = view.id;
	ruleCode = view.code;
	expect(view.code).toMatch(/^R-[0-9]{3,4}$/);
	expect(number(view.code)).toBeGreaterThan(5);
	expect(view).toMatchObject({ siteCode: 'DMO', metric: 'Nowcast', comparator: 'GreaterThan', severity: 'Warning', zones: ['A-VIS', 'D-VIS'] });
	expect(created.headers()['location']).toContain(`/api/v1/admin/alert-rules/${ruleId}`);

	const updated = await call('PUT', `${api}/${ruleId}`, { token: administrator, data: rule({ threshold: 25, enabled: false }) });
	expect(updated.status(), await updated.text()).toBe(200);
	expect(await updated.json()).toMatchObject({ code: ruleCode, threshold: 25, enabled: false });
	expect((await call('PUT', `${api}/${ruleId}`, { token: administrator, data: rule({ siteCode: 'E2E1' }) })).status(), 'a rule stays in its site').toBe(400);

	const passwordOnly = (await signIn(accounts().SystemAdministrator)).accessToken;
	const unconfirmed = await call('DELETE', `${api}/${ruleId}`, { token: passwordOnly });
	expect(unconfirmed.status(), 'deleting is critical: a second factor in the last 15 minutes').toBe(401);
	expect(unconfirmed.headers()['www-authenticate']).toContain('insufficient_user_authentication');
	expect((await call('GET', `${api}/${ruleId}`, { token: administrator })).status(), 'still there').toBe(200);
	expect((await call('DELETE', `${api}/${ruleId}`, { token: administrator })).status()).toBe(204);
	expect((await call('GET', `${api}/${ruleId}`, { token: administrator })).status()).toBe(404);
	expect((await call('DELETE', `${api}/${ruleId}`, { token: administrator })).status(), 'deleted twice').toBe(404);

	const next = await call('POST', api, { token: administrator, data: rule({ name: 'E2E after the delete' }) });
	expect(next.status(), await next.text()).toBe(201);
	const nextView = await next.json();
	expect(number(nextView.code)).toBeGreaterThan(number(ruleCode));
	await call('DELETE', `${api}/${nextView.id}`, { token: administrator });
});

test('typed values only: numbers for enums, misfit comparators, unknown zones and roles are refused', async () => {
	for (const [why, overrides] of [
		['metric as a number', { metric: '0' }],
		['metric in another case', { metric: 'nowcast' }],
		['metric with padding', { metric: 'Nowcast ' }],
		['metric names joined', { metric: 'QueueLength,BinP90' }],
		['combined severity flags', { severity: 'Critical,Warning' }],
		['severity names joined', { severity: 'Info,Warning' }],
		['comparator names joined', { comparator: 'GreaterThan,GreaterOrEqual' }],
		['IsTrue on a number', { comparator: 'IsTrue' }],
		['clear threshold on the firing side', { clearThreshold: 30 }],
		['threshold beyond the unit', { threshold: 601 }],
		['zone outside the published profile', { zones: ['A-VIS', 'NOT-A-ZONE'] }],
		['role outside the catalogue', { ownerRole: 'Root' }],
		['escalation without minutes', { escalateAfterMinutes: null }],
		['sustain out of range', { sustainMinutes: 0 }],
		['name with a control character', { name: 'Rule\u0007' }],
		['name with a bidirectional override', { name: 'Rule ‮evil' }],
		['an expression field', { expression: 'queue > 10', name: 'E2E expression' }]
	] as const) {
		const response = await call('POST', api, { token: administrator, data: rule(overrides) });
		if (why === 'an expression field' && response.status() !== 400) {
			// Refused, or ignored as an unknown member: the rule is the typed fields, nothing evaluates the extra one (CWE-94).
			expect(response.status(), `${why}: ${await response.text()}`).toBe(201);
			const view = await response.json();
			expect(view).not.toHaveProperty('expression');
			await call('DELETE', `${api}/${view.id}`, { token: administrator });
			continue;
		}
		expect(response.status(), `${why}: ${await response.text()}`).toBe(400);
		expectNoLeak(await response.text(), why);
	}
});

test('hostile names are stored and returned as plain text', async () => {
	for (const name of ['<script>alert(1)</script>', "'; DROP TABLE alert_rule; --", '{{7*7}} ${7*7}', '=cmd|\' /C calc\'!A0', 'قاعدة الانتظار']) {
		const created = await call('POST', api, { token: administrator, data: rule({ name }) });
		expect(created.status(), `${name}: ${await created.text()}`).toBe(201);
		const view = await created.json();
		expect(view.name).toBe(name);
		expect(created.headers()['content-type']).toContain('application/json');
		const read = await call('GET', `${api}/${view.id}`, { token: administrator });
		expect((await read.json()).name).toBe(name);
		await call('DELETE', `${api}/${view.id}`, { token: administrator });
	}
});

test("other sites' callers see nothing, roles they lack are refused, and handlers only read", async () => {
	const probe = await (await call('POST', api, { token: administrator, data: rule({ name: 'E2E scope probe' }) })).json();
	try {
		const border = (await signIn(accounts().BorderShiftSupervisor)).accessToken;
		expect((await call('GET', `${api}/${probe.id}`, { token: border })).status(), 'a DMO rule from E2E1').toBe(404);
		expect((await call('PUT', `${api}/${probe.id}`, { token: border, data: rule() })).status()).toBe(404);
		expect((await call('DELETE', `${api}/${probe.id}`, { token: border })).status(), 'step-up is asked first; nothing about the rule is told').toBe(401);
		expect((await call('GET', `${api}/${probe.id}`, { token: administrator })).status(), 'still there').toBe(200);
		expect((await call('POST', api, { token: border, data: rule() })).status(), 'create in a site outside the caller').toBe(400);
		const listed = await call('GET', api, { token: border });
		expect(listed.status()).toBe(200);
		expect((await listed.json()).data.filter((r: any) => r.siteCode === 'DMO')).toHaveLength(0);
		const zones = await call('POST', api, { token: border, data: rule({ siteCode: 'E2E1', escalateToRole: null, escalationContact: null }) });
		expect(zones.status(), 'E2E1 has no published profile with these zones').toBe(400);
		const lacked = await call('POST', api, { token: border, data: rule({ siteCode: 'E2E1', ownerRole: 'SystemAdministrator' }) });
		expect(lacked.status(), 'a role the creator does not hold').toBe(400);
		expect(await lacked.text(), 'refused for the role, not only for the zones').toContain('a role the caller does not hold');

		const handler = (await signIn(accounts().HandlerStationManager)).accessToken;
		expect((await call('GET', api, { token: handler })).status()).toBe(200);
		expect((await call('POST', api, { token: handler, data: rule({ siteCode: 'E2E1' }) })).status()).toBe(403);
		expect((await call('PUT', `${api}/${probe.id}`, { token: handler, data: rule() })).status()).toBe(403);
		expect((await call('DELETE', `${api}/${probe.id}`, { token: handler })).status()).toBe(403);
		expect((await call('GET', api)).status(), 'anonymous').toBe(401);
	} finally {
		await call('DELETE', `${api}/${probe.id}`, { token: administrator });
	}
});
