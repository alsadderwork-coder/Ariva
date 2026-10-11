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
	/** The 15-minute bin a health-check alarm judged (ARV-114b); null for the other metrics. */
	binStartUtc?: string | null;
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

/** The site's open alerts the caller's role is responsible for (the server decides), newest first; at most 500 a page. */
export function openAlerts(siteCode: string, pageSize = 100): Promise<Result<Page<Alert>>> {
	return Api.get<Page<Alert>>('/api/v1/alerts', {
		query: { siteCode, open: true, pageSize: Math.min(Math.max(1, pageSize), 500) }
	});
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

/** A queue zone of a border hall that is a lane's queue (ARV-057); its wait comes live from the hub by zone key. */
export interface ImmigrationQueue {
	zoneKey: string;
	zone: string;
	lane: string;
}

export interface ImmigrationLane {
	lane: string;
	desksOpen: number;
	desksPaused: number;
	transactions: number;
	meanServiceSeconds: number | null;
	maxP90ServiceSeconds: number | null;
	meanCycleSeconds: number | null;
}

export interface ImmigrationEGates {
	gatesConfigured: number;
	gatesUsed: number;
	attempts: number;
	accepted: number;
	rejected: number;
	rejectRate: number | null;
	utilisation: number | null;
	rejects: {
		documentRead: number;
		biometricCapture: number;
		eligibility: number;
		referredToOfficer: number;
		technical: number;
		other: number;
	};
	extraManualDeskMinutes: number;
	extraManualWaitMinutes: number | null;
}

export interface ImmigrationDesk {
	desk: string;
	lane: string | null;
	state: 'Opened' | 'Paused' | 'Closed' | 'Unknown' | string;
	transactions: number;
	meanServiceSeconds: number | null;
	p90ServiceSeconds: number | null;
	lastIntervalUtc: string | null;
}

export interface ImmigrationGate {
	gate: string;
	attempts: number;
	rejected: number;
	utilisation: number | null;
	meanCycleSeconds: number | null;
}

export interface ImmigrationHall {
	kind: 'Immigration' | 'Emigration' | string;
	queues: ImmigrationQueue[];
	lanes: ImmigrationLane[];
	eGates: ImmigrationEGates;
	desks: ImmigrationDesk[];
	gates: ImmigrationGate[];
}

/** The immigration view (ARV-057): lane and e-gate totals; desks and gates only for border roles (decided by the server). */
export interface Immigration {
	siteCode: string;
	asOfUtc: string;
	windowMinutes: number;
	desksIncluded: boolean;
	halls: ImmigrationHall[];
}

export function immigration(siteCode: string): Promise<Result<Immigration>> {
	if (!site.test(siteCode)) return Promise.resolve(fail<Immigration>('Refused: not a site.'));
	return Api.get<Immigration>(`/api/v1/sites/${siteCode}/immigration`);
}

/** An overflow band's state (ARV-120): its latest minute with a reading within the server's window, or NotHeard. */
export interface OverflowBand {
	band: string;
	state: 'Occupied' | 'Empty' | 'NotHeard' | string;
	minuteUtc: string | null;
	maxOccupancy: number | null;
}

/** A queue zone with overflow bands: Occupied when any band is (decided by the server). */
export interface OverflowZone {
	zone: string;
	state: 'Occupied' | 'Empty' | 'NotHeard' | string;
	bands: OverflowBand[];
}

/**
 * The overflow bands of a site's queue zones (ARV-120, LiveQueue.View); a queue zone without a band is not listed. A profile
 * with more bands than one answer holds (500) is refused with 409, never answered in part (`overflowRefused`).
 */
export interface OverflowBands {
	siteCode: string;
	asOfUtc: string;
	freshMinutes: number;
	zones: OverflowZone[];
}

/** The server refused the overflow read for its bound (409): the screens mark no queue and say why. */
export function overflowRefused(result: Result<unknown>): boolean {
	return result.hasErrors && result.status === 409;
}

export function overflowBands(siteCode: string): Promise<Result<OverflowBands>> {
	if (!site.test(siteCode)) return Promise.resolve(fail<OverflowBands>('Refused: not a site.'));
	return Api.get<OverflowBands>(`/api/v1/sites/${siteCode}/overflow-bands`);
}

/**
 * One 15-minute bin of a queue zone's continuous health checks (ARV-114a, formulas F18), its latest revision: the
 * conservation residual (null when either boundary is not measured), the tracks and their completion rate (null when no
 * track entered), and the minutes checked against the zone's physical capacity.
 */
export interface ZoneHealthBin {
	startUtc: string;
	lengthMinutes: number;
	revision: number;
	status: 'Provisional' | 'Final' | string;
	zoneProfileVersion: number;
	entries: number;
	exits: number;
	occupancyStart: number | null;
	occupancyEnd: number | null;
	conservationResidual: number | null;
	tracksEntered: number;
	tracksExited: number;
	tracksAbandoned: number;
	tracksFragmented: number;
	tracksCensored: number;
	tracksRejected: number;
	tracksOpen: number;
	trackCompletionRate: number | null;
	occupancyMinutes: number;
	capacityMinutes: number;
	minutesOutsideCapacity: number;
}

export interface ZoneHealth {
	siteCode: string;
	zone: string;
	fromUtc: string;
	toUtc: string;
	bins: ZoneHealthBin[];
	truncated: boolean;
}

/** A queue zone's name as the server takes it: 1 to 200 characters, no surrounding space, no control character. */
function isZoneName(value: string): boolean {
	return (
		value.length > 0 &&
		value.length <= 200 &&
		value.trim() === value &&
		!/[\u0000-\u001f\u007f-\u009f]/.test(value)
	);
}

/**
 * The health bins of a queue zone whose start lies in [from, to) (ARV-114a, DataQuality.View; at most 31 days): the zone
 * travels as a query value, never in the path.
 */
export function zoneHealth(
	siteCode: string,
	zone: string,
	from: Date,
	to: Date
): Promise<Result<ZoneHealth>> {
	if (!site.test(siteCode)) return Promise.resolve(fail<ZoneHealth>('Refused: not a site.'));
	if (!isZoneName(zone)) return Promise.resolve(fail<ZoneHealth>('Refused: not a zone.'));
	if (!(from.getTime() < to.getTime()))
		return Promise.resolve(fail<ZoneHealth>('Refused: not a range.'));
	return Api.get<ZoneHealth>(`/api/v1/sites/${siteCode}/zone-health`, {
		query: { zone, from: from.toISOString(), to: to.toISOString() }
	});
}
