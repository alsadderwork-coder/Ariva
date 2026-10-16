import { Api, fail, type Result } from './Api';
import type { Page } from './topology';

/**
 * The alert rule API (ARV-037, ARV-038) as the alert rules screen uses it (ARV-056). A rule is typed data only: there is
 * no expression anywhere (CWE-94); the server checks every value, keeps each caller to their sites and roles, and
 * deleting is a critical action (step-up within 15 minutes, which Api.ts answers with the step-up dialog).
 */

const base = '/api/v1/admin/alert-rules';
const guid = /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i;

export const metrics = [
	'Nowcast',
	'PredictedNowcast',
	'BinP90',
	'QueueLength',
	'SensorOffline',
	'OverflowOccupied',
	'DesksBelowPlan',
	'ConservationResidual',
	'TrackCompletion',
	'OccupancyOutOfRange'
] as const;
/**
 * The continuous health checks of a queue zone's bins (F18, ARV-114b): judged per queue zone, so a rule on one watches
 * queue zones only (the server refuses overflow zones for them).
 */
export const healthChecks: ReadonlySet<string> = new Set([
	'ConservationResidual',
	'TrackCompletion',
	'OccupancyOutOfRange'
]);
/**
 * Metrics the evaluation cannot judge yet (AlertRuleErrors.NotEvaluatedMetrics): offered only when editing a rule that
 * already uses one (the server refuses new ones), and marked "not evaluated" in the list. Empty since ARV-181b2, when
 * DesksBelowPlan became evaluated (OverflowOccupied left with ARV-115); kept for a metric added before its inputs exist.
 */
export const notEvaluated: ReadonlySet<string> = new Set<string>();
/**
 * The metrics whose rules and alerts are border data (Ariva.Core BorderAlerts.Metrics): open desks below recommendation
 * (ARV-181b2) and, since ARV-187, the predicted nowcast (a lane queue stepped with its share of the arrival wave, from
 * which the lane split could be solved). The form offers them only to holders of {@link borderPermission}, owned by a
 * border role (never no owner) and escalated to a border role or none; open desks below recommendation also fires above
 * ({@link aboveOnly}) and watches queue zones only. Hiding them is a convenience: the server refuses every other caller
 * (403) and every other value (400), and hides such rules (404).
 */
export const borderOnly: ReadonlySet<string> = new Set(['DesksBelowPlan', 'PredictedNowcast']);
/** The permission whose holders create, change, preview and see border-only rules (BorderAlerts.Receives). */
export const borderPermission = 'ArrivalWaveLanes.View';
/**
 * The border roles (BorderAlerts.Roles): the alert roles holding ArrivalWaveLanes.View that are not airport-side. A
 * border-only rule belongs to one of them and escalates to one of them or to no role (AlertRuleErrors.BorderRolesOnly).
 */
export const borderRoles: readonly string[] = ['BorderShiftSupervisor', 'SystemAdministrator'];
/**
 * The border metrics that count a shortfall, so a rule on one fires above (AlertRuleValues.FiresAboveOnly,
 * AlertRuleErrors.BorderComparator): open desks below recommendation only. The predicted nowcast keeps all four
 * comparators (ARV-187).
 */
export const aboveOnly: ReadonlySet<string> = new Set(['DesksBelowPlan']);
/** The comparators of a rule on an {@link aboveOnly} metric. */
export const aboveComparators: readonly string[] = ['GreaterThan', 'GreaterOrEqual'];
/**
 * Whether a rule on the metric watches queue zones only (AlertRuleValues.WatchesQueueZonesOnly): the health checks
 * (stored per queue zone, ARV-114b) and open desks below recommendation (a recommendation per lane, a queue zone;
 * ARV-181b2). The predicted nowcast, border data since ARV-187, may watch an overflow zone (which takes its queue's
 * arrivals), so it is not listed here: listing it would drop a saved rule's overflow zones from the form.
 */
export function watchesQueueZonesOnly(metric: string): boolean {
	return healthChecks.has(metric) || metric === 'DesksBelowPlan';
}

/**
 * Whether a saved rule on a border-only metric fails the border checks (AlertRuleValues' BorderProblems), so the
 * evaluation never judges it (ARV-181b2, ARV-187 P3): an owner that is not a border role (no owner included), an
 * escalation to a role that is not a border role, or for an {@link aboveOnly} metric a comparator that does not fire
 * above. Such a rule comes from before ARV-187 (a predicted nowcast rule of an airport-side role) or from a row changed in
 * the database; only a border caller sees it, and saving it with border choices repairs it. A marker only: the server
 * decides.
 */
export function failsBorderRules(
	rule: Pick<AlertRule, 'metric' | 'comparator' | 'ownerRole' | 'escalateToRole'>
): boolean {
	if (!borderOnly.has(rule.metric)) return false;
	const isBorderRole = (role: string | null): boolean => !!role && borderRoles.includes(role);
	return (
		!isBorderRole(rule.ownerRole) ||
		(!!rule.escalateToRole && !isBorderRole(rule.escalateToRole)) ||
		(aboveOnly.has(rule.metric) && !aboveComparators.includes(rule.comparator))
	);
}
export const comparators = ['GreaterThan', 'GreaterOrEqual', 'LessThan', 'LessOrEqual'] as const;
export const severities = ['Info', 'Warning', 'Critical'] as const;
/** The roles a rule can belong to or escalate to (RoleCodes); administrators are not an owner of operational alerts. */
export const operationalRoles = [
	'BorderShiftSupervisor',
	'TerminalDutyManager',
	'HandlerStationManager'
] as const;

/** Every role a saved rule can name (an administrator may give a rule to administrators). */
export const roleCodes = [...operationalRoles, 'SystemAdministrator'] as const;

export type Metric = (typeof metrics)[number];
export type Unit = 'minutes' | 'people' | 'desks' | 'percent' | 'condition';

/** The limits the server applies (AlertRuleValues), so the form refuses what the server would. */
export const limits = {
	nameLength: 200,
	zones: 64,
	minutes: 120,
	escalationMinutes: 1440,
	contactLength: 100,
	leadMin: 15,
	leadMax: 60,
	backtestHours: 24
} as const;

export function unitOf(metric: string): Unit {
	switch (metric) {
		case 'QueueLength':
		case 'ConservationResidual':
			return 'people';
		case 'DesksBelowPlan':
			return 'desks';
		case 'TrackCompletion':
			return 'percent';
		case 'SensorOffline':
		case 'OverflowOccupied':
			return 'condition';
		default:
			return 'minutes';
	}
}

/** The largest threshold the server takes for a metric (AlertRuleValues.MaxThreshold). */
export function maxThreshold(metric: string): number {
	switch (metric) {
		case 'TrackCompletion':
			return 100;
		case 'OccupancyOutOfRange':
			return 1_440;
	}
	const unit = unitOf(metric);
	return unit === 'people' ? 100_000 : unit === 'desks' ? 1_000 : unit === 'minutes' ? 600 : 0;
}

export interface AlertRule {
	id: string;
	siteCode: string;
	code: string;
	name: string;
	zones: string[];
	metric: string;
	comparator: string;
	threshold: number | null;
	minQueueLength: number | null;
	clearThreshold: number | null;
	sustainMinutes: number;
	clearAfterMinutes: number;
	severity: string;
	ownerRole: string | null;
	escalateAfterMinutes: number | null;
	escalateToRole: string | null;
	escalationContact: string | null;
	notifyByEmail: boolean;
	enabled: boolean;
	createdOn: string | null;
	modifiedOn: string | null;
	leadMinutes: number | null;
}

/** What the server takes to create or change a rule (AlertRuleRequest); the code is the server's. */
export interface AlertRuleRequest {
	siteCode: string;
	name: string;
	zones: string[];
	metric: string;
	comparator: string;
	threshold: number | null;
	minQueueLength: number | null;
	clearThreshold: number | null;
	sustainMinutes: number;
	clearAfterMinutes: number;
	severity: string;
	ownerRole: string | null;
	escalateAfterMinutes: number | null;
	escalateToRole: string | null;
	escalationContact: string | null;
	notifyByEmail: boolean;
	enabled: boolean;
	leadMinutes: number | null;
}

export interface BacktestAlert {
	zoneName: string | null;
	deviceCode: string | null;
	raisedUtc: string;
	clearedUtc: string | null;
	value: number;
	binStartUtc: string | null;
	predictedForUtc: string | null;
}

export interface Backtest {
	count: number;
	firstRaisedUtc: string | null;
	alerts: BacktestAlert[];
	truncated: boolean;
	targets: number;
	targetsWithData: number;
}

/** The request for a saved rule, as the form starts from it (a duplicate or an edit). */
export function requestOf(rule: AlertRule): AlertRuleRequest {
	return {
		siteCode: rule.siteCode,
		name: rule.name,
		zones: [...rule.zones],
		metric: rule.metric,
		comparator: rule.comparator,
		threshold: rule.threshold,
		minQueueLength: rule.minQueueLength,
		clearThreshold: rule.clearThreshold,
		sustainMinutes: rule.sustainMinutes,
		clearAfterMinutes: rule.clearAfterMinutes,
		severity: rule.severity,
		ownerRole: rule.ownerRole,
		escalateAfterMinutes: rule.escalateAfterMinutes,
		escalateToRole: rule.escalateToRole,
		escalationContact: rule.escalationContact,
		notifyByEmail: rule.notifyByEmail,
		enabled: rule.enabled,
		leadMinutes: rule.leadMinutes
	};
}

/**
 * The request as the server wants it for the chosen metric: a condition has no threshold, clear threshold or minimum
 * queue and compares with IsTrue; a minimum queue is for the nowcast only; a lead time for the predicted nowcast only;
 * an empty contact is no contact. The server still checks every value.
 */
export function normalise(request: AlertRuleRequest): AlertRuleRequest {
	const condition = unitOf(request.metric) === 'condition';
	const contact = request.escalationContact?.trim() ?? '';
	return {
		...request,
		name: request.name.trim(),
		comparator: condition ? 'IsTrue' : request.comparator,
		threshold: condition ? null : request.threshold,
		clearThreshold: condition ? null : request.clearThreshold,
		minQueueLength: request.metric === 'Nowcast' ? request.minQueueLength : null,
		leadMinutes: request.metric === 'PredictedNowcast' ? request.leadMinutes : null,
		ownerRole: request.ownerRole || null,
		escalateToRole: request.escalateAfterMinutes ? request.escalateToRole || null : null,
		escalationContact: request.escalateAfterMinutes && contact ? contact : null
	};
}

export function search(siteCode: string): Promise<Result<Page<AlertRule>>> {
	return Api.get<Page<AlertRule>>(base, { query: { siteCode, pageSize: 500 } });
}

export function create(request: AlertRuleRequest): Promise<Result<AlertRule>> {
	return Api.post<AlertRule>(base, normalise(request));
}

export function update(id: string, request: AlertRuleRequest): Promise<Result<AlertRule>> {
	return guid.test(id)
		? Api.put<AlertRule>(`${base}/${id}`, normalise(request))
		: Promise.resolve(fail<AlertRule>('Refused: not an id.'));
}

export function remove(id: string): Promise<Result<unknown>> {
	return guid.test(id)
		? Api.delete<unknown>(`${base}/${id}`)
		: Promise.resolve(fail<unknown>('Refused: not an id.'));
}

/** How often the rule would have fired on the stored minutes of an ended range of up to a day, in UTC. */
export function backtest(
	request: AlertRuleRequest,
	fromUtc: string,
	toUtc: string
): Promise<Result<Backtest>> {
	return Api.post<Backtest>(`${base}/backtest`, { rule: normalise(request), fromUtc, toUtc });
}

/** A UTC time as the screen shows it, the same in both languages: 2026-10-02 18:05 UTC. */
export function utcText(iso: string | null | undefined): string {
	if (!iso) return '';
	const time = new Date(iso);
	return Number.isFinite(time.getTime())
		? `${time.toISOString().slice(0, 16).replace('T', ' ')} UTC`
		: '';
}

/** A datetime-local value (taken as UTC) as the API wants it, or null. */
export function utcOf(local: string): string | null {
	return /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(local) ? `${local}:00Z` : null;
}

/** The datetime-local value of a time, in UTC, to the minute. */
export function localOf(time: Date): string {
	return time.toISOString().slice(0, 16);
}
