import { Api, fail, type Result } from './Api';

/**
 * The observer's side of a validation campaign (ARV-104a, ARV-104b) as the observer tablet uses it (ARV-104c, ARV-104d):
 * the running campaigns of a site, a line count per 15-minute bin, a correction of one's own count, tracer batches with
 * the tablet's own clock reading, and desk state batches with their corrections. Everything is under Validation.Capture
 * and checked by the server: a site the caller cannot see answers 404, the campaign's creator or starter 403, a campaign
 * that is not running 409.
 *
 * Data boundary: tracers are labels (T-07), never names; observers are Ariva user ids. Desk states are border data: the
 * server lists a campaign's desks only to an account that may observe them (desksIncluded) and answers 403 to desk
 * batches, corrections and reads from any other; the tablet shows no desk log without them. Campaign, line, zone,
 * checkpoint and desk names and codes and correction reasons are text from the server: the screens render them as text
 * only (CWE-79).
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

/** A desk's state in one minute, as the observer saw it (ObservedDeskState). */
export type DeskState = 'Serving' | 'Idle' | 'Paused' | 'Closed';

/** The four states in the order the tablet offers them (as the live desk panel lists them). */
export const deskStates: readonly DeskState[] = ['Serving', 'Idle', 'Paused', 'Closed'];

/** One desk batch holds at most 20 desks (DeskObservationBatch.MaxDesks). */
export const maxDesksPerBatch = 20;

/** Each desk of a batch has exactly 15 states, one per minute of the bin (DeskObservationBatch.MinutesPerBin). */
export const minutesPerBin = 15;

export const minuteMs = 60_000;

/** A desk's 15 minutes in a batch: null where the minute was not observed. */
export interface DeskMinutes {
	deskId: string;
	states: (DeskState | null)[];
}

/** A recorded desk state for one minute (DeskObservationViewModel). Border data. */
export interface DeskObservation {
	id: string;
	campaignId: string;
	batchId: string | null;
	deskId: string;
	checkpoint: string | null;
	deskCode: string | null;
	minuteUtc: string;
	observerId: string;
	revision: number;
	current: boolean;
	state: DeskState;
	reason: string | null;
	correctsId: string | null;
	recordedUtc: string;
}

/** A recorded desk batch (DeskObservationBatchViewModel). */
export interface DeskBatch {
	id: string;
	campaignId: string;
	observerId: string;
	binStartUtc: string;
	receivedUtc: string;
	observations: DeskObservation[];
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
 * Fragments of the server's refusal texts the tablet recognises (ValidationErrors.ClockOffsetTooLarge,
 * ValidationErrors.KeyReused, ValidationErrors.DeskObservationsNeedBorderRole and ValidationErrors.OwnCampaign in
 * Ariva.Core). The API sends no error code for them, so ValidationErrorTextTests in Ariva.UnitTests reads these literals
 * from this file and fails when the C# constants stop containing them. Any other refusal is shown with Ariva's own text.
 */
export const clockRefusalText = "clock differs from the server's by more than";
export const keyReusedText = 'Idempotency-Key was already used for another request';
export const deskRoleText = 'Desk states are border data';
export const ownCampaignText = 'You created or started this campaign';

/** The server's refusal of desk states to an account that holds an airport role without seeing border desks (403). */
export function isDeskRoleRefusal(status: number | undefined, message: string): boolean {
	return status === 403 && message.includes(deskRoleText);
}

/**
 * The server's refusal of the campaign's own creator or starter (403). A 403 that is neither this nor the desk role
 * refusal (a permission taken away during the shift, for example) is shown with the server's text.
 */
export function isOwnCampaignRefusal(status: number | undefined, message: string): boolean {
	return status === 403 && message.includes(ownCampaignText);
}

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

/**
 * The running campaigns of a site the observer may count for. Also the tablet's read of Ariva's clock (its Date header),
 * so it gives up after 30 seconds like a send: a hung read never blocks the next one.
 */
export function running(siteCode: string): Promise<Result<CaptureCampaign[]>> {
	const path = base(siteCode);
	return path
		? Api.get<CaptureCampaign[]>(path, { signal: timeout() })
		: Promise.resolve(fail('Invalid site code.'));
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

/** The longest round trip whose answer still tells Ariva's clock (a slow answer may have waited on the way back). */
export const maxClockRoundTripMs = 10_000;

/**
 * The largest difference between Ariva's clock and the tablet's the desk log accepts (15 minutes, a whole bin): a reading
 * beyond it (a tablet hours off, or a Date header that is not Ariva's own, from a cache) leaves Ariva's clock unknown.
 */
export const maxArivaOffsetMs = 15 * 60_000;

/**
 * Ariva's clock minus this device's, in milliseconds, from an answer's Date header (ARV-104d), or null when the answer
 * has none or took too long. The header is Ariva's clock to the second when the answer was written, so the estimate is
 * the header's middle of that second against the device's clock when the answer came: within about a second, which is
 * plenty for a minute grid.
 */
export function arivaOffsetMs(result: Result<unknown>): number | null {
	const clock = result.serverClock;
	if (!clock) return null;
	const roundTrip = clock.receivedMs - clock.sentMs;
	if (roundTrip < 0 || roundTrip > maxClockRoundTripMs) return null;
	return clock.dateMs + 500 - clock.receivedMs;
}

/**
 * The caller's own desk states of a campaign (current revisions), latest minute first: at most 500, which holds the
 * last bins the observer may want to correct. Bound to the observer's account like every desk call.
 */
export function ownDeskObservations(
	siteCode: string,
	campaignId: string,
	asSubject: string
): Promise<Result<Page<DeskObservation>>> {
	const path = campaignPath(siteCode, campaignId);
	if (!path) return Promise.resolve(fail('Invalid request.'));
	return Api.get<Page<DeskObservation>>(`${path}/desk-observations`, {
		query: { sortBy: 'minuteUtc', sortDescending: true, pageSize: 500 },
		signal: timeout(),
		asSubject
	});
}

/**
 * One 15-minute bin of desk states (UTC on the quarter hour; minutes that have ended only), at most 20 desks with
 * exactly 15 states each, sent with its Idempotency-Key (kept for its retries). Anything else is refused here, before it
 * is sent.
 */
export function captureDesks(
	siteCode: string,
	campaignId: string,
	body: { binStartUtc: string; desks: DeskMinutes[] },
	key: string,
	asSubject: string
): Promise<Result<DeskBatch>> {
	const path = campaignPath(siteCode, campaignId);
	const valid =
		body.desks.length >= 1 &&
		body.desks.length <= maxDesksPerBatch &&
		body.desks.every(
			(d) =>
				guid.test(d.deskId) &&
				d.states.length === minutesPerBin &&
				d.states.every((s) => s === null || deskStates.includes(s))
		) &&
		body.desks.some((d) => d.states.some((s) => s !== null));
	if (!path || !valid) return Promise.resolve(fail('Invalid request.'));
	return Api.post<DeskBatch>(`${path}/desk-observations`, body, {
		headers: { 'Idempotency-Key': key },
		signal: timeout(),
		asSubject
	});
}

/** A correction of one's own latest desk state: the next revision with the corrected state and a reason. */
export function correctDesk(
	siteCode: string,
	campaignId: string,
	observationId: string,
	body: { state: DeskState; reason: string },
	key: string,
	asSubject: string
): Promise<Result<DeskObservation>> {
	const path = campaignPath(siteCode, campaignId);
	if (!path || !guid.test(observationId) || !deskStates.includes(body.state))
		return Promise.resolve(fail('Invalid request.'));
	return Api.post<DeskObservation>(`${path}/desk-observations/${observationId}/corrections`, body, {
		headers: { 'Idempotency-Key': key },
		signal: timeout(),
		asSubject
	});
}

// The campaign manager's side (ARV-104a, ARV-104b, ARV-104g; screens ARV-104h): the campaigns of a site, planning,
// starting and closing one, its progress and every observer's counts and tracer runs, and its results. Reading needs
// Validation.View, planning and starting Validation.Manage, closing Validation.Manage and a second factor within 15
// minutes (a critical action: Api.ts answers the server's challenge with the step-up dialog). The server decides every
// call; a site or campaign the caller cannot see answers 404. Desks and desk-state results are border data: the server
// leaves them out (desksIncluded false, desks null) for a caller who does not see border desks.

/** A campaign's lifecycle (ValidationCampaignStatus). */
export type CampaignStatus = 'Planned' | 'Running' | 'Closed';

export const campaignStatuses: readonly CampaignStatus[] = ['Planned', 'Running', 'Closed'];

/** The bounds of a campaign the server checks (ValidationCampaign in Ariva.Core); the form checks them first. */
export const campaignLimits = {
	name: 200,
	zones: 50,
	lines: 200,
	days: 31,
	/** Queue zones times planned days: larger campaigns' results cannot be computed (ARV-104g). */
	zoneDays: 400,
	desks: 100,
	daysBack: 31,
	daysAhead: 366,
	targetBinsPerLine: 31 * 96,
	targetTracerRuns: 1_000
} as const;

/** The placeholder targets Ariva records while TC-04 is not answered (ValidationCampaign.DefaultTarget*). */
export const placeholderTargets = { binsPerLine: 20, tracerRuns: 30 } as const;

/** A campaign in a list (ValidationCampaignSummaryViewModel). */
export interface CampaignSummary {
	id: string;
	siteCode: string;
	name: string;
	status: CampaignStatus;
	profileVersion: number;
	days: string[];
	zones: number;
	lines: number;
	createdUtc: string;
	startedUtc: string | null;
	closedUtc: string | null;
}

export interface CampaignTargets {
	binsPerLine: number;
	tracerRuns: number;
	placeholder: boolean;
}

/** A queue zone in scope with the tracer runs recorded in it (any observer). */
export interface CampaignZone {
	id: string;
	name: string;
	tracerRuns: number;
}

/** A line in scope with the 15-minute bins that hold at least one count (any observer). */
export interface CampaignLine {
	id: string;
	name: string;
	role: string;
	queueZone: string;
	binsCaptured: number;
}

/** A border desk in scope with its observed minutes. Border data: listed only when desksIncluded. */
export interface CampaignDesk {
	id: string;
	checkpoint: string;
	code: string;
	minutesObserved: number;
}

/** A campaign with its scope and progress (ValidationCampaignViewModel). People are Ariva user ids only. */
export interface Campaign {
	id: string;
	siteCode: string;
	name: string;
	status: CampaignStatus;
	profileId: string;
	profileVersion: number;
	geometryHash: string;
	profileStatus: string;
	profileRetiredUtc: string | null;
	timeZoneId: string;
	days: string[];
	targets: CampaignTargets;
	zones: CampaignZone[];
	lines: CampaignLine[];
	desksIncluded: boolean;
	desks: CampaignDesk[];
	createdById: string;
	createdUtc: string;
	startedById: string | null;
	startedUtc: string | null;
	closedById: string | null;
	closedUtc: string | null;
}

/** A campaign to plan (CreateValidationCampaignRequest): ids of the published version reviewed, local days, optional targets. */
export interface CreateCampaignRequest {
	name: string;
	profileVersion: number;
	zoneIds: string[];
	lineIds: string[];
	days: string[];
	targetBinsPerLine: number | null;
	targetTracerRuns: number | null;
	deskIds: string[];
}

/** A criterion's verdict (CriterionVerdict). */
export type Verdict = 'NoData' | 'Pass' | 'Fail';

/** The pilot criteria in their order (CampaignCriterion). */
export type Criterion =
	| 'CountAccuracy'
	| 'WaitError'
	| 'WaitBias'
	| 'TrackCompletion'
	| 'DeskStateAgreement'
	| 'NowcastError'
	| 'Availability';

export const criteria: readonly Criterion[] = [
	'CountAccuracy',
	'WaitError',
	'WaitBias',
	'TrackCompletion',
	'DeskStateAgreement',
	'NowcastError',
	'Availability'
];

export interface CriterionCheck {
	value: number | null;
	target: number;
	verdict: Verdict;
}

/** One line (count accuracy) or zone (track completion) of a criterion judged per unit. */
export interface CriterionUnit {
	queueZone: string | null;
	lineName: string | null;
	value: number | null;
	judged: number;
	required: number;
	excluded: number;
	judgedVerdict: Verdict;
	verdict: Verdict;
}

/** A campaign's verdict on one criterion (CampaignCriterionVerdict). */
export interface CriterionVerdict {
	criterion: Criterion;
	verdict: Verdict;
	reason: string;
	value: number | null;
	judgedValue: number | null;
	target: number;
	judged: number;
	required: number;
	excluded: number;
	excludedShare: number | null;
	units: CriterionUnit[];
}

export interface StandingTally {
	good: number;
	noSystemWait: number;
	degraded: number;
	unknown: number;
	provisional: number;
	otherVersion: number;
}

/** Count accuracy of one line over the campaign (LineAccuracy). */
export interface LineAccuracy {
	queueZone: string;
	lineId: string;
	lineName: string;
	role: string;
	judged: number;
	passing: number;
	judgedBins: number;
	excludedBins: number;
	lowestAccuracy: number | null;
	pooledAccuracy: number | null;
	bins: StandingTally;
	check: CriterionCheck;
}

/** Tracer wait error and bias of one zone, or of every zone (queueZone null), TracerZoneSummary. */
export interface TracerZoneSummary {
	queueZone: string | null;
	runs: number;
	compared: number;
	excluded: number;
	withinTolerance: number;
	bias: number | null;
	meanErrorMinutes: number | null;
	largestAbsoluteErrorMinutes: number | null;
	abandoned: number;
	offsetOutliers: number;
	errorCheck: CriterionCheck;
	biasCheck: CriterionCheck;
}

export interface TrackTotals {
	bins: number;
	entered: number;
	exited: number;
	rate: number | null;
}

/** Track completion of one queue zone (ZoneTrackCompletion). */
export interface ZoneTrackCompletion {
	queueZone: string;
	bins: StandingTally;
	excludedBins: number;
	good: TrackTotals;
	check: CriterionCheck;
}

/** Errors of a set of nowcast minutes (NowcastErrorStats), in minutes. */
export interface NowcastErrorStats {
	minutes: number;
	medianAbsoluteErrorMinutes: number | null;
	meanAbsoluteErrorMinutes: number | null;
	meanErrorMinutes: number | null;
}

export interface NowcastErrorSummary {
	minutes: number;
	judged: NowcastErrorStats;
	noServiceUnderCut: number;
}

/** The nowcast error of a zone, or of every zone, with the ground-truth proof (shadow and both null without the proof). */
export interface NowcastZoneErrors {
	queueZone: string | null;
	published: NowcastErrorSummary | null;
	shadow: NowcastErrorSummary | null;
	both: { minutes: number; published: NowcastErrorStats; shadow: NowcastErrorStats } | null;
	check: CriterionCheck;
}

/** Desk-state agreement of one border desk, or of every desk (deskId null). Border data. */
export interface DeskAgreementSummary {
	deskId: string | null;
	checkpointCode: string | null;
	deskCode: string | null;
	minutes: number;
	judged: number;
	agreeing: number;
	excluded: number;
	withoutSystemMinute: number;
	strictAgreement: number | null;
	check: CriterionCheck;
}

/** A calibration record of a device in scope (no notes, no person). */
export interface CalibrationRecord {
	deviceCode: string;
	queueZone: string;
	method: string;
	sampleSize: number;
	countingAccuracyPercent: number;
	waitTimeErrorMinutes: number;
	thresholdPercent: number;
	passed: boolean;
	performedOn: string;
}

/** A frozen revision of the results: its number, how many there are, the SHA-256 of the stored document. */
export interface ResultsRevision {
	number: number;
	revisions: number;
	contentSha256: string;
	frozenUtc: string;
	reason: string | null;
}

/**
 * A campaign's results as the caller may read them (ValidationResultsViewModel, projected per caller by the server):
 * `desks` is null for a caller who does not see border desks, the shadow's figures are null without the proof, and
 * `revision` is null for a campaign not closed (computed when asked, never stored).
 */
export interface CampaignResults {
	campaignId: string;
	siteCode: string;
	campaignName: string;
	status: CampaignStatus;
	profileVersion: number;
	geometryHash: string;
	plannedDays: string[];
	timeZoneId: string;
	computedUtc: string;
	problem: string | null;
	targets: { binsPerLine: number; tracerRuns: number; placeholder: boolean; exclusions: string };
	criteria: CriterionVerdict[];
	review: string[];
	counts: { lines: LineAccuracy[] } | null;
	tracers: { zones: TracerZoneSummary[]; overall: TracerZoneSummary | null } | null;
	trackCompletion: ZoneTrackCompletion[];
	nowcast: {
		shadowRead: boolean;
		zones: NowcastZoneErrors[];
		overall: NowcastZoneErrors | null;
	} | null;
	calibrations: CalibrationRecord[];
	desks: {
		verdict: CriterionVerdict;
		desks: DeskAgreementSummary[];
		overall: DeskAgreementSummary | null;
	} | null;
	revision: ResultsRevision | null;
	audience: { desksIncluded: boolean; observers: string; proofIncluded: boolean } | null;
}

function manageBase(siteCode: string): string | null {
	return site.test(siteCode)
		? `/api/v1/sites/${encodeURIComponent(siteCode)}/validation/campaigns`
		: null;
}

function managed(siteCode: string, campaignId: string): string | null {
	const root = manageBase(siteCode);
	return root && guid.test(campaignId) ? `${root}/${campaignId}` : null;
}

/** True for a value that can name a campaign in a path or a link (a GUID). */
export function isCampaignId(value: string | null | undefined): value is string {
	return typeof value === 'string' && guid.test(value);
}

/** True for a value that can name a site in a path or a link. */
export function isSiteCode(value: string | null | undefined): value is string {
	return typeof value === 'string' && site.test(value);
}

/** The campaigns of a site, newest first: text in the name and a status, pages of at most 100. */
export function searchCampaigns(
	siteCode: string,
	query: { text?: string; status?: CampaignStatus | ''; pageIndex?: number } = {}
): Promise<Result<Page<CampaignSummary>>> {
	const path = manageBase(siteCode);
	if (!path) return Promise.resolve(fail('Invalid site code.'));
	const status = query.status && campaignStatuses.includes(query.status) ? query.status : undefined;
	return Api.get<Page<CampaignSummary>>(path, {
		query: {
			text: query.text?.trim() ? query.text.trim().slice(0, 64) : undefined,
			status,
			sortBy: 'createdUtc',
			sortDescending: true,
			pageIndex: query.pageIndex ?? 1,
			pageSize: 100
		}
	});
}

/** A campaign with its scope, targets and progress. */
export function getCampaign(siteCode: string, campaignId: string): Promise<Result<Campaign>> {
	const path = managed(siteCode, campaignId);
	return path ? Api.get<Campaign>(path) : Promise.resolve(fail('Invalid request.'));
}

/** Plans a campaign over the site's published zone profile version (Planned). */
export function createCampaign(
	siteCode: string,
	body: CreateCampaignRequest
): Promise<Result<Campaign>> {
	const path = manageBase(siteCode);
	const valid =
		[...body.zoneIds, ...body.lineIds, ...body.deskIds].every((id) => guid.test(id)) &&
		body.days.every((day) => /^\d{4}-\d{2}-\d{2}$/.test(day));
	if (!path || !valid) return Promise.resolve(fail('Invalid request.'));
	return Api.post<Campaign>(path, body);
}

/** Starts a planned campaign (409 when its profile version is no longer the published one). */
export function startCampaign(siteCode: string, campaignId: string): Promise<Result<Campaign>> {
	const path = managed(siteCode, campaignId);
	return path ? Api.post<Campaign>(`${path}/start`) : Promise.resolve(fail('Invalid request.'));
}

/**
 * Closes a planned or running campaign: nothing is captured or corrected afterwards and the results are frozen. A critical
 * action: without a second factor in the last 15 minutes Ariva answers 401 and Api.ts opens the step-up dialog, then sends
 * the close once more; a cancelled dialog leaves the campaign as it was (the answer stays 401).
 */
export function closeCampaign(siteCode: string, campaignId: string): Promise<Result<Campaign>> {
	const path = managed(siteCode, campaignId);
	return path ? Api.post<Campaign>(`${path}/close`) : Promise.resolve(fail('Invalid request.'));
}

/** Every observer's counts of a campaign, newest bin first: current revisions or every revision, optionally one line. */
export function campaignCounts(
	siteCode: string,
	campaignId: string,
	query: { lineId?: string; currentOnly: boolean; pageIndex: number }
): Promise<Result<Page<ManualCount>>> {
	const path = managed(siteCode, campaignId);
	if (!path || (query.lineId && !guid.test(query.lineId)))
		return Promise.resolve(fail('Invalid request.'));
	return Api.get<Page<ManualCount>>(`${path}/counts`, {
		query: {
			lineId: query.lineId || undefined,
			currentOnly: query.currentOnly,
			sortBy: 'binStartUtc',
			sortDescending: true,
			pageIndex: query.pageIndex,
			pageSize: 100
		}
	});
}

/** The most tracer runs the progress view reads (20 pages of 500); beyond it the view says it shows the first ones. */
export const maxProgressRuns = 10_000;

/** Every observer's tracer runs of a campaign, a page of 500 at a time, earliest join first. */
export function campaignRuns(
	siteCode: string,
	campaignId: string,
	pageIndex: number
): Promise<Result<Page<TracerRun>>> {
	const path = managed(siteCode, campaignId);
	if (!path) return Promise.resolve(fail('Invalid request.'));
	return Api.get<Page<TracerRun>>(`${path}/tracer-runs`, {
		query: { sortBy: 'joinedUtc', sortDescending: false, pageIndex, pageSize: 500 }
	});
}

/**
 * The campaign's results as the caller may read them: the latest frozen revision of a closed campaign (or `revision`), a
 * campaign not closed computed now. The server may answer 429 or 503 with Retry-After while it computes others.
 */
export function campaignResults(
	siteCode: string,
	campaignId: string,
	revision?: number
): Promise<Result<CampaignResults>> {
	const path = managed(siteCode, campaignId);
	if (!path || (revision !== undefined && !(Number.isInteger(revision) && revision >= 1)))
		return Promise.resolve(fail('Invalid request.'));
	return Api.get<CampaignResults>(`${path}/results`, { query: { revision } });
}
