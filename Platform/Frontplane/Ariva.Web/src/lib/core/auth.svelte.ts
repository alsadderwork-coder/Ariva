/**
 * The signed-in session in the browser (ARV-051, ADR-0026 section 7). The access token lives in memory only, never in
 * storage: a new tab or a reload gets one from the refresh cookie (__Secure-ariva_rt, HttpOnly, path /api/auth), which
 * the browser sends only to Ariva's own /api/auth on the same origin. One tab refreshes for all: the refresh runs under
 * the Web Locks API, and the tab that refreshed shares the new token with the others over a BroadcastChannel, so the
 * single-use refresh token is presented once. Signing out is shared the same way.
 */
import { goto } from '$app/navigation';
import { Endpoints } from './Endpoints';

export interface TokenAnswer {
	accessToken: string;
	tokenType: string;
	expiresIn: number;
	scope: string | null;
	recoveryCodesRemaining?: number | null;
}

/** The signed-in user (GET /api/auth/me): what the web shows; the server stays the authority. */
export interface CurrentUser {
	userName: string;
	displayName: string;
	roles: string[];
	permissions: string[];
	allSites: boolean;
	sites: string[];
	mustChangePassword: boolean;
	totpEnrolled: boolean;
	pending: boolean;
}

/** An RFC 9457 problem as Ariva sends it (type, title, detail, and an error code for the auth cases). */
export interface Problem {
	status: number;
	type?: string;
	title?: string;
	detail?: string;
	error?: string;
	errors?: Record<string, string[]>;
}

type Message =
	| { kind: 'token'; token: string; expiresAt: number; pending: boolean }
	| { kind: 'signed-out' };

const channelName = 'ariva-auth';
const lockName = 'ariva-auth-refresh';
/** Refresh this long before the token expires (the server's clock skew allowance is 30 seconds). */
const refreshAhead = 60_000;
/**
 * A hint that this browser signed in, so a visit without one does not send a refresh that can only fail. It holds no
 * credential (the refresh token stays in the HttpOnly cookie); a stale hint costs one refused refresh.
 */
const hintKey = 'ariva-signed-in';

function readHint(): boolean {
	try {
		return localStorage.getItem(hintKey) === '1';
	} catch {
		return true;
	}
}

function writeHint(signedIn: boolean): void {
	try {
		if (signedIn) localStorage.setItem(hintKey, '1');
		else localStorage.removeItem(hintKey);
	} catch {
		// storage blocked: every visit tries the refresh
	}
}

/** The token's subject (the user id), read for display decisions only; the server verifies every token. */
function subjectOf(token: string): string | null {
	try {
		const payload = token.split('.')[1] ?? '';
		const json = atob(payload.replace(/-/g, '+').replace(/_/g, '/'));
		const sub = (JSON.parse(json) as { sub?: unknown }).sub;
		return typeof sub === 'string' ? sub : null;
	} catch {
		return null;
	}
}

function apiUrl(path: string): string {
	return `${Endpoints.main.baseUrl.replace(/\/+$/, '')}/${path.replace(/^\/+/, '')}`;
}

/** Reads a problem body (or the status alone) from a failed answer. */
export async function readProblem(response: Response): Promise<Problem> {
	const problem: Problem = { status: response.status };
	if ((response.headers.get('content-type') ?? '').includes('json')) {
		try {
			const body = (await response.json()) as Partial<Problem>;
			problem.type = typeof body.type === 'string' ? body.type : undefined;
			problem.title = typeof body.title === 'string' ? body.title : undefined;
			problem.detail = typeof body.detail === 'string' ? body.detail : undefined;
			problem.error = typeof body.error === 'string' ? body.error : undefined;
			problem.errors = typeof body.errors === 'object' && body.errors ? body.errors : undefined;
		} catch {
			// not JSON after all; the status is enough
		}
	}
	return problem;
}

/**
 * The page to return to after signing in: a path on this site only (one slash, no scheme, no backslash), so a link
 * cannot send the user elsewhere after sign-in (an open redirect).
 */
export function safeNext(value: string | null | undefined): string {
	if (
		!value ||
		!value.startsWith('/') ||
		value.startsWith('//') ||
		value.includes('\\') ||
		/[\u0000-\u001f]/.test(value)
	)
		return '/';
	if (value.startsWith('/login') || value.startsWith('/setup')) return '/';
	return value.length > 2000 ? '/' : value;
}

/** The answer of an /api/auth call: the body, or the problem. */
export interface AuthAnswer<T> {
	ok: boolean;
	/** The body when ok. */
	data: T;
	/** The status (and, when refused, the problem); status 0 when Ariva could not be reached. */
	problem: Problem;
}

/**
 * A POST to /api/auth on this origin with the access token held in memory (when there is one) and the cookie. The
 * sign-in pages and dialogs use it directly: they handle their own 401s, which must not start another step-up.
 */
export async function authPost<T>(path: string, body?: unknown): Promise<AuthAnswer<T>> {
	const headers: Record<string, string> = { Accept: 'application/json' };
	if (body !== undefined) headers['Content-Type'] = 'application/json';
	if (auth.token) headers.Authorization = `Bearer ${auth.token}`;
	let response: Response;
	try {
		response = await fetch(apiUrl(`/api/auth/${path.replace(/^\/+/, '')}`), {
			method: 'POST',
			headers,
			credentials: 'same-origin',
			body: body === undefined ? undefined : JSON.stringify(body)
		});
	} catch {
		return { ok: false, data: null as T, problem: { status: 0 } };
	}
	if (!response.ok) {
		const problem = await readProblem(response);
		// The session ended on the server (revoked, signed out elsewhere): back to sign-in, returning here afterwards.
		if (
			headers.Authorization &&
			problem.status === 401 &&
			problem.error === 'session_expired' &&
			path !== 'logout'
		) {
			await auth.sessionEnded(
				typeof location === 'undefined' ? '/' : `${location.pathname}${location.search}`
			);
		}
		return { ok: false, data: null as T, problem };
	}
	return {
		ok: true,
		data: (response.status === 204 ? null : await response.json()) as T,
		problem: { status: response.status }
	};
}

/** The messages of a refused request that are safe to show as text: validation messages first, then the detail. */
export function problemText(problem: Problem): string[] {
	const fromErrors = Object.values(problem.errors ?? {})
		.flat()
		.filter((message): message is string => typeof message === 'string' && message.length > 0);
	if (fromErrors.length) return fromErrors.slice(0, 5);
	return problem.detail ? [problem.detail] : problem.title ? [problem.title] : [];
}

class AuthState {
	token = $state<string | null>(null);
	expiresAt = $state(0);
	pending = $state(false);
	user = $state<CurrentUser | null>(null);
	/** True once the first silent refresh (on load) has finished, whatever its outcome. */
	ready = $state(false);

	private channel: BroadcastChannel | null = null;
	private timer: ReturnType<typeof setTimeout> | undefined;
	private starting: Promise<boolean> | null = null;
	/** True when the last refresh was answered with a refusal (the session is over on the server). */
	private refused = false;

	get signedIn(): boolean {
		return this.token !== null;
	}

	/** True when the user holds the permission ("Entity.Action"). */
	can(permission: string): boolean {
		return this.user?.permissions.includes(permission) ?? false;
	}

	private listen(): void {
		if (this.channel || typeof BroadcastChannel === 'undefined') return;
		this.channel = new BroadcastChannel(channelName);
		this.channel.onmessage = (event: MessageEvent<Message>) => {
			const message = event.data;
			if (message?.kind === 'token' && typeof message.token === 'string') {
				// Another account signed in in another tab: forget this tab's user, so /api/auth/me is read again.
				if (this.token !== null && subjectOf(message.token) !== subjectOf(this.token))
					this.user = null;
				this.apply(
					message.token,
					Number(message.expiresAt) || Date.now(),
					message.pending === true,
					false
				);
			} else if (message?.kind === 'signed-out') {
				this.clear(false);
			}
		};
	}

	private apply(token: string, expiresAt: number, pending: boolean, share: boolean): void {
		this.token = token;
		this.expiresAt = expiresAt;
		this.pending = pending;
		clearTimeout(this.timer);
		const wait = Math.max(5_000, expiresAt - Date.now() - refreshAhead);
		this.timer = setTimeout(() => void this.refresh(), wait);
		if (share)
			this.channel?.postMessage({ kind: 'token', token, expiresAt, pending } satisfies Message);
	}

	/** Takes a token answer from the server (sign-in, refresh, password change, step-up) and shares it. */
	accept(answer: TokenAnswer): void {
		this.listen();
		writeHint(true);
		this.apply(
			answer.accessToken,
			Date.now() + answer.expiresIn * 1000,
			answer.scope === 'pending',
			true
		);
	}

	/** Forgets the session in this tab (and, when sharing, in the others). */
	clear(share = true): void {
		clearTimeout(this.timer);
		this.token = null;
		this.expiresAt = 0;
		this.pending = false;
		this.user = null;
		writeHint(false);
		if (share) this.channel?.postMessage({ kind: 'signed-out' } satisfies Message);
	}

	/**
	 * The session ended on the server (401 session_expired, or a refresh that failed): forget it and go to the sign-in
	 * page, which returns to <paramref name="from"/> afterwards.
	 */
	async sessionEnded(from: string): Promise<void> {
		this.clear();
		const next = safeNext(from);
		await goto(next === '/' ? '/login' : `/login?next=${encodeURIComponent(next)}`, {
			replaceState: true
		});
	}

	/** On load: a session from the refresh cookie (one refresh at a time; none when this tab already has a token). */
	start(): Promise<boolean> {
		this.listen();
		if (this.token !== null || !readHint()) {
			this.ready = true;
			return Promise.resolve(this.token !== null);
		}
		this.starting ??= this.refresh().finally(() => {
			this.ready = true;
			this.starting = null;
		});
		return this.starting;
	}

	/**
	 * A new access token from the refresh cookie. Under the Web Locks API one tab refreshes at a time; a tab that waited
	 * and received a fresh token from another meanwhile does not refresh again.
	 */
	async refresh(): Promise<boolean> {
		this.listen();
		const before = this.token;
		const run = async (): Promise<boolean> => {
			if (
				this.token !== null &&
				this.token !== before &&
				this.expiresAt - Date.now() > refreshAhead
			)
				return true;
			const response = await fetch(apiUrl('/api/auth/refresh'), {
				method: 'POST',
				headers: { 'X-Ariva-Csrf': '1', Accept: 'application/json' },
				credentials: 'same-origin'
			}).catch(() => null);
			if (response?.status === 401) {
				// Only a 401 means the server no longer knows the session (it also cleared the cookie). Tell the other
				// tabs only when this tab had a session: a tab that never signed in has nothing to end.
				this.refused = true;
				this.clear(before !== null);
				return false;
			}
			if (!response?.ok) {
				// Unreachable, rate limited (429), refused for its origin (403) or restarting (5xx): the session may well
				// live on, so keep it and try again shortly rather than run past expiry or pretend it ended.
				if (this.token !== null) {
					clearTimeout(this.timer);
					this.timer = setTimeout(() => void this.refresh(), 15_000);
				}
				return this.token !== null;
			}
			this.refused = false;
			this.accept((await response.json()) as TokenAnswer);
			return true;
		};
		return typeof navigator !== 'undefined' && navigator.locks
			? navigator.locks.request(lockName, run)
			: run();
	}

	/** Loads the signed-in user (names, permissions, first-login needs). */
	async loadUser(): Promise<CurrentUser | null> {
		if (!this.token) return null;
		const response = await fetch(apiUrl('/api/auth/me'), {
			headers: { Authorization: `Bearer ${this.token}`, Accept: 'application/json' }
		});
		if (!response.ok) return null;
		this.user = (await response.json()) as CurrentUser;
		return this.user;
	}

	/** Signs out: the server ends the session and clears the cookie; every tab forgets the token. */
	async signOut(): Promise<boolean> {
		let revoked = false;
		try {
			// The server must end the session (and clear the refresh cookie) even when this tab's access token has
			// expired meanwhile (a laptop waking up, a frozen tab): refresh first when it is missing or about to expire,
			// and once more when logout answers 401.
			if (this.token === null || this.expiresAt - Date.now() < 10_000) await this.refresh();
			for (let attempt = 0; attempt < 2 && this.token !== null && !revoked; attempt++) {
				this.refused = false;
				const response = await fetch(apiUrl('/api/auth/logout'), {
					method: 'POST',
					headers: { Authorization: `Bearer ${this.token}` },
					credentials: 'same-origin'
				}).catch(() => null);
				revoked = response?.ok === true;
				if (response?.status === 401 && attempt === 0) await this.refresh();
			}
			// A refused refresh means the server no longer knows the session: nothing is left to end.
			if (!revoked && this.token === null && this.refused) revoked = true;
		} finally {
			this.clear();
		}
		return revoked;
	}
}

export const auth = new AuthState();

/** The step-up dialog's request (ARV-051): a critical action asked for a fresh second factor. */
class StepUpState {
	open = $state(false);
	private waiting: ((confirmed: boolean) => void)[] = [];

	/** Opens the dialog; resolves true once the user confirmed with a code, false when they cancel. */
	request(): Promise<boolean> {
		this.open = true;
		return new Promise((resolve) => this.waiting.push(resolve));
	}

	finish(confirmed: boolean): void {
		this.open = false;
		const waiting = this.waiting;
		this.waiting = [];
		for (const resolve of waiting) resolve(confirmed);
	}
}

export const stepUp = new StepUpState();
