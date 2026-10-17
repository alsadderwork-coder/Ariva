import type { Result } from '$lib/core/Api';
import { arivaOffsetMs, maxArivaOffsetMs } from '$lib/core/validation';

/**
 * Ariva's clock on the observer tablet (ARV-104d, ARV-104c1; owner decision 2026-10-09: one clock rule for the whole
 * tablet). The line tally's 15-minute bins and the desk log's minutes follow it; tracer batches keep the tablet's own
 * clock reading, which the server measures and corrects. It is read from the HTTP Date header of Ariva's answers
 * (`arivaOffsetMs` in validation.ts).
 */

/**
 * Where the tablet stands with Ariva's clock: never read (no answer carried a readable Date header, as when the web app
 * and Ariva.Api.Main are not on one origin: the tally and the desk log follow the tablet's clock, with a warning), known,
 * lost (the tablet's own clock jumped since it was read: both wait until Ariva answers again) or implausible (the reading
 * is more than 15 minutes from the tablet's clock: both wait until a reading within that comes).
 */
export type ClockState = 'unmeasured' | 'known' | 'lost' | 'implausible';

/** A difference between the tablet's clock and the time passed (monotonic) beyond this is a clock jump or a sleep. */
const clockJumpMs = 5_000;

/** Ariva's clock as the tablet knows it: the offset from the last answer that told it. */
export class ArivaClock {
	offsetMs = $state<number | null>(null);
	state = $state<ClockState>('unmeasured');
	#lastWall = Date.now();
	#lastMono = performance.now();
	#jumped: () => void;

	/** `jumped` runs once per jump of the tablet's own clock, whichever use of the clock finds it first. */
	constructor(jumped: () => void = () => {}) {
		this.#jumped = jumped;
	}

	/**
	 * Reads Ariva's clock from an answer's Date header, when it has one and came quickly. Returns true when this is the
	 * first reading after the screen followed the tablet's own clock and it moves the screen's time by more than 5
	 * seconds: whatever was placed on the tablet's clock (the tally's bin in progress) no longer lines up with Ariva's.
	 */
	observe(result: Result<unknown>): boolean {
		const offset = arivaOffsetMs(result);
		if (offset === null) return false;
		if (Math.abs(offset) > maxArivaOffsetMs) {
			this.offsetMs = null;
			this.state = 'implausible';
			return false;
		}
		const first = this.state === 'unmeasured';
		this.offsetMs = offset;
		this.state = 'known';
		return first && Math.abs(offset) > clockJumpMs;
	}

	/** The tablet's own clock jumped (set by hand or from the network, or the tablet slept): the last reading no longer holds. */
	invalidate(): void {
		if (this.state !== 'known') return;
		this.offsetMs = null;
		this.state = 'lost';
	}

	/**
	 * Compares the tablet's clock with the time that passed (monotonic) since the last check; a jump drops the reading
	 * and runs `jumped`. Every use of Ariva's time checks first (the screen's tick, each tap, a start or a stop), so a tap
	 * in the second after a jump is never placed with the offset the jump made wrong.
	 */
	check(): boolean {
		const wall = Date.now();
		const mono = performance.now();
		const jumped = Math.abs(wall - this.#lastWall - (mono - this.#lastMono)) > clockJumpMs;
		this.#lastWall = wall;
		this.#lastMono = mono;
		if (jumped) {
			this.invalidate();
			this.#jumped();
		}
		return jumped;
	}

	/** True while the tally and the desk log must wait for Ariva's clock: nothing is placed and no bin closes meanwhile. */
	get held(): boolean {
		return this.state === 'lost' || this.state === 'implausible';
	}

	/** Ariva's time for a reading of the tablet's clock (the tablet's own when Ariva's clock was never read). */
	at(deviceMs: number): number {
		return deviceMs + (this.offsetMs ?? 0);
	}

	/** Ariva's time now, after checking for a jump; null while the tally and the desk log wait for Ariva's clock. */
	now(): number | null {
		this.check();
		return this.held ? null : this.at(Date.now());
	}
}
