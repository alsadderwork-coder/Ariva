import { isLocalUrl } from '../../support/local-target';

// ARV-128: what the smoke suite targets and signs in with, from environment variables only. No default points anywhere and
// nothing is read from a file: a deployed run gets every value from the operator's environment (wiki 04 section 8), and the
// local E2E stack's run gets them from playwright.config.ts and its global setup, which provision the stack.

/** The addresses the smoke suite calls. */
export const urlVariables = {
	/** The web origin: the web app, and /api and /hubs on it (the ingress routes them to Ariva.Api.Main, ADR-0026). */
	base: 'ARIVA_SMOKE_BASE_URL',
	/** Ariva.Api.Main's own host, for its health probes. */
	main: 'ARIVA_SMOKE_MAIN_URL',
	/** Ariva.Api.Ingest's host: its health probes and the test device's check. */
	ingest: 'ARIVA_SMOKE_INGEST_URL',
	/** Optional: Ariva.Api.Integration's host, for its health probes. */
	integration: 'ARIVA_SMOKE_INTEGRATION_URL'
} as const;

/** The smoke account, its site, the test device and the display. Secret values are never printed (see secretValues). */
export const credentialVariables = {
	userName: 'ARIVA_SMOKE_USERNAME',
	password: 'ARIVA_SMOKE_PASSWORD',
	/** The account's authenticator secret (base32), where it has one: every committed environment requires one (Auth:TotpRequired). */
	totpSecret: 'ARIVA_SMOKE_TOTP_SECRET',
	/** The smoke site, or several separated by commas: the account must hold exactly these sites (checks.ts). */
	site: 'ARIVA_SMOKE_SITE',
	/** Optional: a queue zone of the site's published profile to join on the live hub (default: the profile's first queue zone). */
	zone: 'ARIVA_SMOKE_ZONE',
	deviceKey: 'ARIVA_SMOKE_DEVICE_KEY',
	displayCode: 'ARIVA_SMOKE_DISPLAY_CODE',
	displayKey: 'ARIVA_SMOKE_DISPLAY_KEY'
} as const;

const optional = new Set<string>([urlVariables.integration, credentialVariables.totpSecret, credentialVariables.zone]);

/** The variables whose values are secrets: never printed, never in a step title, and looked for in the run's artifacts. */
export const secretVariables = [credentialVariables.password, credentialVariables.totpSecret, credentialVariables.deviceKey, credentialVariables.displayKey] as const;

export interface SmokeTarget {
	base: string;
	main: string;
	ingest: string;
	integration?: string;
	/** True only when every address is on this machine (tests/support/local-target.ts). */
	local: boolean;
}

/** The value of a variable, trimmed; undefined when unset or blank. */
export function variable(name: string): string | undefined {
	const value = process.env[name]?.trim();
	return value ? value : undefined;
}

/** The required variables that are not set, by name. */
export function missingVariables(scope: 'urls' | 'all'): string[] {
	const names = scope === 'urls' ? Object.values(urlVariables) : [...Object.values(urlVariables), ...Object.values(credentialVariables)];
	return names.filter((name) => !optional.has(name) && variable(name) === undefined);
}

/** The addresses; an address that is not an http or https origin is refused (by name, never by value). */
export function smokeTarget(): SmokeTarget {
	const missing = missingVariables('urls');
	if (missing.length > 0) throw new Error(`ARV-128: set ${missing.join(', ')} (wiki 04 section 8)`);
	const read = (name: string): string | undefined => {
		const value = variable(name);
		if (value === undefined) return undefined;
		let url: URL;
		try {
			url = new URL(value);
		} catch {
			throw new Error(`ARV-128: ${name} is not an address`);
		}
		if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password || url.search || url.hash) {
			throw new Error(`ARV-128: ${name} must be an http or https origin, without credentials, query or fragment`);
		}
		return url.origin + url.pathname.replace(/\/+$/, '');
	};
	const target = {
		base: read(urlVariables.base)!,
		main: read(urlVariables.main)!,
		ingest: read(urlVariables.ingest)!,
		integration: read(urlVariables.integration)
	};
	const addresses = [target.base, target.main, target.ingest, target.integration].filter((v): v is string => v !== undefined);
	return { ...target, local: addresses.every(isLocalUrl) };
}

/** A required credential or setting; fails the test with the variable's name only. */
export function credential(name: (typeof credentialVariables)[keyof typeof credentialVariables]): string {
	const value = variable(name);
	if (value === undefined) throw new Error(`ARV-128: set ${name} (wiki 04 section 8)`);
	return value;
}

/** The secret values that are set (for redaction and for the artifact scan). */
export function secretValues(): string[] {
	return secretVariables.map((name) => variable(name)).filter((value): value is string => value !== undefined && value.length >= 6);
}

/** Replaces every secret value, every bearer token and every access_token value in a text. */
export function scrub(text: string, extra: string[] = []): string {
	let out = text;
	for (const value of [...secretValues(), ...extra].filter((v) => v && v.length >= 6).sort((a, b) => b.length - a.length)) {
		out = out.split(value).join('[redacted]');
	}
	return out
		.replace(/access_token=[^&\s"']+/gi, 'access_token=[redacted]')
		.replace(/Bearer\s+[A-Za-z0-9\-._~+/]+=*/gi, 'Bearer [redacted]')
		.replace(/\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*/g, '[jwt]');
}
