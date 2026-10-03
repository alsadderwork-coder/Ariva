import { Api, fail, type Result } from './Api';
import { Endpoints } from './Endpoints';

/**
 * The passenger display API (ARV-058). Administration (settings, a new player credential, delete) goes through the
 * signed-in user's token like every other admin call; creating a display and issuing a new credential are critical
 * actions (step-up within 15 minutes, which Api.ts answers with the step-up dialog). The board is fetched by the player
 * with the display's own credential only, never with a user token.
 */

const base = '/api/v1/admin/displays';
const guid = /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i;
export const codePattern = /^[A-Z0-9]+(-[A-Z0-9]+)*$/;

/** The languages a board has resource files for (lib/i18n/board). */
export const boardLanguages = ['en', 'ar', 'pt', 'sw'] as const;
export type BoardLanguage = (typeof boardLanguages)[number];

export const limits = { entries: 12, label: 80, message: 200, name: 200 } as const;

export interface DisplayEntry {
	zone: string;
	labels: Record<string, string>;
}

export interface Display {
	id: string;
	siteCode: string;
	code: string;
	name: string;
	location: string | null;
	orientation: 'Landscape' | 'Portrait' | string;
	languages: string[];
	bandMinutes: number;
	hysteresisMinutes: number;
	staleSeconds: number;
	entries: DisplayEntry[];
	fallback: Record<string, string>;
	enabled: boolean;
	credentialPrefix: string | null;
	credentialIssuedOn: string | null;
	createdOn: string | null;
	modifiedOn: string | null;
}

export interface DisplayRequest {
	siteCode: string;
	code: string;
	name: string;
	location: string | null;
	orientation: string;
	languages: string[];
	bandMinutes: number;
	hysteresisMinutes: number;
	staleSeconds: number;
	entries: DisplayEntry[];
	fallback: Record<string, string>;
	enabled: boolean;
}

export interface Issued {
	display: Display;
	credential: string;
}

export function search(siteCode: string): Promise<Result<Display[]>> {
	return Api.get<Display[]>(base, { query: { siteCode } });
}

export function create(request: DisplayRequest): Promise<Result<Issued>> {
	return Api.post<Issued>(base, request);
}

export function update(id: string, request: DisplayRequest): Promise<Result<Display>> {
	return guid.test(id)
		? Api.put<Display>(`${base}/${id}`, request)
		: Promise.resolve(fail<Display>('Refused: not an id.'));
}

export function newCredential(id: string): Promise<Result<Issued>> {
	return guid.test(id)
		? Api.post<Issued>(`${base}/${id}/credential`)
		: Promise.resolve(fail<Issued>('Refused: not an id.'));
}

export function remove(id: string): Promise<Result<unknown>> {
	return guid.test(id)
		? Api.delete<unknown>(`${base}/${id}`)
		: Promise.resolve(fail<unknown>('Refused: not an id.'));
}

/** The player's address: the display's code in the query, its credential in the fragment (never sent to a server). */
export function playerUrl(code: string, credential: string): string {
	return `${location.origin}/display?code=${encodeURIComponent(code)}#key=${encodeURIComponent(credential)}`;
}

export interface BoardEntry {
	labels: Record<string, string>;
	nowcastMinutes: number | null;
	degraded: boolean;
	noService: string | null;
	ageSeconds: number | null;
}

export interface Board {
	code: string;
	name: string;
	orientation: string;
	languages: string[];
	bandMinutes: number;
	hysteresisMinutes: number;
	staleSeconds: number;
	fallback: Record<string, string>;
	entries: BoardEntry[];
	serverUtc: string;
}

/** What the player learns from one board request: the board, or that its credential was refused, or nothing new. */
export type BoardAnswer = { board: Board } | { refused: true } | { failed: true };

/**
 * The board as the player fetches it: the display's own credential in its header, no user token, no cookie. A 401 is a
 * refused credential (or a deleted or disabled display); anything else that fails leaves the last board standing until
 * it goes stale.
 */
export async function board(code: string, credential: string): Promise<BoardAnswer> {
	if (!codePattern.test(code)) return { refused: true };
	try {
		const response = await fetch(
			`${Endpoints.main.baseUrl}/api/v1/display/board?code=${encodeURIComponent(code)}`,
			{
				headers: { Accept: 'application/json', 'X-Ariva-Display-Key': credential },
				credentials: 'omit',
				cache: 'no-store'
			}
		);
		if (response.status === 401) return { refused: true };
		if (!response.ok) return { failed: true };
		return { board: (await response.json()) as Board };
	} catch {
		return { failed: true };
	}
}
