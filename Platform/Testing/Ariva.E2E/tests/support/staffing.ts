import pg from 'pg';

// ARV-181a and ARV-181b1: the staffing recommendation suites plant lane_staffing rows as Ariva.Api.Cronz writes them.
// Ariva.Api.Stream does not run in the E2E run, so the job itself has no queue length now and gives no recommendation;
// a suite plants a lane's minute with the numbers it checks. Planted rows must hold together as the API's read-back
// demands (SvcStaffingRecommendations.View and Plausible): 13 rows on the minute's plan, the lane's values alike in each,
// a recommendation exactly when the gap is None (with arrivals, a bound other than None unless it is 0, and a wait), at
// most the lane's desks and all of them at the limit, a shortfall only at the limit, and a lane-wide gap of None only with
// desks, a throughput and a queue length. The runtime role cannot delete rows; the migration login (this client) can.

/** A client on the migration login of the E2E database. */
export function staffingDatabase(): pg.Client {
	return new pg.Client({
		host: process.env.Database__Host || 'localhost',
		port: Number(process.env.Database__Port || 5432),
		database: process.env.Database__Name || 'postgres',
		user: process.env.Database__Migration__Username || 'postgres',
		password: process.env.Database__Migration__Password
	});
}

/** The 13 intervals the job plans from a minute: to the next quarter hour, then 12 quarter hours. */
export function staffingPlan(minute: Date): { start: Date; minutes: number }[] {
	const quarter = 15 * 60_000;
	const next = new Date(Math.floor(minute.getTime() / quarter) * quarter + quarter);
	const slots = [{ start: minute, minutes: Math.round((next.getTime() - minute.getTime()) / 60_000) }];
	for (let k = 1; k < 13; k++) slots.push({ start: new Date(next.getTime() + (k - 1) * quarter), minutes: 15 });
	return slots;
}

/** An ISO time as the API writes it (no milliseconds). */
export function apiTime(value: Date): string {
	return value.toISOString().replace('.000Z', 'Z');
}

/** A lane's values of a planted minute, repeated in each of its 13 rows. */
export interface PlantedLane {
	zoneKey: string;
	lane: string;
	desks: number;
	openNow: number | null;
	openNowDegraded: boolean;
	queueNow: number | null;
	targetMinutes: number;
	busyCap: number;
	throughputPerDesk: number | null;
	throughputSource: 'Measured' | 'Fallback' | null;
	laneGap: 'None' | 'NoQueueLength' | 'NoThroughput' | 'NoDesks';
}

/** One planted interval. */
export interface PlantedInterval {
	arrivals: number | null;
	recommended: number | null;
	bound: 'None' | 'Wait' | 'Busy' | 'Both';
	atLimit: boolean;
	shortfall: number | null;
	waitRecommended: number | null;
	waitOpen: number | null;
	noServiceOpen: boolean;
	gap: string;
}

/**
 * Writes a lane's minute (13 rows on the minute's plan) in one transaction, over any row of that minute: the job writes
 * each minute once (ON CONFLICT DO NOTHING), so a minute planted before the job's run of it stays as planted.
 */
export async function plantStaffingMinute(db: pg.Client, lane: PlantedLane, minute: Date, intervals: PlantedInterval[]): Promise<void> {
	const slots = staffingPlan(minute);
	if (intervals.length !== slots.length) throw new Error(`a planted minute has ${slots.length} intervals`);
	await db.query('BEGIN');
	try {
		await db.query('DELETE FROM lane_staffing WHERE zone_key = $1 AND minute_utc = $2', [lane.zoneKey, minute]);
		for (const [k, slot] of slots.entries()) {
			const i = intervals[k];
			await db.query(
				`INSERT INTO lane_staffing (zone_key, minute_utc, interval_index, lane, start_utc, minutes, arrivals, recommended, bound, at_limit, shortfall,
				                            wait_recommended, wait_open, no_service_open, backlog_end, gap, lane_gap, queue_minute_utc, queue_now, open_now,
				                            open_now_degraded, desks, target_minutes, busy_cap, throughput_per_desk, throughput_source, computed_utc)
				 VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21, $22, $23, $24, $25, $26, $27)
				 ON CONFLICT (zone_key, minute_utc, interval_index) DO UPDATE SET lane = EXCLUDED.lane, start_utc = EXCLUDED.start_utc, minutes = EXCLUDED.minutes,
				   arrivals = EXCLUDED.arrivals, recommended = EXCLUDED.recommended, bound = EXCLUDED.bound, at_limit = EXCLUDED.at_limit,
				   shortfall = EXCLUDED.shortfall, wait_recommended = EXCLUDED.wait_recommended, wait_open = EXCLUDED.wait_open,
				   no_service_open = EXCLUDED.no_service_open, backlog_end = EXCLUDED.backlog_end, gap = EXCLUDED.gap, lane_gap = EXCLUDED.lane_gap,
				   queue_minute_utc = EXCLUDED.queue_minute_utc, queue_now = EXCLUDED.queue_now, open_now = EXCLUDED.open_now,
				   open_now_degraded = EXCLUDED.open_now_degraded, desks = EXCLUDED.desks, target_minutes = EXCLUDED.target_minutes,
				   busy_cap = EXCLUDED.busy_cap, throughput_per_desk = EXCLUDED.throughput_per_desk, throughput_source = EXCLUDED.throughput_source,
				   computed_utc = EXCLUDED.computed_utc`,
				[
					lane.zoneKey,
					minute,
					k,
					lane.lane,
					slot.start,
					slot.minutes,
					i.arrivals,
					i.recommended,
					i.bound,
					i.atLimit,
					i.shortfall,
					i.waitRecommended,
					i.waitOpen,
					i.noServiceOpen,
					i.recommended == null ? null : 0,
					i.gap,
					lane.laneGap,
					lane.queueNow == null ? null : new Date(minute.getTime() - 60_000),
					lane.queueNow,
					lane.openNow,
					lane.openNowDegraded,
					lane.desks,
					lane.targetMinutes,
					lane.busyCap,
					lane.throughputPerDesk,
					lane.throughputSource,
					new Date(minute.getTime() + 1_000)
				]
			);
		}
		await db.query('COMMIT');
	} catch (error) {
		await db.query('ROLLBACK');
		throw error;
	}
}

/**
 * ARV-181b2: the constructed case of the open desks below recommendation alert (formulas F16, the plan's case), as the
 * staffing job writes interval 0 of each minute from 0 to 19 after a start: a lane of 8 desks with a measured throughput,
 * a queue of 40 and 30 arriving. Desks short (recommended minus open now) are 0 to minute 4, then 2, no row, 2, a degraded
 * open count, 3, a flight without passengers, an unknown open count, 1, 2, 0 and 0 (3 recommended, 5 open), then 0; minutes
 * 17 to 19 have no arrivals, which the read-back check refuses (8 short, never counted). A rule GreaterOrEqual 2, sustain 3
 * and clear after 2 is raised at minute 9 with 3 desks short and cleared at minute 15. tests/api/desks-below-recommendation.spec.ts
 * plants the same rows on its own lane.
 */
export const desksShortCase: { minute: number; recommended: number | null; openNow: number | null; degraded?: boolean; gap?: string; arrivals?: boolean }[] = [
	...[0, 1, 2, 3, 4].map((minute) => ({ minute, recommended: 4, openNow: 4 })),
	{ minute: 5, recommended: 6, openNow: 4 },
	{ minute: 7, recommended: 6, openNow: 4 },
	{ minute: 8, recommended: 7, openNow: 4, degraded: true },
	{ minute: 9, recommended: 7, openNow: 4 },
	{ minute: 10, recommended: null, openNow: 4, gap: 'FlightWithoutPassengers' },
	{ minute: 11, recommended: 5, openNow: null },
	{ minute: 12, recommended: 5, openNow: 4 },
	{ minute: 13, recommended: 6, openNow: 4 },
	{ minute: 14, recommended: 4, openNow: 4 },
	{ minute: 15, recommended: 3, openNow: 5 },
	{ minute: 16, recommended: 4, openNow: 4 },
	...[17, 18, 19].map((minute) => ({ minute, recommended: 8, openNow: 0, arrivals: false }))
];

/**
 * Plants {@link desksShortCase} for a lane from a whole UTC minute, over any rows of the lane between `clearFrom` and
 * `clearTo` (the job's own rows of those minutes, for a database kept from an earlier day). Rows hold together as
 * script 0053 and the read-back check demand.
 */
export async function plantDesksShortCase(db: pg.Client, zoneKey: string, lane: string, start: Date, clearFrom: Date, clearTo: Date): Promise<void> {
	await db.query('BEGIN');
	try {
		await db.query('DELETE FROM lane_staffing WHERE zone_key = $1 AND minute_utc >= $2 AND minute_utc <= $3', [zoneKey, clearFrom, clearTo]);
		for (const r of desksShortCase) {
			const minute = new Date(start.getTime() + r.minute * 60_000);
			await db.query(
				`INSERT INTO lane_staffing (zone_key, minute_utc, interval_index, lane, start_utc, minutes, arrivals, recommended, bound, at_limit, shortfall,
				                            wait_recommended, wait_open, no_service_open, backlog_end, gap, lane_gap, queue_minute_utc, queue_now, open_now,
				                            open_now_degraded, desks, target_minutes, busy_cap, throughput_per_desk, throughput_source, computed_utc)
				 VALUES ($1, $2, 0, $3, $2, $4, $5, $6, $7, $8, NULL, $9, $10, false, NULL, $11, 'None', $12, 40, $13, $14, 8, 10, 0.85, 0.5, 'Measured', $15)`,
				[
					zoneKey,
					minute,
					lane,
					15 - (minute.getUTCMinutes() % 15),
					r.arrivals === false ? null : 30,
					r.recommended,
					r.recommended == null || r.recommended === 0 ? 'None' : 'Wait',
					r.recommended === 8,
					r.recommended == null ? null : 8,
					r.recommended == null || r.openNow == null ? null : 12,
					r.gap ?? 'None',
					new Date(minute.getTime() - 60_000),
					r.openNow,
					r.degraded ?? false,
					new Date(minute.getTime() + 45_000)
				]
			);
		}
		await db.query('COMMIT');
	} catch (error) {
		await db.query('ROLLBACK');
		throw error;
	}
}

// Routed answers (ARV-181b1): a browser test that needs the panel to show given values answers the staffing request
// itself with page.route, shaped exactly as the API's view models (ISvcStaffingRecommendations.cs, camel case).

/** The staffing API's address as a page.route pattern, for any site. */
export const staffingRoute = '**/api/v1/sites/*/staffing-recommendation';

/** An interval as the API answers it (StaffingIntervalViewModel). */
export interface RoutedInterval {
	startUtc: string;
	minutes: number;
	arrivals: number | null;
	recommended: number | null;
	bound: string;
	atLimit: boolean;
	shortfallDesks: number | null;
	waitRecommendedMinutes: number | null;
	waitOpenNowMinutes: number | null;
	noServiceOpenNow: boolean;
	gap: string;
}

/** A lane as the API answers it (LaneStaffingViewModel). */
export interface RoutedLane {
	zone: string;
	zoneKey: string;
	lane: string;
	minuteUtc: string | null;
	targetMinutes: number | null;
	busyCap: number | null;
	desks: number | null;
	openNow: number | null;
	openNowDegraded: boolean;
	queueNow: number | null;
	cycleMinutes: number | null;
	throughputSource: string | null;
	gap: string;
	intervals: RoutedInterval[];
}

/** A site's answer as the API gives it (StaffingRecommendationViewModel). */
export interface RoutedAnswer {
	siteCode: string;
	asOfUtc: string;
	intervalMinutes: number;
	indicative: boolean;
	lanes: RoutedLane[];
}

/**
 * A routed lane of the demo airport computed at `minute` (the current minute unless given): 6 desks, 3 open now, a queue
 * of 25 and a measured cycle time, then 13 intervals on the minute's plan, each with 40 arriving, 4 desks recommended for
 * the wait and waits of 8 and 12.5 minutes. `values` replace the lane's values and `interval` the first interval's.
 */
export function routedLane(
	zone: string,
	lane: string,
	values: Partial<RoutedLane> = {},
	interval: Partial<RoutedInterval> = {},
	minute: Date = new Date(Math.floor(Date.now() / 60_000) * 60_000)
): RoutedLane {
	return {
		zone,
		zoneKey: `DMO/${zone}`,
		lane,
		minuteUtc: apiTime(minute),
		targetMinutes: 10,
		busyCap: 0.85,
		desks: 6,
		openNow: 3,
		openNowDegraded: false,
		queueNow: 25,
		cycleMinutes: 1.583,
		throughputSource: 'Measured',
		gap: 'None',
		...values,
		intervals: staffingPlan(minute).map((slot, k) => ({
			startUtc: apiTime(slot.start),
			minutes: slot.minutes,
			arrivals: 40,
			recommended: 4,
			bound: 'Wait',
			atLimit: false,
			shortfallDesks: null,
			waitRecommendedMinutes: 8,
			waitOpenNowMinutes: 12.5,
			noServiceOpenNow: false,
			gap: 'None',
			...(k === 0 ? interval : {})
		}))
	};
}

/** The demo airport's routed answer with these lanes, as of `asOf` (now unless given). */
export function routedAnswer(lanes: RoutedLane[], asOf: Date = new Date()): RoutedAnswer {
	return { siteCode: 'DMO', asOfUtc: asOf.toISOString(), intervalMinutes: 15, indicative: true, lanes };
}
