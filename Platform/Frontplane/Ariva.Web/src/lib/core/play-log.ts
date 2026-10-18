/**
 * The passenger display player's proof of play (ARV-144), on its own and without imports so that a test can hold it to its
 * rules outside the browser (tests/api/advertising-play-log.spec.ts).
 *
 * A play is one advertisement (never a slide) on screen in its slot: it starts when its image has loaded or its video plays
 * (not while its bytes are on their way, when the region shows the instructions), and it ends at the slot's end or when it
 * is cut short (interrupted: the wait rule, a stale board, the plan's end or change, advertising turned off, the item failing
 * after it showed, the page going). It is completed only when it ended at the slot's end and was on screen for the slot, as
 * the server judges it (`playLimits.slotToleranceMilliseconds` either way): an advertisement whose bytes came late in its
 * slot was on screen for less of it, so it is reported as cut short. Its start is by the server's clock as the player keeps
 * it from its boards (the board's serverUtc plus the time since it came), given by the caller; its length is measured on the
 * page's monotonic clock (performance.now, also given by the caller), so a screen whose own clock is changed meanwhile still
 * reports the time the advertisement was really on screen.
 *
 * Plays wait in memory only, never in any storage: at most `playLimits.queue`, the oldest out first when it is full (counted
 * in `dropped`). A batch of at most `playLimits.batch` goes every `playTiming.everyMilliseconds`, and sooner when a whole
 * batch waits, but never within `playTiming.soonestMilliseconds` of the last turn, so a player stays under the server's six
 * batches a minute; one request at a time. The server's answer decides (`outcomeOf`): a 200 lets the batch go (accepted,
 * duplicates and refused alike: a refused play is never worth sending again), a 400 or 413 lets it go (it can never succeed),
 * a 401 lets it go and stops sending until a good board comes back (`resume`), and anything else (no answer, a 429, a 5xx)
 * keeps it, at the front and in its order, for the next full turn: never a tight retry loop. A play sent twice counts once on
 * the server (keyed by display, advertisement and start), so a batch tried again, or sent as the page goes, is safe.
 *
 * The functional test shortens the turn through the player's `playsEvery` query parameter (whole seconds): `sendEvery` holds
 * it to `playTiming.everySeconds` (12 to 60, within the server's rate) and ignores anything else; nothing else reads it.
 */

/** The bounds of the log. */
export const playLimits = {
	/** Plays held at most (waiting and in flight); the oldest go first when it is full. */
	queue: 500,
	/** Plays in one batch: the server takes 1 to 100. */
	batch: 100,
	/** The longest play the server takes: a slot's ceiling of 120 seconds and a little for the player's timing. */
	durationMilliseconds: 125_000,
	/** The shortest: less than a second on screen is no play, and the server refuses it. */
	minimumDurationMilliseconds: 1_000,
	/** A completed play was on screen for its slot within this much: the server refuses one shorter as completed. */
	slotToleranceMilliseconds: 2_000
} as const;

/** When batches go. */
export const playTiming = {
	/** A batch every minute. */
	everyMilliseconds: 60_000,
	/** Sooner when a whole batch waits, but never within 12 seconds of the last turn (the server takes 6 a minute). */
	soonestMilliseconds: 12_000,
	/** What the player's `playsEvery` query parameter may set, in whole seconds; anything else is ignored. */
	everySeconds: { min: 12, max: 60 }
} as const;

/** One play as the server takes it (POST api/v1/display/plays). */
export interface Play {
	mediaId: string;
	/** ISO 8601 UTC with milliseconds and a Z, by the server's clock as the player keeps it. */
	startedUtc: string;
	/** The time it was on screen: a whole number, `playLimits.minimumDurationMilliseconds` to `playLimits.durationMilliseconds`. */
	durationMilliseconds: number;
	/** It played to its slot's end; false when it was cut short. */
	completed: boolean;
}

/** What the server did with a batch. */
export interface PlaysAnswer {
	accepted: number;
	duplicates: number;
	refused: number;
}

/**
 * What a batch's answer means for its plays: `answered` (a 2xx: let them go, counted as sent), `unsendable` (a 400 or 413: let
 * them go, they can never succeed), `unauthorized` (a 401: let them go and stop until a good board) or `retry` (no answer, a
 * 429, a 5xx or anything else: keep them for the next turn).
 */
export type PlaysOutcome = 'answered' | 'unsendable' | 'unauthorized' | 'retry';

export function outcomeOf(status: number | null | undefined): PlaysOutcome {
	if (typeof status !== 'number' || !Number.isInteger(status)) return 'retry';
	if (status >= 200 && status < 300) return 'answered';
	if (status === 401) return 'unauthorized';
	if (status === 400 || status === 413) return 'unsendable';
	return 'retry';
}

/**
 * The turn in milliseconds from the player's `playsEvery` query parameter: whole seconds held to 12 to 60; the minute when
 * it is missing or anything but a whole number.
 */
export function sendEvery(text: string | null | undefined): number {
	if (typeof text !== 'string' || !/^\d{1,4}$/.test(text)) return playTiming.everyMilliseconds;
	const seconds = Math.min(
		playTiming.everySeconds.max,
		Math.max(playTiming.everySeconds.min, Number(text))
	);
	return seconds * 1000;
}

const guid = /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i;

/** A play's length on screen: whole milliseconds, 0 to the server's ceiling. */
export function playDuration(startedAt: number, endedAt: number): number {
	const length = Math.round(endedAt - startedAt);
	if (!Number.isFinite(length) || length < 0) return 0;
	return Math.min(playLimits.durationMilliseconds, length);
}

/**
 * The plays of one player: the one on screen, the ones waiting and the batch in flight, with what tests read (`queued`,
 * `sent`, `dropped`). Times are the caller's: `serverNow` in milliseconds since the epoch by the server's clock, `now` on a
 * monotonic clock (performance.now in the browser).
 */
export class PlayLog {
	private readonly waiting: Play[] = [];
	private flight: Play[] | null = null;
	private current: {
		mediaId: string;
		startedUtc: number;
		at: number;
		slotMilliseconds: number;
	} | null = null;
	private lastTurn: number;
	private lastFailed = false;
	private halted = false;
	private answered = 0;
	private lost = 0;
	private readonly maxQueue: number;
	private readonly maxBatch: number;

	constructor(
		private readonly everyMs: number = playTiming.everyMilliseconds,
		/** When the log starts: its first batch is due a turn later. */
		now = 0,
		limits: { queue: number; batch: number } = playLimits
	) {
		this.lastTurn = now;
		this.maxQueue = Math.max(1, Math.floor(limits.queue));
		// A batch never holds more than the log does.
		this.maxBatch = Math.max(1, Math.min(this.maxQueue, Math.floor(limits.batch)));
	}

	/** Plays the server has not answered for yet: waiting, and in flight. */
	get queued(): number {
		return this.waiting.length + (this.flight?.length ?? 0);
	}

	/** Plays the server answered for (a 2xx), whatever it did with them. */
	get sent(): number {
		return this.answered;
	}

	/** Plays let go, oldest first, because the log was full. */
	get dropped(): number {
		return this.lost;
	}

	/** A batch is on its way. */
	get sending(): boolean {
		return this.flight !== null;
	}

	/** Sending stopped after a 401, until a good board comes back. */
	get stopped(): boolean {
		return this.halted;
	}

	/** The advertisement on screen now, or null. */
	get playing(): string | null {
		return this.current?.mediaId ?? null;
	}

	/**
	 * An advertisement is on screen in a slot of `slotSeconds`: its play starts now (`serverNow` by the server's clock, `now`
	 * on the monotonic clock). One already on screen ends first, cut short. Nothing starts for an id that is not a GUID, a
	 * server time that is not one, or a slot that is not a positive number of seconds.
	 */
	start(mediaId: string, serverNow: number, now: number, slotSeconds: number): void {
		this.end(false, now);
		if (typeof mediaId !== 'string' || !guid.test(mediaId)) return;
		if (!Number.isFinite(serverNow) || Math.abs(serverNow) > 8.64e15) return;
		if (!Number.isFinite(slotSeconds) || slotSeconds <= 0) return;
		this.current = {
			mediaId,
			startedUtc: serverNow,
			at: now,
			slotMilliseconds: slotSeconds * 1000
		};
	}

	/**
	 * The advertisement on screen has gone: at its slot's end (`slotEnded`) or cut short. Completed only at the slot's end and
	 * when it was on screen for the slot (within the server's tolerance). Null when none was on screen, or when it was on
	 * screen for less than a second (no play: nothing is reported).
	 */
	end(slotEnded: boolean, now: number): Play | null {
		const shown = this.current;
		if (!shown) return null;
		this.current = null;
		const durationMilliseconds = playDuration(shown.at, now);
		if (durationMilliseconds < playLimits.minimumDurationMilliseconds) return null;
		const play: Play = {
			mediaId: shown.mediaId,
			startedUtc: new Date(shown.startedUtc).toISOString(),
			durationMilliseconds,
			completed:
				slotEnded === true &&
				durationMilliseconds >= shown.slotMilliseconds - playLimits.slotToleranceMilliseconds
		};
		this.add(play);
		return play;
	}

	/** Whether a batch should go now: plays waiting, none in flight, not stopped, and its turn has come. */
	due(now: number): boolean {
		if (this.halted || this.flight !== null || this.waiting.length === 0) return false;
		const since = now - this.lastTurn;
		if (since >= this.everyMs) return true;
		return (
			!this.lastFailed &&
			this.waiting.length >= this.maxBatch &&
			since >= playTiming.soonestMilliseconds
		);
	}

	/** The next batch, oldest first, when one is due: it is in flight until `settle`. Null when none is due. */
	take(now: number): Play[] | null {
		if (!this.due(now)) return null;
		this.flight = this.waiting.splice(0, this.maxBatch);
		this.lastTurn = now;
		return this.flight.map((play) => ({ ...play }));
	}

	/** The answer to the batch in flight (`status` null when the request did not complete): what became of its plays. */
	settle(status: number | null | undefined): PlaysOutcome | null {
		const batch = this.flight;
		if (!batch) return null;
		this.flight = null;
		const outcome = outcomeOf(status);
		if (outcome === 'answered') this.answered += batch.length;
		if (outcome === 'unauthorized') this.halted = true;
		// Kept at the front, in their order; `add` held the log to its bound meanwhile, so they fit.
		if (outcome === 'retry') {
			this.waiting.unshift(...batch);
			this.trim();
		}
		this.lastFailed = outcome === 'retry';
		return outcome;
	}

	/** A good board came back: sending starts again after a 401. */
	resume(): void {
		this.halted = false;
	}

	/**
	 * What to send as the page goes (fetch with keepalive, which may not arrive): one batch of the oldest plays, the batch in
	 * flight first, without letting any go (if the page comes back they go again at their turn, and the server counts a play
	 * once). None while sending is stopped.
	 */
	final(): Play[] {
		if (this.halted) return [];
		return [...(this.flight ?? []), ...this.waiting]
			.slice(0, this.maxBatch)
			.map((play) => ({ ...play }));
	}

	private add(play: Play): void {
		// The batch in flight is already sent, so the oldest waiting play goes to make room.
		while (this.waiting.length > 0 && this.queued >= this.maxQueue) {
			this.waiting.shift();
			this.lost++;
		}
		this.waiting.push(play);
		this.trim();
	}

	/** Holds the waiting plays to the bound, the oldest out first. */
	private trim(): void {
		while (this.waiting.length > this.maxQueue) {
			this.waiting.shift();
			this.lost++;
		}
	}
}
