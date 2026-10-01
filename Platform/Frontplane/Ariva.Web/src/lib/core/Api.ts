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

async function readPayload(response: Response): Promise<unknown> {
	if (response.status === 204) {
		return null;
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

/**
 * Sends a request and always resolves to a Result: API results are passed through, other JSON bodies
 * are wrapped as data, and HTTP or network failures become error results instead of exceptions.
 */
export async function request<T>(
	method: HttpMethod,
	path: string,
	options: RequestOptions = {}
): Promise<Result<T>> {
	const headers: Record<string, string> = { Accept: 'application/json', ...options.headers };
	let body: string | undefined;

	if (options.body !== undefined) {
		headers['Content-Type'] = 'application/json';
		body = JSON.stringify(options.body);
	}

	let response: Response;
	try {
		response = await fetch(buildUrl(path, options), {
			method,
			headers,
			body,
			signal: options.signal
		});
	} catch (error) {
		return fail<T>(error instanceof Error ? error.message : 'Network request failed');
	}

	const payload = await readPayload(response);

	if (isResult(payload)) {
		const result = normalise(payload as Result<T>);
		if (!response.ok && !result.hasErrors) {
			return { ...result, hasErrors: true, errorMessages: [statusMessage(response)] };
		}
		return result;
	}

	if (!response.ok) {
		return fail<T>(statusMessage(response));
	}

	return ok(payload as T);
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
