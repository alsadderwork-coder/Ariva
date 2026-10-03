import { auth, readProblem, stepUp } from './auth.svelte';
import { Endpoints } from './Endpoints';

/** Result envelope returned by Ariva APIs; the same shape as AMAN's Result. */
export interface Result<T> {
	hasErrors: boolean;
	errorMessages: string[];
	warningMessages: string[];
	infoMessages: string[];
	data: T | null;
}

export type HttpMethod = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';

export type QueryValue = string | number | boolean | null | undefined;

export interface RequestOptions {
	/** Host base URL; defaults to Ariva.Api.Main. */
	baseUrl?: string;
	query?: Record<string, QueryValue>;
	body?: unknown;
	headers?: Record<string, string>;
	signal?: AbortSignal;
	/** True for a call that must not carry the signed-in user's token (a call to another host, for example). */
	anonymous?: boolean;
	/** 'blob' reads a successful answer as a Blob (an image); errors are still read as problems. */
	responseType?: 'json' | 'blob';
}

/** Builds a successful result around data. */
export function ok<T>(data: T, infoMessages: string[] = []): Result<T> {
	return { hasErrors: false, errorMessages: [], warningMessages: [], infoMessages, data };
}

/** Builds a failed result with one or more error messages. */
export function fail<T>(...errorMessages: string[]): Result<T> {
	return { hasErrors: true, errorMessages, warningMessages: [], infoMessages: [], data: null };
}

function isResult(value: unknown): value is Result<unknown> {
	return (
		typeof value === 'object' &&
		value !== null &&
		typeof (value as { hasErrors?: unknown }).hasErrors === 'boolean'
	);
}

function normalise<T>(value: Result<T>): Result<T> {
	return {
		hasErrors: value.hasErrors,
		errorMessages: value.errorMessages ?? [],
		warningMessages: value.warningMessages ?? [],
		infoMessages: value.infoMessages ?? [],
		data: value.data ?? null
	};
}

function buildUrl(path: string, options: RequestOptions): string {
	const base = (options.baseUrl ?? Endpoints.main.baseUrl).replace(/\/+$/, '');
	const url = `${base}/${path.replace(/^\/+/, '')}`;

	const params = new URLSearchParams();
	for (const [key, value] of Object.entries(options.query ?? {})) {
		if (value !== undefined && value !== null) {
			params.append(key, String(value));
		}
	}

	const query = params.toString();
	return query ? `${url}?${query}` : url;
}

async function readPayload(
	response: Response,
	responseType: 'json' | 'blob' = 'json'
): Promise<unknown> {
	if (response.status === 204) {
		return null;
	}
	if (responseType === 'blob' && response.ok) {
		return await response.blob();
	}

	const contentType = response.headers.get('content-type') ?? '';
	if (contentType.includes('json')) {
		try {
			return await response.json();
		} catch {
			return null;
		}
	}

	const text = await response.text();
	return text.length > 0 ? text : null;
}

function statusMessage(response: Response): string {
	return `HTTP ${response.status} ${response.statusText}`.trim();
}

/** The origin a URL resolves to from this page (relative URLs are this page's origin). */
function originOf(url: string): string | null {
	try {
		return new URL(url, typeof location === 'undefined' ? 'http://localhost' : location.href)
			.origin;
	} catch {
		return null;
	}
}

/** The page to return to after signing in again: where the user is now. */
function currentPage(): string {
	return typeof location === 'undefined' ? '/' : `${location.pathname}${location.search}`;
}

/**
 * What to do with a 401 from Ariva.Api.Main (ARV-051): a critical action asked for a fresh second factor (RFC 9470,
 * error mfa_required) opens the step-up dialog; an ended session (session_expired) goes back to sign-in with the page
 * remembered; an expired access token is refreshed. True when the call should be sent once more.
 */
async function recover(response: Response): Promise<boolean> {
	const problem = await readProblem(response.clone());
	const challenge = response.headers.get('www-authenticate') ?? '';
	if (problem.error === 'mfa_required' && challenge.includes('insufficient_user_authentication')) {
		return stepUp.request();
	}
	if (problem.error !== 'session_expired' && (await auth.refresh())) {
		return true;
	}
	await auth.sessionEnded(currentPage());
	return false;
}

/**
 * Sends a request and always resolves to a Result: API results are passed through, other JSON bodies
 * are wrapped as data, and HTTP or network failures become error results instead of exceptions. A call to
 * Ariva.Api.Main carries the signed-in user's access token (from memory, never storage) and is sent once more after
 * a step-up or a refresh.
 */
export async function request<T>(
	method: HttpMethod,
	path: string,
	options: RequestOptions = {}
): Promise<Result<T>> {
	// A path is relative to its host: a backslash, a control character (the URL parser drops tabs and newlines) or a leading // could make the URL parser leave the host (CWE-918).
	if (
		path.includes('\\') ||
		/[\u0000-\u001f\u007f]/.test(path) ||
		/^\s*[/\\]{2}/.test(path) ||
		/^[a-z][a-z0-9+.-]*:/i.test(path.trim())
	) {
		return fail<T>('Refused: the path is not relative to the host.');
	}
	// The access token goes only to Ariva.Api.Main's own origin, decided on the resolved URL, not on the base string.
	const withToken =
		!options.anonymous &&
		originOf(buildUrl(path, options)) === originOf(Endpoints.main.baseUrl || '/');
	let response: Response | null = null;

	for (let attempt = 0; attempt < 2; attempt++) {
		const headers: Record<string, string> = { Accept: 'application/json', ...options.headers };
		let body: string | FormData | undefined;

		if (options.body instanceof FormData) {
			// Multipart (a file upload): the browser sets the content type with its boundary.
			body = options.body;
		} else if (options.body !== undefined) {
			headers['Content-Type'] = 'application/json';
			body = JSON.stringify(options.body);
		}
		if (withToken && auth.token) {
			headers.Authorization = `Bearer ${auth.token}`;
		}

		try {
			response = await fetch(buildUrl(path, options), {
				method,
				headers,
				body,
				signal: options.signal,
				credentials: 'same-origin'
			});
		} catch (error) {
			return fail<T>(error instanceof Error ? error.message : 'Network request failed');
		}

		if (!(withToken && response.status === 401 && attempt === 0 && (await recover(response)))) {
			break;
		}
	}

	const answer = response!;
	const payload = await readPayload(answer, options.responseType);

	if (isResult(payload)) {
		const result = normalise(payload as Result<T>);
		if (!answer.ok && !result.hasErrors) {
			return { ...result, hasErrors: true, errorMessages: [statusMessage(answer)] };
		}
		return result;
	}

	if (!answer.ok) {
		return fail<T>(problemMessage(payload) ?? statusMessage(answer));
	}

	return ok(payload as T);
}

/** The title and detail of an RFC 9457 problem body, as Ariva wrote them (shown as text, never as markup). */
function problemMessage(payload: unknown): string | null {
	if (typeof payload !== 'object' || payload === null) return null;
	const { title, detail } = payload as { title?: unknown; detail?: unknown };
	const parts = [title, detail].filter(
		(part): part is string => typeof part === 'string' && part.length > 0
	);
	return parts.length ? parts.join(': ') : null;
}

export const Api = {
	get: <T>(path: string, options?: RequestOptions) => request<T>('GET', path, options),
	post: <T>(path: string, body?: unknown, options?: RequestOptions) =>
		request<T>('POST', path, { ...options, body }),
	put: <T>(path: string, body?: unknown, options?: RequestOptions) =>
		request<T>('PUT', path, { ...options, body }),
	patch: <T>(path: string, body?: unknown, options?: RequestOptions) =>
		request<T>('PATCH', path, { ...options, body }),
	delete: <T>(path: string, options?: RequestOptions) => request<T>('DELETE', path, options)
};
