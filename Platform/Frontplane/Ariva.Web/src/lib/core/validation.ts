import { Api, fail, type Result } from './Api';

/**
 * The observer's side of a validation campaign (ARV-104a, ARV-104b) as the observer tablet uses it (ARV-104c): the
 * running campaigns of a site, a line count per 15-minute bin, a correction of one's own count, and tracer batches with
 * the tablet's own clock reading. Everything is under Validation.Capture and checked by the server: a site the caller
 * cannot see answers 404, the campaign's creator or starter 403, a campaign that is not running 409.
 *
 * Data boundary: tracers are labels (T-07), never names; observers are Ariva user ids. Campaign, line and zone names and
 * correction reasons are user text shown to others: the screens render them as text only (CWE-79).
 */

const site = /^[A-Z0-9]{2,8}(-[A-Z0-9]{1,8})?$/;
const guid = /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i;

/** One 15-minute bin, as the server counts them (UTC quarter hours, which are quarter hours in every site time zone). */
export const binMs = 15 * 60_000;

/** A tracer code is the campaign's label T-01 to T-999 (ASCII digits only), never a name (ValidationErrors.InvalidTracerCode). */
export const tracerCodePattern = /^T-[0-9]{2,3}$/;

/** The longest tracer run the server accepts (3 hours, ValidationErrors.InvalidRunTimes). */
export const maxRunMs = 3 * 60 * 60_000;

/** A correction reason is 1 to 200 characters (ManualCount.MaxReasonLength). */
export const maxReasonLength = 200;

/** Crossings in and out are whole numbers from 0 to 10,000 (ValidationErrors.InvalidCrossings). */
export const maxCrossings = 10_000;

export interface Page<T> {
	data: T[];
	totalCount: number;
	pageIndex: number;
	pageSize: number;
}

export interface CaptureLine {
	id: string;
	name: string;
	role: string;
	queueZone: string;
}

export interface CaptureZone {
	id: string;
	name: string;
}

export interface CaptureDesk {
	id: string;
	checkpoint: string;
	code: string;
}

/** A running campaign as an observer sees it (CaptureCampaignViewModel). */
export interface CaptureCampaign {
	id: string;
	siteCode: string;
	name: string;
	timeZoneId: string;
	days: string[];
	binMinutes: number;
	lines: CaptureLine[];
	zones: CaptureZone[];
	desksIncluded: boolean;
	desks: CaptureDesk[];
	maxClockOffsetSeconds: number;
}

/** A count revision (ManualCountViewModel). */
export interface ManualCount {
	id: string;
	campaignId: string;
	lineId: string;
	lineName: string;
	binStartUtc: string;
	observerId: string;
	revision: number;
	current: boolean;
	crossingsIn: number;
	crossingsOut: number;
	reason: string | null;
	correctsId: string | null;
	recordedUtc: string;
}

export interface TracerRunRequest {
	zoneId: string;
	tracerCode: string;
	joinedUtc: string;
	exitedUtc: string;
	abandoned: boolean;
}

/** A recorded tracer run (TracerRunViewModel): server-clock times, the device's times and the batch's offset. */
export interface TracerRun {
	id: string;
	campaignId: string;
	batchId: string;
	zoneId: string;
	zoneName: string;
	tracerCode: string;
	observerId: string;
	joinedUtc: string;
	exitedUtc: string;
	waitSeconds: number;
	abandoned: boolean;
	joinedRawUtc: string;
	exitedRawUtc: string;
	clockOffsetMs: number;
	recordedUtc: string;
}

/** A recorded batch (TracerBatchViewModel): the measured offset is device minus server, in milliseconds. */
export interface TracerBatch {
	id: string;
	campaignId: string;
	observerId: string;
	deviceClockUtc: string;
	receivedUtc: string;
	clockOffsetMs: number;
	runs: TracerRun[];
}

/** A fresh Idempotency-Key: a random UUID, never derived from the user, the line or the time (it is unique per observer). */
export function newKey(): string {
	if (typeof crypto.randomUUID === 'function') return crypto.randomUUID();
	const bytes = crypto.getRandomValues(new Uint8Array(16));
	bytes[6] = (bytes[6] & 0x0f) | 0x40;
	bytes[8] = (bytes[8] & 0x3f) | 0x80;
	const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
	return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/** UTC ISO 8601 ending in Z to the millisecond, as the server reads device times. */
export function utc(ms: number): string {
	return new Date(ms).toISOString();
}

/** The bin's start in UTC on the quarter hour without milliseconds ("2026-10-08T06:15:00Z"). */
export function binStartUtc(binStartMs: number): string {
	return new Date(binStartMs).toISOString().replace('.000Z', 'Z');
}

/** The start of the bin a time falls in. */
export function binOf(ms: number): number {
	return Math.floor(ms / binMs) * binMs;
}

/** The local date (yyyy-MM-dd) of a time in a time zone; the campaign's planned days are such dates. */
export function localDate(ms: number, timeZone: string): string {
	return new Intl.DateTimeFormat('en-CA', {
		timeZone,
		year: 'numeric',
		month: '2-digit',
		day: '2-digit'
	}).format(new Date(ms));
}

/**
 * True when the request was sent and no usable answer came: a network failure or a timeout (status 0), or a server that
 * could not answer now (408, 429, 5xx). Worth sending again with the same key. A request refused before it left the
 * browser (no status) and any other refusal are final.
 */
export function isRetryable(result: Result<unknown>): boolean {
	const status = result.status;
	return (
		status === 0 || status === 408 || status === 429 || (status !== undefined && status >= 500)
	);
}

/**
 * Fragments of the server's refusal texts the tablet recognises (ValidationErrors.ClockOffsetTooLarge and
 * ValidationErrors.KeyReused in Ariva.Core). The API sends no error code for them, so ValidationErrorTextTests in
 * Ariva.UnitTests reads these two literals from this file and fails when the C# constants stop containing them.
 */
export const clockRefusalText = "clock differs from the server's by more than";
export const keyReusedText = 'Idempotency-Key was already used for another request';

/** The server's refusal of a tablet whose clock is more than 5 minutes off. */
export function isClockRefusal(status: number | undefined, message: string): boolean {
	return status === 400 && message.includes(clockRefusalText);
}

/** The server's refusal of a key sent again with another body (409). */
export function isKeyReused(status: number | undefined, message: string): boolean {
	return status === 409 && message.includes(keyReusedText);
}

/** How long a capture call may take before the tablet treats it as lost and keeps it for a retry. */
export const sendTimeoutMs = 30_000;

const timeout = (): AbortSignal => AbortSignal.timeout(sendTimeoutMs);

function base(siteCode: string): string | null {
	return site.test(siteCode)
		? `/api/v1/sites/${encodeURIComponent(siteCode)}/validation/capture/campaigns`
		: null;
}

function campaignPath(siteCode: string, campaignId: string): string | null {
	const root = base(siteCode);
	return root && guid.test(campaignId) ? `${root}/${campaignId}` : null;
}

/** The running campaigns of a site the observer may count for. */
export function running(siteCode: string): Promise<Result<CaptureCampaign[]>> {
	const path = base(siteCode);
	return path ? Api.get<CaptureCampaign[]>(path) : Promise.resolve(fail('Invalid site code.'));
}

/** The caller's own counts of a campaign (current revisions), newest bin first, optionally for one line. */
export function ownCounts(
	siteCode: string,
	campaignId: string,
	lineId?: string
): Promise<Result<Page<ManualCount>>> {
	const path = campaignPath(siteCode, campaignId);
	if (!path || (lineId && !guid.test(lineId))) return Promise.resolve(fail('Invalid request.'));
	return Api.get<Page<ManualCount>>(`${path}/counts`, {
		query: { lineId, sortBy: 'binStartUtc', sortDescending: true, pageSize: 100 }
	});
}

/** A count of a line for one ended bin, sent with its Idempotency-Key (kept for its retries). */
export function captureCount(
	siteCode: string,
	campaignId: string,
	body: { lineId: string; binStartUtc: string; crossingsIn: number; crossingsOut: number },
	key: string,
	asSubject: string
): Promise<Result<ManualCount>> {
	const path = campaignPath(siteCode, campaignId);
	if (!path || !guid.test(body.lineId)) return Promise.resolve(fail('Invalid request.'));
	return Api.post<ManualCount>(`${path}/counts`, body, {
		headers: { 'Idempotency-Key': key },
		signal: timeout(),
		asSubject
	});
}

/** A correction of one's own latest count: the next revision with a reason. */
export function correctCount(
	siteCode: string,
	campaignId: string,
	countId: string,
	body: { crossingsIn: number; crossingsOut: number; reason: string },
	key: string,
	asSubject: string
): Promise<Result<ManualCount>> {
	const path = campaignPath(siteCode, campaignId);
	if (!path || !guid.test(countId)) return Promise.resolve(fail('Invalid request.'));
	return Api.post<ManualCount>(`${path}/counts/${countId}/corrections`, body, {
		headers: { 'Idempotency-Key': key },
		signal: timeout(),
		asSubject
	});
}

/** The caller's own tracer runs of a campaign, latest join first. */
export function ownRuns(siteCode: string, campaignId: string): Promise<Result<Page<TracerRun>>> {
	const path = campaignPath(siteCode, campaignId);
	if (!path) return Promise.resolve(fail('Invalid request.'));
	return Api.get<Page<TracerRun>>(`${path}/tracer-runs`, {
		query: { sortBy: 'joinedUtc', sortDescending: true, pageSize: 100 }
	});
}

/**
 * A tracer batch with the tablet's clock read now (at every send, retries included: a stale reading would shift the
 * measured offset) and the batch's Idempotency-Key.
 */
export function captureRuns(
	siteCode: string,
	campaignId: string,
	runs: TracerRunRequest[],
	key: string,
	asSubject: string
): Promise<Result<TracerBatch>> {
	const path = campaignPath(siteCode, campaignId);
	if (!path) return Promise.resolve(fail('Invalid request.'));
	return Api.post<TracerBatch>(
		`${path}/tracer-runs`,
		{ deviceClockUtc: utc(Date.now()), runs },
		{ headers: { 'Idempotency-Key': key }, signal: timeout(), asSubject }
	);
}
