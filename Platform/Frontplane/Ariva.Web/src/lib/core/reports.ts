import { Api, fail, type Result } from './Api';

/**
 * The daily report and its schedules (ARV-060 API, ARV-061 screen). The report is one site's local day, built by the
 * server from stored minutes; the alerts it lists are the ones the caller's roles see, decided by the server. CSV files
 * come from the server (formula prefixes neutralised there) and are saved as they are, never rebuilt here.
 */

const site = /^[A-Z0-9]{2,8}(-[A-Z0-9]{1,8})?$/;
const guid = /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i;
const isoDate = /^\d{4}-\d{2}-\d{2}$/;
const schedulesBase = '/api/v1/report-schedules';

/** How far back a report can be read (ISvcReports.MaxDaysBack). */
export const maxDaysBack = 400;
export const limits = { name: 120, recipients: 20 } as const;
export const csvSections = ['hours', 'alerts', 'devices'] as const;
export type CsvSection = (typeof csvSections)[number];

export interface LaneHour {
	start: string;
	end: string;
	passengers: number;
	waits: number;
	p50Minutes: number | null;
	p90Minutes: number | null;
	maxQueueLength: number | null;
	provisional: boolean;
	histogramMissing: boolean;
}

export interface LaneReport {
	zone: string;
	laneCategory: string | null;
	passengers: number;
	waits: number;
	p50Minutes: number | null;
	p90Minutes: number | null;
	maxQueueLength: number | null;
	peak: LaneHour | null;
	provisional: boolean;
	hours: LaneHour[];
}

export interface AlertLine {
	ruleCode: string;
	ruleName: string;
	zone: string | null;
	device: string | null;
	severity: string;
	state: string;
	raisedLocal: string;
	clearedLocal: string | null;
	openMinutes: number | null;
}

export interface DeviceUptime {
	code: string;
	zone: string | null;
	uptimePercent: number;
	outageMinutes: number;
	outages: number;
}

export interface DailyHeadline {
	worstPeakP90Minutes: number | null;
	worstPeakLane: string | null;
	worstPeakHour: string | null;
	zoneHoursAboveTarget: number;
	alerts: number;
	criticalAlerts: number;
	lowestUptimePercent: number | null;
}

export interface DailyReport {
	siteCode: string;
	date: string;
	timeZoneId: string;
	fromUtc: string;
	toUtc: string;
	generatedUtc: string;
	headline: DailyHeadline;
	lanes: LaneReport[];
	alertsBySeverity: Record<string, number>;
	alerts: AlertLine[];
	devices: DeviceUptime[];
}

export interface ReportRecipient {
	id: string;
	userName: string;
	displayName: string | null;
}

export interface ReportSchedule {
	id: string;
	siteCode: string;
	name: string;
	template: string;
	sendAt: string;
	enabled: boolean;
	ownerId: string;
	recipients: ReportRecipient[];
	lastSentUtc: string | null;
}

export interface ReportScheduleRequest {
	siteCode: string;
	name: string;
	template: string;
	sendAt: string;
	recipientIds: string[];
	enabled: boolean;
}

/** The local date (YYYY-MM-DD) in a time zone, `days` days from now; the browser's zone when the zone is unknown. */
export function localDate(timeZone: string | null, days = 0, now = new Date()): string {
	const at = new Date(now.getTime() + days * 86_400_000);
	try {
		// en-CA writes dates as YYYY-MM-DD.
		return new Intl.DateTimeFormat('en-CA', {
			timeZone: timeZone ?? undefined,
			year: 'numeric',
			month: '2-digit',
			day: '2-digit'
		}).format(at);
	} catch {
		return at.toISOString().slice(0, 10);
	}
}

export function daily(siteCode: string, date: string): Promise<Result<DailyReport>> {
	if (!site.test(siteCode)) return Promise.resolve(fail<DailyReport>('Refused: not a site.'));
	if (!isoDate.test(date)) return Promise.resolve(fail<DailyReport>('Refused: not a date.'));
	return Api.get<DailyReport>(`/api/v1/sites/${siteCode}/reports/daily`, { query: { date } });
}

/**
 * One section of the report as the server's CSV file. Anything but text/csv (a misrouted page, say) is refused, and the
 * file is retyped as CSV, so a saved or opened object URL can never render as a page of this origin.
 */
export async function csv(
	siteCode: string,
	date: string,
	section: CsvSection
): Promise<Result<Blob>> {
	if (!site.test(siteCode)) return fail<Blob>('Refused: not a site.');
	if (!isoDate.test(date) || !csvSections.includes(section))
		return fail<Blob>('Refused: not a report file.');
	const result = await Api.get<Blob>(`/api/v1/sites/${siteCode}/reports/daily.csv`, {
		query: { date, section },
		headers: { Accept: 'text/csv' },
		responseType: 'blob'
	});
	if (result.hasErrors || !(result.data instanceof Blob)) return result;
	if (!/^text\/csv\b/i.test(result.data.type))
		return fail<Blob>('Refused: the answer is not a CSV file.');
	return { ...result, data: new Blob([result.data], { type: 'text/csv' }) };
}

/** The file name the server would give the section: built only from the checked site, date and section. */
export function csvName(siteCode: string, date: string, section: CsvSection): string {
	return `ariva-${siteCode}-${date}-${section}.csv`;
}

/** Saves a file the server sent; the object URL is released once the browser has the download. */
export function save(blob: Blob, name: string): void {
	const url = URL.createObjectURL(blob);
	const link = document.createElement('a');
	link.href = url;
	link.download = name;
	link.rel = 'noopener';
	document.body.append(link);
	link.click();
	link.remove();
	setTimeout(() => URL.revokeObjectURL(url), 30_000);
}

export function schedules(siteCode: string): Promise<Result<ReportSchedule[]>> {
	if (!site.test(siteCode)) return Promise.resolve(fail<ReportSchedule[]>('Refused: not a site.'));
	return Api.get<ReportSchedule[]>(schedulesBase, { query: { siteCode } });
}

export function recipients(siteCode: string): Promise<Result<ReportRecipient[]>> {
	if (!site.test(siteCode)) return Promise.resolve(fail<ReportRecipient[]>('Refused: not a site.'));
	return Api.get<ReportRecipient[]>(`${schedulesBase}/recipients`, { query: { siteCode } });
}

export function createSchedule(request: ReportScheduleRequest): Promise<Result<ReportSchedule>> {
	return Api.post<ReportSchedule>(schedulesBase, request);
}

export function updateSchedule(
	id: string,
	request: ReportScheduleRequest
): Promise<Result<ReportSchedule>> {
	return guid.test(id)
		? Api.put<ReportSchedule>(`${schedulesBase}/${id}`, request)
		: Promise.resolve(fail<ReportSchedule>('Refused: not an id.'));
}

export function removeSchedule(id: string): Promise<Result<unknown>> {
	return guid.test(id)
		? Api.delete<unknown>(`${schedulesBase}/${id}`)
		: Promise.resolve(fail<unknown>('Refused: not an id.'));
}
