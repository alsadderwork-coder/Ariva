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
	'DesksBelowPlan'
] as const;
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
export type Unit = 'minutes' | 'people' | 'desks' | 'condition';

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
			return 'people';
		case 'DesksBelowPlan':
			return 'desks';
		case 'SensorOffline':
		case 'OverflowOccupied':
			return 'condition';
		default:
			return 'minutes';
	}
}

export function maxThreshold(metric: string): number {
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
