import { Api, fail, type Result } from './Api';

/**
 * The topology admin API (ARV-014, ARV-015) as the topology screen uses it (ARV-052). Every call is limited to the
 * caller's sites by the server: another site's record answers 404, a change without the permission 403.
 */

export interface Page<T> {
	data: T[];
	totalCount: number;
	pageIndex: number;
	pageSize: number;
}

export interface Site {
	code: string;
	name: string;
	/** "Illustrative, not surveyed" (ARV-139a): set by the server's demo seed only; the screens show a banner. */
	illustrative?: boolean;
}

export interface Airport {
	id: string;
	iataCode: string;
	icaoCode: string | null;
	name: string;
	timeZoneId: string;
}

export interface Terminal {
	id: string;
	airportId: string;
	airportIataCode: string;
	code: string;
	name: string;
	siteCode: string;
}

export interface Level {
	id: string;
	terminalId: string;
	code: string;
	name: string;
	floorNumber: number;
	widthMetres: number;
	depthMetres: number;
	siteCode: string;
}

export const checkpointKinds = ['CheckIn', 'Security', 'Emigration', 'Immigration'] as const;
export type CheckpointKind = (typeof checkpointKinds)[number];

export interface Checkpoint {
	id: string;
	levelId: string;
	code: string;
	name: string;
	kind: CheckpointKind;
	siteCode: string;
}

export const deskKinds = ['Counter', 'SecurityLane', 'Desk', 'EGate'] as const;
export type DeskKind = (typeof deskKinds)[number];

/** The desk kinds a checkpoint kind allows (Ariva.Core Checkpoint.AllowedDeskKinds). */
export function allowedDeskKinds(kind: CheckpointKind): DeskKind[] {
	switch (kind) {
		case 'CheckIn':
			return ['Counter'];
		case 'Security':
			return ['SecurityLane'];
		default:
			return ['Desk', 'EGate'];
	}
}

/** The reference lane categories (Ariva.Core LaneCategory.Reference). */
export const laneCategories = ['CRW', 'CIT', 'RES', 'VIS', 'EG'] as const;

export interface Desk {
	id: string;
	checkpointId: string;
	code: string;
	name: string | null;
	kind: DeskKind;
	laneCategories: string[];
	inService: boolean;
	siteCode: string;
}

export const externalSystems = ['Aman', 'Aodb'] as const;

export interface DeskCodeMapping {
	id: string;
	system: string;
	externalCode: string;
	deskId: string;
	deskCode: string;
	siteCode: string;
}

/** The entities of the tree, parent first, with their admin route. */
export const entities = {
	airport: 'airports',
	terminal: 'terminals',
	level: 'levels',
	checkpoint: 'checkpoints',
	desk: 'desks'
} as const;
export type EntityName = keyof typeof entities;

const base = '/api/v1/admin';
const guid = /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i;

/** An id as a path segment: GUIDs only, so a value such as '..' can never change the request's target. */
function segment(id: string): string | null {
	return guid.test(id) ? id : null;
}

function refused<T>(): Promise<Result<T>> {
	return Promise.resolve(fail<T>('Refused: not an id.'));
}

/** One page of an entity (at most 500), by parent and site, sorted by code. */
export function search<T>(
	entity: EntityName,
	query: { parentId?: string; siteCode?: string; text?: string } = {}
): Promise<Result<Page<T>>> {
	return Api.get<Page<T>>(`${base}/${entities[entity]}`, {
		query: { ...query, sortBy: 'code', pageSize: 500 }
	});
}

export function create<T>(entity: EntityName, body: unknown): Promise<Result<T>> {
	return Api.post<T>(`${base}/${entities[entity]}`, body);
}

export function update<T>(entity: EntityName, id: string, body: unknown): Promise<Result<T>> {
	const at = segment(id);
	return at ? Api.put<T>(`${base}/${entities[entity]}/${at}`, body) : refused<T>();
}

export function remove(entity: EntityName, id: string): Promise<Result<unknown>> {
	const at = segment(id);
	return at ? Api.delete<unknown>(`${base}/${entities[entity]}/${at}`) : refused();
}

export function createDeskRange(body: {
	checkpointId: string;
	prefix: string;
	from: number;
	to: number;
	width: number;
	kind: DeskKind;
	laneCategories: string[];
}): Promise<Result<Desk[]>> {
	return Api.post<Desk[]>(`${base}/desks/range`, body);
}

export function sites(): Promise<Result<Site[]>> {
	return Api.get<Site[]>('/api/v1/sites');
}

export function mappings(deskId: string): Promise<Result<Page<DeskCodeMapping>>> {
	return Api.get<Page<DeskCodeMapping>>(`${base}/desk-code-mappings`, {
		query: { deskId, pageSize: 100 }
	});
}

export function createMapping(body: {
	system: string;
	externalCode: string;
	deskId: string;
}): Promise<Result<DeskCodeMapping>> {
	return Api.post<DeskCodeMapping>(`${base}/desk-code-mappings`, body);
}

export function removeMapping(id: string): Promise<Result<unknown>> {
	const at = segment(id);
	return at ? Api.delete<unknown>(`${base}/desk-code-mappings/${at}`) : refused();
}
