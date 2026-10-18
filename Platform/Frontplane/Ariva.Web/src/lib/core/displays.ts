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

/**
 * A display's layout (ARV-140): the full queue view, or a split screen whose queue region comes first in the reading order
 * and keeps its share (50 to 75 percent); the second region shows instructions, or advertising where the site's advertising
 * policy allows it for every zone the display shows (ARV-145; the server checks it, and the board falls back to instructions).
 */
export const layouts = ['Full', 'Split'] as const;
export const secondRegions = ['Instructions', 'Advertising'] as const;
export const queueShare = { min: 50, max: 75, default: 60 } as const;

/**
 * ARV-143b: the wait, in minutes, at which a display's advertising gives way to instructions (with the display's hysteresis);
 * none means never. The server applies the same range.
 */
export const advertisingPause = { min: 1, max: 240 } as const;

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
	layout: 'Full' | 'Split' | string;
	queueShare: number;
	secondRegion: 'Instructions' | 'Advertising' | string;
	/** ARV-143b: the wait at which advertising gives way to instructions, or null for never. */
	advertisingPauseMinutes: number | null;
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
	layout: string;
	queueShare: number;
	secondRegion: string;
	/** ARV-143b: 1 to 240, or null for never. */
	advertisingPauseMinutes: number | null;
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
	/** The display's site is an illustrative demo site (ARV-139a): the board shows the "Illustrative, not surveyed" note. */
	illustrative?: boolean;
	/** ARV-140: Full or Split; a board from a server before ARV-140 has none and keeps the full view. */
	layout?: string;
	queueShare?: number;
	secondRegion?: string;
	/** ARV-141: the slide shown under the waits, if one applies to the display. */
	pinnedSlide?: BoardSlide | null;
	/** ARV-141: the slides the second region rotates, in the site's order; none means the default instructions. */
	slides?: BoardSlide[];
	/**
	 * ARV-143b: the playlist the second region plays now, only while the display is split, set to advertising, the site's
	 * policy allows it, a schedule applies and at least one item may play; otherwise null (instructions).
	 */
	advertising?: BoardAdvertising | null;
}

/** One item a display plays (ARV-143b): an advertisement fetched by its id, or an instruction slide. */
export interface BoardAdvertisingItem {
	kind: 'Media' | 'Slide' | string;
	/** An advertisement's id (a GUID), fetched with the display's credential; null for a slide. */
	mediaId: string | null;
	/** The advertisement's type: image/png, image/jpeg, image/webp, video/mp4 or video/webm; null for a slide. */
	contentType: string | null;
	/** The item's slot, 1 to 120 seconds: the player ends it then, whatever its media holds. */
	seconds: number;
	/** A slide's icon and words in the display's languages; null for an advertisement. */
	slide: BoardSlide | null;
}

/** The plan a display plays now (ARV-143b). */
export interface BoardAdvertising {
	playlistId: string;
	/** When the plan next changes (at most the site's next local midnight): a player without a newer board stops it then. */
	untilUtc: string;
	/** The display's wait rule: advertising gives way to instructions at this wait (with the hysteresis); null for none. */
	pauseMinutes: number | null;
	items: BoardAdvertisingItem[];
	/** One cycle: indexes into items, each item as often as its weight, already spread (weights 2 and 1 give [0, 1, 0]). */
	order: number[];
}

/** An instruction slide as the player gets it (ARV-141): its icon's name, its time on screen, its words per display language. */
export interface BoardSlide {
	position: number;
	icon: string;
	durationSeconds: number;
	texts: { language: string; title: string; body: string | null }[];
}

/** The default instructions of the second region, per language (static/board/instructions.json; ARV-141 makes them configurable). */
export interface Instructions {
	title: string;
	steps: string[];
}

/**
 * The second region's instructions, fetched on their own (a constant path of this web app, never a URL from the page), so
 * that the second region failing to load never touches the queue region. Null when they cannot be read.
 */
export async function instructions(): Promise<Record<string, Instructions> | null> {
	try {
		const response = await fetch('/board/instructions.json', {
			headers: { Accept: 'application/json' },
			credentials: 'omit'
		});
		if (!response.ok) return null;
		const body = (await response.json()) as unknown;
		if (!body || typeof body !== 'object' || Array.isArray(body)) return null;
		// Only the board's own languages are read, so no other key (such as __proto__) is ever assigned.
		const result: Record<string, Instructions> = {};
		for (const [language, value] of Object.entries(body as Record<string, unknown>)) {
			if (!(boardLanguages as readonly string[]).includes(language)) continue;
			const entry = value as { title?: unknown; steps?: unknown };
			if (
				typeof entry?.title === 'string' &&
				Array.isArray(entry.steps) &&
				entry.steps.every((step) => typeof step === 'string')
			)
				result[language] = { title: entry.title, steps: entry.steps as string[] };
		}
		// Nothing usable is a failure to load, so the region shows its quiet empty panel.
		return Object.keys(result).length > 0 ? result : null;
	} catch {
		return null;
	}
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

/** What the player learns from one advertisement request: its bytes, or that it cannot play now. */
export type AdvertisementAnswer = { blob: Blob } | { failed: true };

/** The most an advertisement may weigh as the player reads it (the server takes videos of at most 100 MB). */
const maxAdvertisementBytes = 160 * 1024 * 1024;

/**
 * One advertisement as the player fetches it (ARV-143b): by the GUID its board gives, with the display's code in the query
 * and its credential in the header, no user token and no cookie; the path is this API's own, never a URL from the board.
 * The server answers 404 when the item may not play now, the site's policy is off or the display is not set to advertising;
 * anything but a 200 is a failure, and the player shows instructions for that item's slot.
 */
export async function advertisement(
	code: string,
	credential: string,
	mediaId: string,
	signal?: AbortSignal
): Promise<AdvertisementAnswer> {
	if (!codePattern.test(code) || !guid.test(mediaId) || !credential) return { failed: true };
	try {
		const response = await fetch(
			`${Endpoints.main.baseUrl}/api/v1/display/advertising/${mediaId}?code=${encodeURIComponent(code)}`,
			{
				headers: { 'X-Ariva-Display-Key': credential },
				credentials: 'omit',
				cache: 'no-store',
				signal
			}
		);
		const length = Number(response.headers.get('content-length') ?? '0');
		if (response.status !== 200 || length > maxAdvertisementBytes) {
			await response.body?.cancel().catch(() => undefined);
			return { failed: true };
		}
		const blob = await response.blob();
		return blob.size > 0 && blob.size <= maxAdvertisementBytes ? { blob } : { failed: true };
	} catch {
		return { failed: true };
	}
}
