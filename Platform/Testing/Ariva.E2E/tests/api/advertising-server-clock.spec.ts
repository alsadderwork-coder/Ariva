import { expect, test } from '@playwright/test';
import { BoardClock } from '../../../../Frontplane/Ariva.Web/src/lib/core/board-clock';
import { PlayLog } from '../../../../Frontplane/Ariva.Web/src/lib/core/play-log';

// ARV-197: the passenger display player's clock of the server, held to its rules outside the browser (the web project has no
// unit test runner; board-clock.ts has no imports, so Node runs it as it is). Each good board's serverUtc counts against the
// moment it came on the monotonic clock; the clock is that moment's time plus the largest offset of the boards that came within
// a minute of the newest (64 at most), so a late board never moves it back and a correction of the server's own clock is
// followed at the first board a minute on. A time that is not one changes nothing, and a time far from now gives a finite
// clock that the play log refuses.

const t0 = Date.parse('2026-10-29T10:00:00.000Z');
const iso = (milliseconds: number) => new Date(milliseconds).toISOString();

test('the bounds are a minute and 64 boards', () => {
	expect([BoardClock.windowMs, BoardClock.maxSeen]).toEqual([60_000, 64]);
});

test('before any board there is no clock of the server', () => {
	const clock = new BoardClock();
	expect(clock.at(0)).toBeNaN();
	expect(clock.at(1_000)).toBeNaN();
});

test('a time that is not one changes nothing; a time without its zone is read as UTC', () => {
	const clock = new BoardClock();
	for (const text of [null, undefined, '', 'not a time', '2026-13-45T99:00:00Z', 42 as unknown as string]) clock.observe(text, 1_000);
	expect(clock.at(2_000)).toBeNaN();
	clock.observe(iso(t0), Number.NaN);
	clock.observe(iso(t0), Number.POSITIVE_INFINITY);
	expect(clock.at(2_000), 'a moment that is not a number').toBeNaN();

	clock.observe('2026-10-29T10:00:00', 1_000);
	expect(clock.at(1_000)).toBe(t0);
	expect(clock.at(Number.NaN)).toBeNaN();
});

test('the clock runs on from a board by the monotonic clock', () => {
	const clock = new BoardClock();
	clock.observe(iso(t0), 5_000);
	expect(clock.at(5_000)).toBe(t0);
	expect(clock.at(65_000)).toBe(t0 + 60_000);
	expect(clock.at(3_605_000), 'an hour on, without another board').toBe(t0 + 3_600_000);
});

test('a board that came late does not move the clock back', () => {
	// Boards every 10 seconds, every other one 7 seconds late (the ARV-144 proof of play test's case).
	const clock = new BoardClock();
	clock.observe(iso(t0), 0);
	expect(clock.at(9_999)).toBe(t0 + 9_999);
	clock.observe(iso(t0 + 10_000 - 7_000), 10_000);
	expect(clock.at(10_000)).toBe(t0 + 10_000);
	clock.observe(iso(t0 + 20_000), 20_000);
	clock.observe(iso(t0 + 30_000 - 7_000), 30_000);
	expect(clock.at(30_000)).toBe(t0 + 30_000);
});

test("a board's offset counts for a minute after a newer one, so a correction of the server's clock is followed", () => {
	const clock = new BoardClock();
	clock.observe(iso(t0 + 30_000), 0);
	clock.observe(iso(t0 + 59_999), 59_999);
	expect(clock.at(59_999), 'the ahead board still counts within the minute').toBe(t0 + 30_000 + 59_999);
	clock.observe(iso(t0 + 60_000), 60_000);
	expect(clock.at(60_000), 'a minute after it, it no longer does').toBe(t0 + 60_000);
});

test('at most 64 boards are kept: an early high offset goes once 64 newer ones came', () => {
	const clock = new BoardClock();
	clock.observe(iso(t0 + 5_000), 0);
	for (let i = 1; i <= 63; i++) clock.observe(iso(t0 + i), i);
	expect(clock.at(63)).toBe(t0 + 5_000 + 63);
	clock.observe(iso(t0 + 64), 64);
	expect(clock.at(64)).toBe(t0 + 64);
});

test('a time far from now gives a finite clock that the play log refuses', () => {
	const clock = new BoardClock();
	clock.observe('+275760-09-13T00:00:00.000Z', 0);
	const far = clock.at(1_000);
	expect(Number.isFinite(far)).toBe(true);
	expect(far).toBeGreaterThan(8.64e15);
	const log = new PlayLog(60_000, 0);
	log.start('00000000-0000-4000-8000-000000000001', far, 1_000, 5);
	log.end(true, 6_000);
	expect(log.queued, 'no play by a clock beyond the dates the server takes').toBe(0);
});
