import { Api, fail, type Result } from './Api';

/**
 * The instruction slides API (ARV-141): short multilingual plain-text slides per site for the passenger displays. Seeing
 * them needs DisplayContent.View, changing them DisplayContent.Manage; the server checks every word (bounded, no control
 * or invisible characters) and the screens show them as text only. Players get them through their board, never here.
 */

const base = '/api/v1/admin/display-slides';
const guid = /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i;

export const scopes = ['Site', 'Zone', 'Display'] as const;

/** The built-in icons, in the order the form lists them (the player draws each from its own set; nothing is uploaded). */
export const icons = [
	'Information',
	'Passport',
	'EGate',
	'HatAndGlasses',
	'Fingerprint',
	'Family',
	'Accessibility',
	'Baggage',
	'NoPhone',
	'FollowSigns'
] as const;

export const limits = { title: 80, body: 240, targets: 20, minSeconds: 5, maxSeconds: 60 } as const;

export interface SlideText {
	title: string;
	body: string | null;
}

export interface Slide {
	id: string;
	siteCode: string;
	position: number;
	scope: 'Site' | 'Zone' | 'Display' | string;
	targets: string[];
	icon: string;
	durationSeconds: number;
	pinned: boolean;
	status: 'Active' | 'Retired' | string;
	texts: Record<string, SlideText>;
	createdOn: string | null;
	modifiedOn: string | null;
}

export interface SlideRequest {
	siteCode: string;
	scope: string;
	targets: string[];
	icon: string;
	durationSeconds: number;
	pinned: boolean;
	texts: Record<string, SlideText>;
}

const refused = <T>() => Promise.resolve(fail<T>('Refused: not an id.'));

export function search(siteCode: string): Promise<Result<Slide[]>> {
	return Api.get<Slide[]>(base, { query: { siteCode } });
}

export function create(request: SlideRequest): Promise<Result<Slide>> {
	return Api.post<Slide>(base, request);
}

export function update(id: string, request: SlideRequest): Promise<Result<Slide>> {
	return guid.test(id) ? Api.put<Slide>(`${base}/${id}`, request) : refused<Slide>();
}

export function retire(id: string): Promise<Result<Slide>> {
	return guid.test(id) ? Api.post<Slide>(`${base}/${id}/retire`) : refused<Slide>();
}

export function restore(id: string): Promise<Result<Slide>> {
	return guid.test(id) ? Api.post<Slide>(`${base}/${id}/restore`) : refused<Slide>();
}

/** The site's slides in a new order: every slide of the site, each once. */
export function reorder(siteCode: string, ids: string[]): Promise<Result<Slide[]>> {
	return ids.every((id) => guid.test(id))
		? Api.put<Slide[]>(`${base}/order`, { ids }, { query: { siteCode } })
		: refused<Slide[]>();
}
