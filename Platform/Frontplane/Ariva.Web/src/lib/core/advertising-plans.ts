import { Api, fail, type Result } from './Api';
import type { MediaStatus } from './advertising-media';

/**
 * A site's advertising plans (ARV-143a): playlists of advertisements from the site's library and instruction slides of
 * the site, each item with its time on screen and its weight; schedules that play a playlist on the site's displays (all
 * of them, those showing given queue zones, or named ones) on days of the week within a window of the site's local time;
 * and one display's week as the board will resolve it. Every call answers 403 while the site's advertising policy is off
 * and 404 for a site outside the caller's sites. Reading needs Advertising.View or Advertising.Manage, changing
 * Advertising.Manage; the server decides every one of these. Site codes, ids, display codes and times are checked here
 * before a request leaves, and each path part is encoded; nothing here ever builds a URL from an item's text.
 */

const site = /^[A-Z0-9]{2,8}(-[A-Z0-9]{1,8})?$/;
const guid = /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i;
const displayCode = /^[A-Z0-9]+(-[A-Z0-9]+)*$/;
const opening = /^(?:[01]\d|2[0-3]):[0-5]\d$/;
const closing = /^(?:(?:[01]\d|2[0-3]):[0-5]\d|24:00)$/;

export const limits = {
	maxName: 120,
	maxItems: 40,
	minSeconds: 5,
	maxSeconds: 60,
	defaultSeconds: 10,
	minWeight: 1,
	maxWeight: 10,
	maxTargets: 20,
	maxDisplayCode: 40
} as const;

/** The days of the week as the API names them, Monday first. */
export const weekDays = [
	'Monday',
	'Tuesday',
	'Wednesday',
	'Thursday',
	'Friday',
	'Saturday',
	'Sunday'
] as const;
export type WeekDay = (typeof weekDays)[number];

export const scopes = ['Site', 'Zone', 'Display'] as const;
export type ScheduleScope = (typeof scopes)[number];

export type PlanStatus = 'Active' | 'Retired';
export type ItemKind = 'Media' | 'Slide';

/** Whether an item's type (an advertisement's, as the library or a playlist names it) is a video: its slot is its length. */
export function isVideo(contentType: string | null | undefined): boolean {
	return (contentType ?? '').startsWith('video/');
}

export interface PlaylistItem {
	kind: ItemKind;
	id: string;
	/** Its time on screen; a video's is its own length, set by the server. */
	seconds: number;
	weight: number;
	/** The advertiser and campaign of an advertisement, or a slide's title (text only); null when it cannot be read. */
	label: string | null;
	/** Whether it would play on the site's local day now: an approved advertisement within its dates, an active slide. */
	playable: boolean;
	mediaStatus: MediaStatus | null;
	contentType: string | null;
}

export interface Playlist {
	id: string;
	siteCode: string;
	name: string;
	status: PlanStatus;
	items: PlaylistItem[];
	/** The length of one pass, in seconds. */
	totalSeconds: number;
	createdOn: string | null;
	modifiedOn: string | null;
}

export interface PlaylistItemRequest {
	kind: ItemKind;
	id: string;
	/** Left out for a video, whose slot is its own length. */
	seconds?: number;
	weight?: number;
}

export interface PlaylistRequest {
	name: string;
	items: PlaylistItemRequest[];
}

export interface Schedule {
	id: string;
	siteCode: string;
	playlistId: string;
	playlistName: string;
	/** False once its playlist is retired: the schedule then applies nowhere until the playlist is restored. */
	playlistActive: boolean;
	scope: ScheduleScope;
	targets: string[];
	/** English day names, Monday first. */
	days: WeekDay[];
	/** "HH:MM" of the site's local day. */
	opens: string;
	/** "HH:MM" or "24:00"; at or before the opening time it is on the next day. */
	closes: string;
	status: PlanStatus;
	createdOn: string | null;
	modifiedOn: string | null;
}

export interface ScheduleRequest {
	playlistId: string;
	scope: ScheduleScope;
	targets: string[];
	days: WeekDay[];
	opens: string;
	closes: string;
}

export interface TimelineSegment {
	fromUtc: string;
	toUtc: string;
	/** The site's local wall-clock time, "yyyy-MM-ddTHH:mm". */
	fromLocal: string;
	toLocal: string;
	scheduleId: string | null;
	playlistId: string | null;
	/** Null while no schedule applies: the display shows its instructions. */
	playlistName: string | null;
}

export interface Timeline {
	siteCode: string;
	displayCode: string;
	/** The site's time zone (an IANA name). */
	timeZone: string;
	/** The display's second region: one set to Instructions plays no advertising whatever the schedules say. */
	secondRegion: 'Advertising' | 'Instructions';
	fromUtc: string;
	toUtc: string;
	segments: TimelineSegment[];
}

const root = (siteCode: string) =>
	`/api/v1/admin/sites/${encodeURIComponent(siteCode)}/advertising`;

const path = (
	siteCode: string,
	kind: 'playlists' | 'schedules',
	id?: string,
	action?: 'retire' | 'restore'
) =>
	`${root(siteCode)}/${kind}${id ? `/${encodeURIComponent(id)}` : ''}${action ? `/${action}` : ''}`;

function checked<T>(
	siteCode: string,
	id: string | null,
	call: () => Promise<Result<T>>
): Promise<Result<T>> {
	if (!site.test(siteCode)) return Promise.resolve(fail<T>('Refused: not a site.'));
	if (id !== null && !guid.test(id)) return Promise.resolve(fail<T>('Refused: not an id.'));
	return call();
}

function itemsRefused(request: PlaylistRequest): string | null {
	if (!Array.isArray(request.items) || request.items.length > limits.maxItems)
		return 'Refused: too many items.';
	for (const item of request.items) {
		if (item.kind !== 'Media' && item.kind !== 'Slide') return 'Refused: not an item kind.';
		if (!guid.test(item.id)) return 'Refused: not an id.';
		if (item.seconds !== undefined && !Number.isInteger(item.seconds))
			return 'Refused: not a number of seconds.';
		if (item.weight !== undefined && !Number.isInteger(item.weight))
			return 'Refused: not a weight.';
	}
	return null;
}

function scheduleRefused(request: ScheduleRequest): string | null {
	if (!guid.test(request.playlistId)) return 'Refused: not an id.';
	if (!(scopes as readonly string[]).includes(request.scope)) return 'Refused: not a scope.';
	if (!request.days.every((d) => (weekDays as readonly string[]).includes(d)))
		return 'Refused: not a day.';
	if (!opening.test(request.opens) || !closing.test(request.closes)) return 'Refused: not a time.';
	return null;
}

export function listPlaylists(siteCode: string): Promise<Result<Playlist[]>> {
	return checked(siteCode, null, () => Api.get<Playlist[]>(path(siteCode, 'playlists')));
}

export function createPlaylist(
	siteCode: string,
	request: PlaylistRequest
): Promise<Result<Playlist>> {
	const refused = itemsRefused(request);
	if (refused) return Promise.resolve(fail<Playlist>(refused));
	return checked(siteCode, null, () => Api.post<Playlist>(path(siteCode, 'playlists'), request));
}

export function updatePlaylist(
	siteCode: string,
	id: string,
	request: PlaylistRequest
): Promise<Result<Playlist>> {
	const refused = itemsRefused(request);
	if (refused) return Promise.resolve(fail<Playlist>(refused));
	return checked(siteCode, id, () => Api.put<Playlist>(path(siteCode, 'playlists', id), request));
}

export function retirePlaylist(siteCode: string, id: string): Promise<Result<Playlist>> {
	return checked(siteCode, id, () => Api.post<Playlist>(path(siteCode, 'playlists', id, 'retire')));
}

export function restorePlaylist(siteCode: string, id: string): Promise<Result<Playlist>> {
	return checked(siteCode, id, () =>
		Api.post<Playlist>(path(siteCode, 'playlists', id, 'restore'))
	);
}

export function listSchedules(siteCode: string): Promise<Result<Schedule[]>> {
	return checked(siteCode, null, () => Api.get<Schedule[]>(path(siteCode, 'schedules')));
}

export function createSchedule(
	siteCode: string,
	request: ScheduleRequest
): Promise<Result<Schedule>> {
	const refused = scheduleRefused(request);
	if (refused) return Promise.resolve(fail<Schedule>(refused));
	return checked(siteCode, null, () => Api.post<Schedule>(path(siteCode, 'schedules'), request));
}

export function updateSchedule(
	siteCode: string,
	id: string,
	request: ScheduleRequest
): Promise<Result<Schedule>> {
	const refused = scheduleRefused(request);
	if (refused) return Promise.resolve(fail<Schedule>(refused));
	return checked(siteCode, id, () => Api.put<Schedule>(path(siteCode, 'schedules', id), request));
}

export function retireSchedule(siteCode: string, id: string): Promise<Result<Schedule>> {
	return checked(siteCode, id, () => Api.post<Schedule>(path(siteCode, 'schedules', id, 'retire')));
}

export function restoreSchedule(siteCode: string, id: string): Promise<Result<Schedule>> {
	return checked(siteCode, id, () =>
		Api.post<Schedule>(path(siteCode, 'schedules', id, 'restore'))
	);
}

/** What one display of the site plays over the week from the site's local midnight today. */
export function timeline(siteCode: string, display: string): Promise<Result<Timeline>> {
	if (display.length > limits.maxDisplayCode || !displayCode.test(display))
		return Promise.resolve(fail<Timeline>('Refused: not a display code.'));
	return checked(siteCode, null, () =>
		Api.get<Timeline>(`${root(siteCode)}/timeline`, { query: { display } })
	);
}

/** The targets typed as a comma separated list: trimmed, empty ones dropped, each named once. */
export function parseTargets(text: string): string[] {
	const seen = new Set<string>();
	for (const part of text.split(',')) {
		const target = part.trim();
		if (target.length > 0) seen.add(target);
	}
	return [...seen];
}

/** Minutes since an arbitrary epoch of a local wall-clock time "yyyy-MM-ddTHH:mm" (or a day "yyyy-MM-dd"), or NaN. */
export function localMinutes(local: string): number {
	const match = /^(\d{4})-(\d{2})-(\d{2})(?:T(\d{2}):(\d{2}))?$/.exec(local);
	if (!match) return Number.NaN;
	const [, year, month, day, hours, minutes] = match;
	return (
		Date.UTC(
			Number(year),
			Number(month) - 1,
			Number(day),
			Number(hours ?? 0),
			Number(minutes ?? 0)
		) / 60_000
	);
}

export interface DaySegment {
	/** Where it starts and how long it is, in percent of the day. */
	start: number;
	width: number;
	/** "HH:MM" of the local day ("24:00" at its end). */
	from: string;
	to: string;
	playlistName: string | null;
}

export interface TimelineDay {
	/** The local day, "yyyy-MM-dd". */
	day: string;
	weekDay: WeekDay;
	segments: DaySegment[];
}

const minutesPerDay = 1_440;

function clock(minuteOfDay: number): string {
	if (minuteOfDay >= minutesPerDay) return '24:00';
	return `${String(Math.floor(minuteOfDay / 60)).padStart(2, '0')}:${String(minuteOfDay % 60).padStart(2, '0')}`;
}

/**
 * The week as rows of local days, from the first segment's local day: each segment cut at the days' midnights and placed
 * by its local start and end, in percent of the day.
 */
export function timelineDays(segments: TimelineSegment[], count = 7): TimelineDay[] {
	const first = segments[0]?.fromLocal.slice(0, 10) ?? '';
	const origin = localMinutes(first);
	if (!Number.isFinite(origin)) return [];
	const spans = segments
		.map((s) => ({
			from: localMinutes(s.fromLocal),
			to: localMinutes(s.toLocal),
			playlistName: s.playlistName
		}))
		.filter((s) => Number.isFinite(s.from) && Number.isFinite(s.to) && s.to > s.from);
	const days: TimelineDay[] = [];
	for (let index = 0; index < count; index++) {
		const start = origin + index * minutesPerDay;
		const end = start + minutesPerDay;
		const date = new Date(start * 60_000);
		const day = date.toISOString().slice(0, 10);
		// getUTCDay counts from Sunday; the week here starts on Monday.
		const weekDay = weekDays[(date.getUTCDay() + 6) % 7];
		const parts: DaySegment[] = [];
		for (const span of spans) {
			const from = Math.max(span.from, start);
			const to = Math.min(span.to, end);
			if (to <= from) continue;
			parts.push({
				start: ((from - start) / minutesPerDay) * 100,
				width: ((to - from) / minutesPerDay) * 100,
				from: clock(from - start),
				to: clock(to - start),
				playlistName: span.playlistName
			});
		}
		days.push({ day, weekDay, segments: parts });
	}
	return days;
}
