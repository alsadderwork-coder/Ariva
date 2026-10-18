import type { Board, BoardAdvertising, BoardAdvertisingItem } from './displays';

export { cacheLimits, MediaCache, type ObjectUrls } from './media-cache';

/**
 * What the passenger display player decides about advertising (ARV-143b), kept apart from the component so that each rule
 * reads on its own: which plan is the same plan, when it has run out, when the wait rule pauses it, and the bounded cache of
 * the advertisements' bytes. Nothing here builds a URL from the board's text: an advertisement is named by its GUID only, and
 * its bytes become an object URL (img-src and media-src blob: in the CSP) that the cache revokes when it lets the item go.
 */

const guid = /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i;

/** The types the server serves; anything else never reaches an img or a video element. */
export const mediaTypes = [
	'image/png',
	'image/jpeg',
	'image/webp',
	'video/mp4',
	'video/webm'
] as const;

/** A cycle holds at most 400 places (40 items of weight 10). */
const maxPlaces = 400;

/** The slot the player gives an item: its own seconds, held to the board's 1 to 120. */
export function slotSeconds(item: BoardAdvertisingItem | null | undefined): number {
	const seconds = item?.seconds;
	return typeof seconds === 'number' && Number.isFinite(seconds)
		? Math.min(120, Math.max(1, seconds))
		: 10;
}

/** An advertisement the player may fetch and show: named by a GUID, of a type the server serves. */
export function isPlayableMedia(
	item: BoardAdvertisingItem | null | undefined
): item is BoardAdvertisingItem & { mediaId: string; contentType: string } {
	return (
		item?.kind === 'Media' &&
		typeof item.mediaId === 'string' &&
		guid.test(item.mediaId) &&
		(mediaTypes as readonly string[]).includes(item.contentType ?? '')
	);
}

export function isVideo(item: BoardAdvertisingItem | null | undefined): boolean {
	return typeof item?.contentType === 'string' && item.contentType.startsWith('video/');
}

/** Whether the bytes fetched are of the type the board named for the item (the media type's essence, without parameters). */
export function matchesType(blob: Blob, contentType: string | null): boolean {
	const essence = blob.type.split(';')[0].trim().toLowerCase();
	return essence !== '' && essence === (contentType ?? '').toLowerCase();
}

/**
 * The plan as the player plays it: its order held to the items it names (indexes that are whole numbers within the items),
 * at most one cycle of 400 places; null when nothing is left to play.
 */
export function playable(plan: BoardAdvertising | null | undefined): BoardAdvertising | null {
	if (!plan || !Array.isArray(plan.items) || !Array.isArray(plan.order)) return null;
	const count = plan.items.length;
	const order = plan.order
		.filter((index) => Number.isInteger(index) && index >= 0 && index < count)
		.slice(0, maxPlaces);
	return order.length > 0 ? { ...plan, order } : null;
}

/**
 * What makes a plan the same plan: the playlist, its items (kind, advertisement, type, slot, slide) and its order. A board
 * that brings the same plan keeps the player's place; any change starts it again at place 0. A slide's words are not part of
 * it: a change of words shows at once without a restart.
 */
export function planKey(plan: BoardAdvertising | null): string {
	if (!plan) return '';
	const items = plan.items.map((item) =>
		item.kind === 'Media'
			? `m:${item.mediaId ?? ''}:${item.contentType ?? ''}:${item.seconds}`
			: `s:${item.slide?.position ?? ''}:${item.slide?.icon ?? ''}:${item.seconds}`
	);
	return `${plan.playlistId}|${items.join(',')}|${plan.order.join(',')}`;
}

/** The advertisements a plan names, by id. */
export function mediaIds(plan: BoardAdvertising | null): Set<string> {
	const ids = new Set<string>();
	for (const item of plan?.items ?? []) if (isPlayableMedia(item)) ids.add(item.mediaId);
	return ids;
}

/**
 * An instant from the server as milliseconds. The server sends UTC with its zone; a text without one is read as UTC too, never
 * as the screen's local time. NaN when it is not a date.
 */
export function utcMilliseconds(text: string | null | undefined): number {
	if (typeof text !== 'string' || text.length === 0) return Number.NaN;
	return Date.parse(/(?:Z|[+-]\d{2}:?\d{2})$/i.test(text) ? text : `${text}Z`);
}

/** The server's clock now: the board's serverUtc plus the time since that board arrived (the screen's own clock may be wrong). */
export function serverNow(serverUtc: string, boardAt: number, now: number): number {
	const server = utcMilliseconds(serverUtc);
	return Number.isFinite(server) ? server + Math.max(0, now - boardAt) : now;
}

/** Whether the plan has run out by the server's clock: a player without a newer board stops it then. */
export function expired(plan: BoardAdvertising, serverNowMs: number): boolean {
	const until = utcMilliseconds(plan.untilUtc);
	return !Number.isFinite(until) || serverNowMs >= until;
}

/**
 * The wait rule, judged on each good board: advertising pauses when the nowcast of any entry with fresh data (a nowcast, and
 * data no older than the display's stale threshold) reaches the display's pause minutes, and resumes only when every fresh
 * entry is below them by the display's hysteresis. Between the two, and with no fresh entry, the state holds as it was. A
 * display without the rule never pauses; a board without a plan keeps the state for the next plan.
 */
export function nextPause(paused: boolean, board: Board): boolean {
	const plan = board.advertising;
	if (!plan) return paused;
	const limit = plan.pauseMinutes;
	if (typeof limit !== 'number' || !Number.isFinite(limit)) return false;
	const fresh = board.entries
		.filter(
			(entry) =>
				typeof entry.nowcastMinutes === 'number' &&
				typeof entry.ageSeconds === 'number' &&
				entry.ageSeconds <= board.staleSeconds
		)
		.map((entry) => entry.nowcastMinutes as number);
	if (fresh.length === 0) return paused;
	if (fresh.some((minutes) => minutes >= limit)) return true;
	const hysteresis = Number.isFinite(board.hysteresisMinutes)
		? Math.max(0, board.hysteresisMinutes)
		: 0;
	if (fresh.every((minutes) => minutes < limit - hysteresis)) return false;
	return paused;
}
