import type { StatusTone } from '$lib/components/shared/StatusBadge.svelte';
import type { ZoneHealthBin } from '$lib/core/operations';

/**
 * How the data-quality panel judges a health bin (ARV-120) against formulas F18's Proposed targets: a conservation
 * residual of 0, a track completion of at least 90 percent on a final bin, and no minute outside the zone's physical
 * capacity. Each result is a word as well as a tone (status is never colour alone); a check that cannot be judged is
 * neutral and says why.
 */

/** F18's track completion target (`HealthChecks.TrackCompletionTarget`), compared with F18's tolerance of 1e-9. */
export const completionTarget = 0.9;
const tolerance = 1e-9;

export type ResidualJudgement =
	| { kind: 'notMeasured'; tone: StatusTone }
	| { kind: 'value'; value: number; tone: StatusTone };

export function residualOf(bin: ZoneHealthBin): ResidualJudgement {
	const value = bin.conservationResidual;
	if (value === null || !Number.isFinite(value)) return { kind: 'notMeasured', tone: 'neutral' };
	return { kind: 'value', value, tone: value === 0 ? 'success' : 'warning' };
}

export type CompletionJudgement =
	| { kind: 'noTracks'; tone: StatusTone }
	| { kind: 'value' | 'soFar'; rate: number; exited: number; entered: number; tone: StatusTone };

/** A provisional bin's open tracks are people still queuing (F11), so only a final bin is judged against the target. */
export function completionOf(bin: ZoneHealthBin): CompletionJudgement {
	const rate = bin.trackCompletionRate;
	if (rate === null || !Number.isFinite(rate) || bin.tracksEntered <= 0)
		return { kind: 'noTracks', tone: 'neutral' };
	const counts = { rate, exited: bin.tracksExited, entered: bin.tracksEntered };
	if (bin.status !== 'Final') return { kind: 'soFar', ...counts, tone: 'neutral' };
	return {
		kind: 'value',
		...counts,
		tone: rate < completionTarget - tolerance ? 'warning' : 'success'
	};
}

export type OccupancyJudgement =
	| { kind: 'notChecked'; tone: StatusTone }
	| { kind: 'within'; checked: number; tone: StatusTone }
	| { kind: 'outside'; minutes: number; checked: number; tone: StatusTone };

/**
 * Outside counts the minutes with a reading below 0 or above a capacity among the minutes with readings (F18), so it is
 * told against those; within, against the minutes checked against a capacity.
 */
export function occupancyOf(bin: ZoneHealthBin): OccupancyJudgement {
	if (bin.minutesOutsideCapacity > 0)
		return {
			kind: 'outside',
			minutes: bin.minutesOutsideCapacity,
			checked: Math.max(bin.occupancyMinutes, bin.minutesOutsideCapacity),
			tone: 'warning'
		};
	if (bin.capacityMinutes <= 0) return { kind: 'notChecked', tone: 'neutral' };
	return { kind: 'within', checked: bin.capacityMinutes, tone: 'success' };
}

/** The bins of a zone newest first, at most `count`. */
export function latestBins(bins: readonly ZoneHealthBin[], count: number): ZoneHealthBin[] {
	return [...bins]
		.sort((a, b) => Date.parse(b.startUtc) - Date.parse(a.startUtc))
		.slice(0, Math.max(0, count));
}
