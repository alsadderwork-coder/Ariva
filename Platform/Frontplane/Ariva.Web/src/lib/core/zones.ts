import { Api, fail, type Result } from './Api';

/**
 * The zone profile and floor plan admin API (ARV-017, ARV-018) as the zones screen uses it (ARV-053). Every call is
 * limited to the caller's sites by the server; publishing is a critical action (step-up within 15 minutes), which
 * Api.ts answers with the step-up dialog.
 */

export const zoneKinds = ['Queue', 'Service', 'Staff', 'Overflow'] as const;
export type ZoneKind = (typeof zoneKinds)[number];
export const lineRoles = ['Entry', 'Exit', 'Count', 'OverflowEntry'] as const;
export type LineRole = (typeof lineRoles)[number];

export interface ProfileSummary {
	id: string;
	siteCode: string;
	name: string;
	status: 'Draft' | 'Published' | 'Retired';
	version: number | null;
	basedOnVersion: number | null;
	geometryHash: string | null;
	createdBy: string | null;
	createdOn: string | null;
	publishedBy: string | null;
	publishedOn: string | null;
	retiredOn: string | null;
	zoneCount: number;
	lineCount: number;
}

export interface Zone {
	id: string;
	name: string;
	kind: ZoneKind;
	levelId: string;
	queueZoneId: string | null;
	deskId: string | null;
	polygon: string;
	areaSquareMetres: number;
}

export interface Line {
	id: string;
	name: string;
	role: LineRole;
	zoneId: string | null;
	levelId: string;
	startX: number;
	startY: number;
	endX: number;
	endY: number;
	lengthMetres: number;
}

export interface Profile {
	profile: ProfileSummary;
	zones: Zone[];
	lines: Line[];
}

export interface Validation {
	publishable: boolean;
	problems: string[];
	geometryHash: string;
}

export interface FloorPlan {
	id: string;
	levelId: string;
	contentType: string;
	sizeBytes: number;
	widthPixels: number | null;
	heightPixels: number | null;
	metresPerPixel: number;
	originX: number;
	originY: number;
}

export interface Point {
	x: number;
	y: number;
}

/** "x y,x y,..." (metres, invariant culture) to points; null when the text is not a ring. */
export function parsePolygon(text: string): Point[] | null {
	const points: Point[] = [];
	for (const part of text.split(',')) {
		const [x, y, extra] = part.trim().split(/\s+/);
		const px = Number(x);
		const py = Number(y);
		if (extra !== undefined || !Number.isFinite(px) || !Number.isFinite(py)) return null;
		points.push({ x: px, y: py });
	}
	return points.length >= 3 ? points : null;
}

/** Points to the server's polygon text, at millimetre precision. */
export function formatPolygon(points: Point[]): string {
	return points.map((p) => `${round(p.x)} ${round(p.y)}`).join(',');
}

export function round(value: number): number {
	return Math.round(value * 1000) / 1000;
}

const base = '/api/v1/admin/zone-profiles';
const guid = /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i;

/** Ids are GUIDs from the server; anything else never reaches a URL. */
function ids<T>(values: string[], call: () => Promise<Result<T>>): Promise<Result<T>> {
	return values.every((value) => guid.test(value))
		? call()
		: Promise.resolve(fail<T>('Refused: not an id.'));
}

export function history(siteCode: string): Promise<Result<ProfileSummary[]>> {
	return Api.get<ProfileSummary[]>(base, { query: { siteCode } });
}

export function get(id: string): Promise<Result<Profile>> {
	return ids([id], () => Api.get<Profile>(`${base}/${id}`));
}

export function createDraft(siteCode: string, name?: string): Promise<Result<Profile>> {
	return Api.post<Profile>(`${base}/drafts`, { siteCode, name: name ?? null });
}

export function discard(id: string): Promise<Result<unknown>> {
	return ids([id], () => Api.delete<unknown>(`${base}/${id}`));
}

export function addZone(
	id: string,
	zone: {
		name: string;
		kind: ZoneKind;
		levelId: string;
		polygon: string;
		queueZoneId?: string | null;
		deskId?: string | null;
	}
): Promise<Result<Zone>> {
	return ids([id], () => Api.post<Zone>(`${base}/${id}/zones`, zone));
}

export function updateZone(
	id: string,
	zoneId: string,
	zone: { name: string; polygon: string }
): Promise<Result<Zone>> {
	return ids([id, zoneId], () => Api.put<Zone>(`${base}/${id}/zones/${zoneId}`, zone));
}

export function removeZone(id: string, zoneId: string): Promise<Result<unknown>> {
	return ids([id, zoneId], () => Api.delete<unknown>(`${base}/${id}/zones/${zoneId}`));
}

export function addLine(
	id: string,
	line: {
		name: string;
		role: LineRole;
		levelId: string;
		startX: number;
		startY: number;
		endX: number;
		endY: number;
		zoneId?: string | null;
	}
): Promise<Result<Line>> {
	return ids([id], () => Api.post<Line>(`${base}/${id}/lines`, line));
}

export function removeLine(id: string, lineId: string): Promise<Result<unknown>> {
	return ids([id, lineId], () => Api.delete<unknown>(`${base}/${id}/lines/${lineId}`));
}

export function validate(id: string): Promise<Result<Validation>> {
	return ids([id], () => Api.get<Validation>(`${base}/${id}/validation`));
}

/** Publishes the geometry the user reviewed (its hash); a draft changed since is refused (409). */
export function publish(id: string, geometryHash: string): Promise<Result<ProfileSummary>> {
	return ids([id], () => Api.post<ProfileSummary>(`${base}/${id}/publish`, { geometryHash }));
}

export function floorPlan(levelId: string): Promise<Result<FloorPlan>> {
	return ids([levelId], () => Api.get<FloorPlan>(`/api/v1/admin/levels/${levelId}/floor-plan`));
}

/** The plan image, fetched with the user's token and shown through an object URL (img-src blob:). */
export function floorPlanContent(levelId: string): Promise<Result<Blob>> {
	return ids([levelId], () =>
		Api.get<Blob>(`/api/v1/admin/levels/${levelId}/floor-plan/content`, { responseType: 'blob' })
	);
}

export function uploadFloorPlan(
	levelId: string,
	file: File,
	metresPerPixel: number,
	originX: number,
	originY: number
): Promise<Result<FloorPlan>> {
	const form = new FormData();
	form.append('file', file);
	form.append('metresPerPixel', String(metresPerPixel));
	form.append('originX', String(originX));
	form.append('originY', String(originY));
	return ids([levelId], () =>
		Api.post<FloorPlan>(`/api/v1/admin/levels/${levelId}/floor-plan`, form)
	);
}
