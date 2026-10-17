import { HttpTransportType, HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr';
import { totpCode } from '../../support/accounts';
import { requestAllowed } from './guard';
import { credential, credentialVariables, scrub, smokeTarget, variable, type SmokeTarget } from './target';

// ARV-128: every call the smoke suite makes. Node's fetch, never Playwright's request fixture, whose call log prints the
// request headers (the bearer token, the device and display keys) into errors and the report. Only the configured
// origins are called, redirects are not followed (a credential never goes to another host), only reads are sent to a
// non-local target (guard.ts), and an error names the method and path only, scrubbed of every secret.

export const target: SmokeTarget = smokeTarget();

export interface SmokeResponse {
	status: number;
	headers: Record<string, string>;
	text: string;
	json<T = any>(): T;
}

export interface SmokeRequest {
	token?: string;
	headers?: Record<string, string>;
	json?: unknown;
}

function origins(): string[] {
	return [target.base, target.main, target.ingest, target.integration].filter((v): v is string => v !== undefined);
}

/** One call to the target; refused before it is sent when it is not a read (non-local) or leaves the target's origins. */
export async function send(method: string, url: string, options: SmokeRequest = {}): Promise<SmokeResponse> {
	const parsed = new URL(url);
	const where = `${method} ${parsed.origin}${parsed.pathname}`;
	if (!origins().some((origin) => url === origin || url.startsWith(`${origin}/`))) throw new Error(`ARV-128: ${where} is not one of the smoke target's addresses; refused`);
	if (!requestAllowed(method, url, target.local)) throw new Error(`ARV-128: the smoke suite sends only reads to a non-local target; ${where} refused`);
	const headers: Record<string, string> = { Accept: 'application/json', ...options.headers };
	if (options.token) headers.Authorization = `Bearer ${options.token}`;
	if (options.json !== undefined) headers['Content-Type'] = 'application/json';
	let response: Response;
	try {
		response = await fetch(url, {
			method,
			headers,
			body: options.json === undefined ? undefined : JSON.stringify(options.json),
			redirect: 'manual',
			signal: AbortSignal.timeout(30_000)
		});
	} catch (error) {
		const cause = (error as { cause?: { code?: string } }).cause?.code ?? (error as Error).name;
		throw new Error(scrub(`${where} failed: ${cause}`, options.token ? [options.token] : []));
	}
	const text = await response.text();
	const lowered: Record<string, string> = {};
	response.headers.forEach((value, name) => (lowered[name.toLowerCase()] = value));
	return {
		status: response.status,
		headers: lowered,
		text,
		json: <T>() => {
			try {
				return JSON.parse(text) as T;
			} catch {
				throw new Error(`${where} did not answer JSON`);
			}
		}
	};
}

// The replay guard takes each TOTP step once per account and only after the last one it took, with one step of skew
// (CWE-287). Every sign-in of a run is in one worker (serial signed-in.spec.ts), so the last step is kept here.
let lastStep = -1;

/** A code for a step this run has not used yet: the current one, or the next (within the server's one step of skew). */
export async function unusedCode(secret: string): Promise<string> {
	const now = Math.floor(Date.now() / 30_000);
	const step = Math.max(now, lastStep + 1);
	if (step > now + 1) await new Promise((resolve) => setTimeout(resolve, (step - 1) * 30_000 - Date.now() + 200));
	lastStep = step;
	return totpCode(secret, 0, step * 30_000);
}

export interface SmokeSession {
	accessToken: string;
}

/** Signs the smoke account in through the web origin's /api (as the browser does), with a TOTP code where it has an authenticator. */
export async function signIn(): Promise<SmokeSession> {
	const secret = variable(credentialVariables.totpSecret);
	const body = {
		userName: credential(credentialVariables.userName),
		password: credential(credentialVariables.password),
		...(secret ? { code: await unusedCode(secret) } : {})
	};
	const response = await send('POST', `${target.base}/api/auth/login`, { json: body });
	if (response.status === 401 && !secret && /mfa/i.test(response.text)) {
		throw new Error(`ARV-128: the smoke account has an authenticator; set ${credentialVariables.totpSecret}`);
	}
	// The answer's body holds the access token: only its status is ever shown.
	if (response.status !== 200) throw new Error(`ARV-128: the smoke account's sign-in answered ${response.status}`);
	const accessToken = response.json<{ accessToken?: string }>().accessToken;
	if (!accessToken) throw new Error('ARV-128: the sign-in answered no access token');
	return { accessToken };
}

/** Ends the session on the server (the refresh token family is revoked with it). */
export async function signOut(session: SmokeSession): Promise<number> {
	return (await send('POST', `${target.base}/api/auth/logout`, { token: session.accessToken })).status;
}

/**
 * The live hub on the web origin, as the web app connects: WebSockets only, no negotiate request, the token as the
 * access_token query value (which only /hubs accepts and every log redacts). The client logs nothing.
 */
export function hub(token?: string): HubConnection {
	return new HubConnectionBuilder()
		.withUrl(`${target.base}/hubs/live`, {
			transport: HttpTransportType.WebSockets,
			skipNegotiation: true,
			...(token ? { accessTokenFactory: () => token } : {})
		})
		.configureLogging(LogLevel.None)
		.build();
}

/** Runs a hub call, rethrowing its error with every secret and token scrubbed from the message. */
export async function scrubbed<T>(work: Promise<T>, token?: string): Promise<T> {
	try {
		return await work;
	} catch (error) {
		throw new Error(scrub(String((error as Error)?.message ?? error), token ? [token] : []));
	}
}
