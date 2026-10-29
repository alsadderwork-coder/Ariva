/**
 * The display player's clock of the server (ARV-197), kept apart with no imports so that it reads on its own and Node can
 * run it as it is (tests/api/advertising-server-clock.spec.ts): a board's serverUtc as milliseconds, and the clock the
 * player keeps from the boards it gets.
 */

/**
 * An instant from the server as milliseconds. The server sends UTC with its zone; a text without one is read as UTC too, never
 * as the screen's local time. NaN when it is not a date.
 */
export function utcMilliseconds(text: string | null | undefined): number {
	if (typeof text !== 'string' || text.length === 0) return Number.NaN;
	return Date.parse(/(?:Z|[+-]\d{2}:?\d{2})$/i.test(text) ? text : `${text}Z`);
}

/**
 * The server's clock as the display player keeps it from its boards (ARV-197): each good board's serverUtc against the moment
 * it arrived on the page's monotonic clock (performance.now()), never the screen's own clock, which may be wrong or be moved
 * while the player runs. A board that reaches the player later than the one before (a slow server or network) carries an
 * earlier time for the same moment and would put the clock back by the delay, so a plan that had run out would play again and
 * a play be counted twice; the clock takes the largest offset among the boards that arrived within a minute of the newest one
 * instead. It does not move back while a quicker board is that recent, and it follows a correction of the server's own clock
 * at the first board a minute after the last one that was ahead. A board far ahead (a server with a wrong clock) holds the
 * clock ahead for that minute: plans end early and the plays it stamps are refused by the server, the safe side.
 */
export class BoardClock {
	/** How long a board's offset counts after a newer board arrives. */
	static readonly windowMs = 60_000;
	/** At most this many boards are kept (a board every 10 seconds keeps 6). */
	static readonly maxSeen = 64;

	#seen: { at: number; offset: number }[] = [];

	/** Takes a good board's serverUtc as it arrives, `at` on the monotonic clock; a text that is not a time changes nothing. */
	observe(serverUtc: string | null | undefined, at: number): void {
		const server = utcMilliseconds(serverUtc);
		if (!Number.isFinite(server) || !Number.isFinite(at)) return;
		this.#seen = [
			...this.#seen.filter((seen) => at - seen.at < BoardClock.windowMs),
			{ at, offset: server - at }
		].slice(-BoardClock.maxSeen);
	}

	/** The server's clock in milliseconds since the epoch at `now` on the monotonic clock; NaN before the first board. */
	at(now: number): number {
		if (this.#seen.length === 0 || !Number.isFinite(now)) return Number.NaN;
		return now + Math.max(...this.#seen.map((seen) => seen.offset));
	}
}
