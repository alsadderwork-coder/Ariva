/**
 * Waiting-time bands of a passenger display (ARV-058, wiki 12): the nowcast shown as a band of whole minutes ("10 to 15
 * min"), a degraded nowcast as a band twice as wide, and a band kept until the nowcast leaves it by the hysteresis, so
 * the board does not flicker between two bands around an edge.
 */
export interface Band {
	low: number;
	high: number;
	degraded: boolean;
}

/** The band a nowcast falls in. */
export function bandOf(minutes: number, size: number, degraded: boolean): Band {
	const low = Math.max(0, Math.floor(minutes / size) * size);
	return { low, high: low + size * (degraded ? 2 : 1), degraded };
}

/**
 * The band to show next: the previous one while the nowcast stays within it widened by the hysteresis on both sides
 * (and the degraded flag has not changed), otherwise the nowcast's own band.
 */
export function nextBand(
	previous: Band | null,
	minutes: number,
	size: number,
	hysteresis: number,
	degraded: boolean
): Band {
	if (
		previous &&
		previous.degraded === degraded &&
		minutes >= previous.low - hysteresis &&
		minutes < previous.high + hysteresis
	)
		return previous;
	return bandOf(minutes, size, degraded);
}
