import { Api, fail, type Result } from './Api';
import type { Page } from './topology';

/** The live operations API (ARV-039, ARV-047, ARV-055) as the live operations screen uses it. */

export interface Alert {
	id: string;
	siteCode: string;
	ruleCode: string;
	ruleName: string;
	zoneName: string | null;
	deviceCode: string | null;
	metric: string;
	severity: 'Info' | 'Warning' | 'Critical' | string;
	state: 'Raised' | 'Acknowledged' | 'Escalated' | 'Cleared' | 'Resolved' | string;
	raisedUtc: string;
	raisedValue: number;
	ownerRole: string | null;
	acknowledgedUtc: string | null;
	acknowledgedBy: string | null;
	acknowledgedNote: string | null;
}

export interface LaneCounts {
	cit: number | null;
	res: number | null;
	vis: number | null;
	crw: number | null;
	eGate: number | null;
	total: number;
}

export interface ArrivalWave {
	siteCode: string;
	nowUtc: string;
	windowMinutes: number;
	flights: {
		flightKey: string;
		flight: string;
		origin: string;
		inBlockUtc: string;
		passengers: number | null;
	}[];
	minutes: { minuteUtc: string; lanes: LaneCounts }[];
	alertWindow: LaneCounts;
}

export interface DeskState {
	checkpoint: string;
	checkpointKind: string;
	desk: string;
	deskKind: string;
	lane: string;
	minuteUtc: string;
	state: 'Serving' | 'Idle' | 'Paused' | 'Closed' | 'Unknown' | string;
	transactions: number;
	degraded: boolean;
}

export interface DeskStates {
	siteCode: string;
	asOfUtc: string;
	desks: DeskState[];
	airportIncluded: boolean;
	borderIncluded: boolean;
	truncated: boolean;
}

const site = /^[A-Z0-9]{2,8}(-[A-Z0-9]{1,8})?$/;
const guid = /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i;

export function openAlerts(siteCode: string): Promise<Result<Page<Alert>>> {
	return Api.get<Page<Alert>>('/api/v1/alerts', { query: { siteCode, open: true, pageSize: 100 } });
}

export function acknowledge(id: string, note: string): Promise<Result<Alert>> {
	if (!guid.test(id)) return Promise.resolve(fail<Alert>('Refused: not an id.'));
	return Api.post<Alert>(`/api/v1/alerts/${id}/acknowledge`, { note: note || null });
}

export function arrivalWave(siteCode: string, minutes = 60): Promise<Result<ArrivalWave>> {
	if (!site.test(siteCode)) return Promise.resolve(fail<ArrivalWave>('Refused: not a site.'));
	return Api.get<ArrivalWave>(`/api/v1/sites/${siteCode}/arrival-wave`, { query: { minutes } });
}

export function deskStates(siteCode: string): Promise<Result<DeskStates>> {
	if (!site.test(siteCode)) return Promise.resolve(fail<DeskStates>('Refused: not a site.'));
	return Api.get<DeskStates>(`/api/v1/sites/${siteCode}/desk-states`);
}
