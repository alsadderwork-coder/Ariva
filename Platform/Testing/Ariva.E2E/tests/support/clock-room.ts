import { expect, type TestInfo } from '@playwright/test';
import { call } from './accounts';
import { hosts, probes } from './hosts';

// ARV-164b: tests whose reads must fall within one period of a window that moves at each period's start (the line tally's
// quarter-hour bins, the immigration view's last 15 whole minutes) start with room in the period, or they straddle its move.

/** Ariva's clock: the Date header of a host's answer, on a whole second. */
export async function arivaNow(): Promise<number> {
	const ariva = Date.parse((await call('GET', `${hosts.main}${probes[2]}`)).headers()['date'] ?? '');
	expect(Number.isFinite(ariva), "Ariva's clock from a Date header").toBe(true);
	return ariva;
}

/**
 * When less than `room` is left of Ariva's current `period` (milliseconds from the epoch's start, UTC), waits until 3 s into the
 * next period and gives the test that time on top of its own. Three seconds: the Date header stands on a whole second, and a
 * page's own reading of Ariva's clock may differ from it by about half a second plus a round trip.
 */
export async function roomInThePeriod(testInfo: TestInfo, period: number, room: number): Promise<void> {
	expect(room, 'the room fits in the period with the margin').toBeLessThan(period - 3_000);
	const left = period - ((await arivaNow()) % period);
	if (left >= room) return;
	const wait = left + 3_000;
	testInfo.setTimeout(testInfo.timeout + wait);
	await new Promise((resolve) => setTimeout(resolve, wait));
}
