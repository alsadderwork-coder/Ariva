import crypto from 'node:crypto';
import { accounts, call, login, unusedTotpCode } from './accounts';
import { hosts } from './hosts';

// ARV-128: the local E2E stack, prepared the way an administrator prepares a deployed environment for the smoke suite
// (wiki 04 section 8): the smoke site E2SM (created by the account seed: the smoke accounts hold it) gets a published
// zone profile with one queue zone if it has none, and each run registers a test device and creates a display there.
// The values go into the runner's environment (ARIVA_SMOKE_*), which the workers inherit, so the smoke specs read
// their credentials from the environment here exactly as in a deployed run. Global setup calls this once the hosts are up.

export const smokeSite = 'E2SM';

/**
 * Whether this run has the `smoke` project: the ordinary run. The visual, zap, demo and rehearsal runs play their own
 * project alone, on their own data, so they neither get it nor provision for it.
 */
export const smokeStack =
	!process.env.ARIVA_E2E_VISUAL_WS && process.env.ARIVA_E2E_ZAP !== '1' && process.env.ARIVA_E2E_DEMO !== '1' && process.env.ARIVA_E2E_REHEARSAL !== '1';

/** The ARIVA_SMOKE_* values global setup provides (the config sets the addresses and the account). */
export const provisionedVariables = ['ARIVA_SMOKE_ZONE', 'ARIVA_SMOKE_DEVICE_KEY', 'ARIVA_SMOKE_DISPLAY_CODE', 'ARIVA_SMOKE_DISPLAY_KEY'] as const;

const letters = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ';
const randomLetters = (n: number) => Array.from({ length: n }, () => letters[crypto.randomInt(26)]).join('');

export async function provisionSmokeSite(): Promise<void> {
	const admin = accounts().smokeAdmin;
	const base = `${hosts.main}/api/v1/admin`;
	const signedIn = await login(admin.userName, admin.password, undefined, undefined, { code: await unusedTotpCode(admin.totpSecret!) });
	if (signedIn.status() !== 200) throw new Error(`smoke provisioning: the administrator's sign-in answered ${signedIn.status()}`);
	const token = (await signedIn.json()).accessToken as string;
	const read = async (resource: string) => {
		const response = await call('GET', `${base}/${resource}`, { token });
		if (response.status() !== 200) throw new Error(`smoke provisioning: GET ${resource} answered ${response.status()}`);
		return response.json();
	};
	const create = async (resource: string, data: unknown) => {
		const response = await call('POST', `${base}/${resource}`, { token, data });
		if (response.status() !== 201 && response.status() !== 200) throw new Error(`smoke provisioning: POST ${resource} answered ${response.status()}: ${(await response.text()).slice(0, 300)}`);
		return response.json();
	};

	try {
		// The site's published profile (a reused database keeps the one an earlier run published), or a new one.
		const profiles = (await read(`zone-profiles?siteCode=${smokeSite}`)) as { id: string; status: string }[];
		const published = profiles.find((profile) => profile.status === 'Published');
		let zone: string;
		let levelId: string;
		if (published) {
			const profile = (await read(`zone-profiles/${published.id}`)) as { zones: { name: string; kind: string; levelId: string }[] };
			const queue = profile.zones.find((z) => z.kind === 'Queue');
			if (!queue) throw new Error(`smoke provisioning: ${smokeSite}'s published profile has no queue zone`);
			({ name: zone, levelId } = queue);
		} else {
			zone = 'Smoke Queue';
			const airport = (await create('airports', { iataCode: randomLetters(3), name: `E2E smoke ${smokeSite}`, timeZoneId: 'Asia/Dubai' })).id;
			const terminal = (await create('terminals', { airportId: airport, code: 'T1', name: 'T1', siteCode: smokeSite })).id;
			levelId = (await create('levels', { terminalId: terminal, code: 'L0', name: 'L0', floorNumber: 0, widthMetres: 100, depthMetres: 50 })).id;
			const draftCreated = await create('zone-profiles/drafts', { siteCode: smokeSite, name: 'Smoke' });
			const draft = draftCreated.id ?? draftCreated.profile?.id;
			const zoneCreated = await create(`zone-profiles/${draft}/zones`, { name: zone, kind: 'Queue', levelId, polygon: '10 10,34 10,34 22,10 22' });
			const zoneId = zoneCreated.id ?? zoneCreated.profile?.id;
			await create(`zone-profiles/${draft}/lines`, { name: 'Smoke entry', role: 'Entry', levelId, startX: 10, startY: 12, endX: 10, endY: 16, zoneId });
			await create(`zone-profiles/${draft}/lines`, { name: 'Smoke exit', role: 'Exit', levelId, startX: 30, startY: 22, endX: 34, endY: 22, zoneId });
			const { geometryHash } = await read(`zone-profiles/${draft}/validation`);
			const publish = await call('POST', `${base}/zone-profiles/${draft}/publish`, { token, data: { geometryHash } });
			if (publish.status() !== 200) throw new Error(`smoke provisioning: publishing ${smokeSite}'s profile answered ${publish.status()}`);
		}

		// A test device and a display of this run (their credentials are shown once, at creation).
		const suffix = crypto.randomBytes(3).toString('hex').toUpperCase();
		const device = await create('devices', {
			code: `SMK-${suffix}`, family: 'StereoVision', model: 'PC2SE', transport: 'HttpsPush', dialect: 'Xovis', clockSource: 'Ntp',
			placement: { levelId, x: 20, y: 16, mountingHeightMetres: 5, orientationDegrees: 0, queueZoneName: zone }
		});
		const displayCode = `SMK-${suffix}`;
		const display = await create('displays', {
			siteCode: smokeSite, code: displayCode, name: 'Smoke', location: null, orientation: 'Landscape', languages: ['en', 'ar'], bandMinutes: 5,
			hysteresisMinutes: 2, staleSeconds: 150, entries: [{ zone, labels: { en: 'Smoke queue', ar: 'طابور الفحص' } }],
			fallback: { en: 'Please follow the signs', ar: 'يرجى اتباع اللوحات' }, enabled: true
		});
		Object.assign(process.env, {
			ARIVA_SMOKE_ZONE: zone,
			ARIVA_SMOKE_DEVICE_KEY: device.credential as string,
			ARIVA_SMOKE_DISPLAY_CODE: displayCode,
			ARIVA_SMOKE_DISPLAY_KEY: display.credential as string
		});
	} finally {
		await call('POST', `${hosts.main}/api/auth/logout`, { token });
	}
}
