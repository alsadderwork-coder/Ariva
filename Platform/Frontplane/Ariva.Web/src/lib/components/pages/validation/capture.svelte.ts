import {
	binMs,
	binOf,
	maxRunMs,
	tracerCodePattern,
	utc,
	type CaptureLine,
	type CaptureZone,
	type TracerRunRequest
} from '$lib/core/validation';

/**
 * The observer tablet's two captures (ARV-104c), held by the capture page so they keep running while the observer
 * switches between the line tally and the tracers. Times are the tablet's own clock; tracer batches carry the clock
 * reading so the server measures the offset and corrects the runs, and bins are the server's UTC quarter hours.
 */

/** A bin counts only when the tally ran from its start (this late at most) to its end. */
const startGraceMs = 5_000;
/** A pause between clock ticks longer than this (the tablet slept, the tab was in the background) makes the bin a part bin. */
const gapMs = 30_000;

/** A closed bin to send: the line, the bin and the crossings tallied. */
export interface CountPayload {
	lineId: string;
	lineName: string;
	binStartMs: number;
	crossingsIn: number;
	crossingsOut: number;
}

/** A bin that was not sent, and why: counting started inside it, the tally paused, or the observer stopped. */
export interface SkippedBin {
	binStartMs: number;
	reason: 'part' | 'stopped';
}

export class Tally {
	line = $state<CaptureLine | null>(null);
	binStart = $state(0);
	partial = $state(true);
	crossingsIn = $state(0);
	crossingsOut = $state(0);
	skipped = $state<SkippedBin | null>(null);
	#lastTick = 0;
	#closed: (payload: CountPayload) => void;

	constructor(closed: (payload: CountPayload) => void) {
		this.#closed = closed;
	}

	get counting(): boolean {
		return this.line !== null;
	}

	/** Starts tallying a line; a start after the bin's first seconds makes the bin in progress a part bin, not sent. */
	start(line: CaptureLine, now: number): void {
		this.line = line;
		this.binStart = binOf(now);
		this.partial = now - this.binStart > startGraceMs;
		this.crossingsIn = 0;
		this.crossingsOut = 0;
		this.skipped = null;
		this.#lastTick = now;
	}

	/** Stops tallying; the bin in progress is not complete and is not sent. */
	stop(): void {
		if (this.line) this.skipped = { binStartMs: this.binStart, reason: 'stopped' };
		this.line = null;
	}

	/** Called every second and before every tap: closes the bin once its end has passed and starts the next one. */
	tick(now: number): void {
		if (!this.line) return;
		const paused = now - this.#lastTick > gapMs;
		this.#lastTick = now;
		if (now < this.binStart + binMs) {
			if (paused) this.partial = true;
			return;
		}
		const ended = this.binStart;
		if (this.partial || paused) {
			this.skipped = { binStartMs: ended, reason: 'part' };
		} else {
			this.#closed({
				lineId: this.line.id,
				lineName: this.line.name,
				binStartMs: ended,
				crossingsIn: this.crossingsIn,
				crossingsOut: this.crossingsOut
			});
		}
		this.binStart = binOf(now);
		// After a pause the new bin started while nobody could tap: it is a part bin too.
		this.partial = paused || now - this.binStart > startGraceMs;
		this.crossingsIn = 0;
		this.crossingsOut = 0;
	}

	tap(direction: 'in' | 'out', step: 1 | -1, now: number): void {
		this.tick(now);
		if (!this.line) return;
		if (direction === 'in') this.crossingsIn = Math.max(0, this.crossingsIn + step);
		else this.crossingsOut = Math.max(0, this.crossingsOut + step);
	}
}

/** A tracer in the queue: joined on the tablet's clock, not yet exited. */
export interface ActiveRun {
	id: number;
	code: string;
	zone: CaptureZone;
	joinedMs: number;
}

/** A finished run waiting to be sent, with what the screen shows of it. */
export interface RunPayload {
	run: TracerRunRequest;
	zoneName: string;
}

export type JoinProblem = 'code' | 'zone' | 'active';

export class Tracers {
	active = $state<ActiveRun[]>([]);
	#next = 1;

	/** Normalises a typed code (trimmed, upper case); the pattern decides whether it is a label. */
	static normalise(code: string): string {
		return code.trim().toUpperCase();
	}

	/** Starts a run for a tracer label in a zone; the label must be T-01 to T-999 and not already in a queue. */
	join(code: string, zone: CaptureZone | undefined, now: number): JoinProblem | null {
		const label = Tracers.normalise(code);
		if (!tracerCodePattern.test(label)) return 'code';
		if (!zone) return 'zone';
		if (this.active.some((r) => r.code === label)) return 'active';
		this.active.push({ id: this.#next++, code: label, zone, joinedMs: now });
		return null;
	}

	/** Ends a run (exited, or abandoned when the tracer left without being served) and returns it for sending. */
	finish(id: number, abandoned: boolean, now: number): RunPayload | null {
		const run = this.active.find((r) => r.id === id);
		if (!run || now <= run.joinedMs) return null;
		this.active = this.active.filter((r) => r.id !== id);
		return {
			run: {
				zoneId: run.zone.id,
				tracerCode: run.code,
				joinedUtc: utc(run.joinedMs),
				exitedUtc: utc(now),
				abandoned
			},
			zoneName: run.zone.name
		};
	}

	/** Drops a run started by mistake; nothing is sent. */
	cancel(id: number): void {
		this.active = this.active.filter((r) => r.id !== id);
	}

	/** True when the run is longer than the server accepts (3 hours): it can only be cancelled. */
	static tooLong(run: ActiveRun, now: number): boolean {
		return now - run.joinedMs > maxRunMs;
	}
}
