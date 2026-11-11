// ARV-279c: the anchor a demo minute played at once is laid on, which the simulator's AMAN records carry in their source
// event ids (`aman-<seed>-<yyyyMMdd>T<HHmm>-<kind>-<code>-<minute>`, AmanFeed.IdTag). A spec that counts what its own play
// sent scopes the count by it, so the records an earlier run left in the database never count, and a run on services an
// earlier run used sends ids of its own.

/** `yyyyMMdd'T'HHmm` of a UTC instant, as the simulator writes an anchor. */
function anchorOf(at: Date): string {
	const iso = at.toISOString();
	return `${iso.slice(0, 4)}${iso.slice(5, 7)}${iso.slice(8, 10)}T${iso.slice(11, 13)}${iso.slice(14, 16)}`;
}

/**
 * The anchors a demo minute played at once between `before` and `after` (milliseconds of this machine's clock, read just
 * before the play call and just after its answer) can be laid on. The feeds lay a minute played at once on the simulator's
 * current wall minute (FeedTime, after the play's restart), so the anchor, the UTC minute of the demo day's 00:00, is that
 * wall minute less the demo minute. The simulator runs on this machine (ARV-128), so its wall minute lies between the two
 * readings, or one minute before the first: a play of the same minute by another spec just before, across a minute boundary,
 * keeps the feed on that play's anchor (FeedTime does not re-anchor a repeated minute). So two or three anchors.
 */
export function anchorsOf(minute: number, before: number, after: number): string[] {
	if (!Number.isInteger(minute) || minute < 0 || minute > 1439 || !(after >= before)) throw new Error('anchorsOf: a minute of the day, then two readings in order');
	const anchors: string[] = [];
	for (let wall = Math.floor(before / 60_000) - 1; wall <= Math.floor(after / 60_000); wall++) anchors.push(anchorOf(new Date((wall - minute) * 60_000)));
	return anchors;
}

/**
 * LIKE patterns, for `source_event_id LIKE ANY($n::text[])`, of the emulated AMAN's source event ids under those anchors:
 * `aman-`, any seed, the anchor, then `rest` (`dk-%-1100` for the desk intervals of minute 1100; anything by default).
 */
export function amanIds(anchors: string[], rest = '%'): string[] {
	return anchors.map((anchor) => `aman-%-${anchor}-${rest}`);
}
