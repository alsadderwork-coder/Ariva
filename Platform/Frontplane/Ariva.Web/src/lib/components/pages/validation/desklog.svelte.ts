import {
	binMs,
	binOf,
	minuteMs,
	minutesPerBin,
	type CaptureDesk,
	type DeskState
} from '$lib/core/validation';

/**
 * The observer tablet's desk log (ARV-104d, wiki 07 section 8): the observer chooses the desks it watches and taps each
 * desk's state every minute; each 15-minute bin of Ariva's clock is queued as one batch once it has ended, with exactly
 * 15 states per desk (null for a minute not observed). Minutes follow Ariva's clock (ArivaClock in arivaClock.svelte.ts,
 * the Date header of its answers), not the tablet's, so a tablet a minute off still logs the minute Ariva compares.
 */

/** A desk batch to send: one bin of Ariva's clock and, per desk with at least one minute observed, its 15 states. */
export interface DeskPayload {
	binStartMs: number;
	desks: { deskId: string; label: string; states: (DeskState | null)[] }[];
}

/** Why the log last stopped, for the screen: minutes before `sentUntilMs` went out; the minute in progress did not. */
export interface StopNotice {
	sentUntilMs: number;
}

/** A desk's label as the screen and the outbox show it: checkpoint and desk code, both codes from the server. */
export function deskLabel(desk: { checkpoint: string | null; code: string | null }): string {
	return [desk.checkpoint, desk.code].filter((part) => part).join(' ');
}

const emptyMinutes = (): (DeskState | null)[] => Array.from({ length: minutesPerBin }, () => null);

export class DeskLog {
	/** The desks being logged, in the campaign's order; empty when the log is not running. */
	desks = $state<CaptureDesk[]>([]);
	/** The bin in progress (Ariva's clock, UTC milliseconds). */
	binStart = $state(0);
	/** Per desk id, the bin's 15 states. */
	states = $state<Record<string, (DeskState | null)[]>>({});
	/** The bin's minute in progress (0 to 14). */
	current = $state(0);
	/** A minute before this one was sent before a stop (or the log restarted inside the bin): no tap lands there. */
	firstOpen = $state(0);
	/** An earlier minute of the bin the observer chose to fill in; null while taps go to the current minute. */
	selected = $state<number | null>(null);
	stopped = $state<StopNotice | null>(null);
	#sentBefore: { binStart: number; firstOpen: number } | null = null;
	#closed: (payload: DeskPayload) => void;

	constructor(closed: (payload: DeskPayload) => void) {
		this.#closed = closed;
	}

	get logging(): boolean {
		return this.desks.length > 0;
	}

	/** The minute a tap goes to. */
	get target(): number {
		return this.selected ?? this.current;
	}

	/** Starts logging the chosen desks in the bin of Ariva's time `now`; minutes of it already sent stay closed. */
	start(desks: CaptureDesk[], now: number): void {
		if (desks.length === 0) return;
		this.binStart = binOf(now);
		this.states = Object.fromEntries(desks.map((d) => [d.id, emptyMinutes()]));
		this.firstOpen = this.#sentBefore?.binStart === this.binStart ? this.#sentBefore.firstOpen : 0;
		this.selected = null;
		this.stopped = null;
		this.desks = desks;
		this.current = this.#minute(now);
	}

	/** Called every second and before every tap: once the bin has ended on Ariva's clock, queues it and starts the next. */
	tick(now: number): void {
		if (!this.logging) return;
		if (now >= this.binStart + binMs) {
			this.#queue(minutesPerBin);
			this.binStart = binOf(now);
			this.states = Object.fromEntries(this.desks.map((d) => [d.id, emptyMinutes()]));
			this.firstOpen = 0;
			this.selected = null;
			this.#sentBefore = null;
		}
		this.current = this.#minute(now);
		// An earlier minute stays chosen until the observer goes back to now (or the bin ends).
		if (this.selected !== null && this.selected >= this.current) this.selected = null;
	}

	/** Records the state a desk shows in the target minute (the current one, or the earlier one chosen). */
	set(deskId: string, state: DeskState, now: number): void {
		this.tick(now);
		const minute = this.target;
		const row = this.states[deskId];
		if (!row || minute < this.firstOpen || minute > this.current) return;
		row[minute] = state;
	}

	/** Chooses the minute taps go to: an earlier minute of the bin that is still open, or null for now. */
	select(minute: number | null): void {
		if (minute === null || minute === this.current) this.selected = null;
		else if (minute >= this.firstOpen && minute < this.current) this.selected = minute;
	}

	/**
	 * Stops logging: the minutes that have ended are queued now; the minute in progress is not sent (it has not ended).
	 * Without Ariva's time (null: the clock is being read again) the minutes before the last known one are queued.
	 */
	stop(now: number | null): void {
		if (!this.logging) return;
		if (now !== null) this.tick(now);
		const ended = this.current;
		this.#queue(ended);
		this.#sentBefore = { binStart: this.binStart, firstOpen: Math.max(ended, this.firstOpen) };
		this.stopped = { sentUntilMs: this.binStart + ended * minuteMs };
		this.desks = [];
		this.states = {};
		this.selected = null;
	}

	#minute(now: number): number {
		return Math.min(minutesPerBin - 1, Math.max(0, Math.floor((now - this.binStart) / minuteMs)));
	}

	/** Queues the bin's minutes before `ended` (from the first open one), for every desk with at least one observed. */
	#queue(ended: number): void {
		const desks = this.desks
			.map((desk) => ({
				deskId: desk.id,
				label: deskLabel(desk),
				states: (this.states[desk.id] ?? emptyMinutes()).map((state, minute) =>
					minute >= this.firstOpen && minute < ended ? state : null
				)
			}))
			.filter((desk) => desk.states.some((state) => state !== null));
		if (desks.length > 0) this.#closed({ binStartMs: this.binStart, desks });
	}
}
