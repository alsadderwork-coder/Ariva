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
