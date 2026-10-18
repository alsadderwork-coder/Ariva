import { expect, test } from '@playwright/test';
import {
	outcomeOf,
	playDuration,
	playLimits,
	PlayLog,
	playTiming,
	sendEvery,
	type Play
} from '../../../../Frontplane/Ariva.Web/src/lib/core/play-log';

// ARV-144: the passenger display player's proof of play, held to its rules outside the browser (the web project has no unit
// test runner; the module has no imports, so Node runs it as it is). A play starts by the server's clock as the player keeps it
// and lasts the time it was on screen, 1 to 125 seconds (less than a second is no play); it is completed at its slot's end and
// interrupted otherwise. The plays wait in memory, at most 500 with the oldest out first, and go in batches of at most 100
// every minute (sooner when a whole batch waits, never within 12 seconds), one at a time; a 200 lets them go, a 400 or 413 lets them go, a 401 lets them
// go and stops sending until a good board, and no answer, a 429 or a 5xx keeps them for the next full turn.

const minute = playTiming.everyMilliseconds;
const serverStart = Date.parse('2026-10-18T09:30:00.000Z');
const id = (n: number) => `00000000-0000-4000-8000-${n.toString(16).padStart(12, '0')}`;

/** A log whose plays each last a second, started a second apart by the server's clock; `n` plays, ended completed. */
function filled(log: PlayLog, n: number, from = 0): void {
	for (let i = from; i < from + n; i++) {
		log.start(id(i), serverStart + i * 1000, i * 1000);
		log.end(true, i * 1000 + 1000);
	}
}

test('the bounds and the timing are those the server takes', () => {
	expect(playLimits).toEqual({ queue: 500, batch: 100, durationMilliseconds: 125_000, minimumDurationMilliseconds: 1_000 });
	expect(playTiming).toEqual({ everyMilliseconds: 60_000, soonestMilliseconds: 12_000, everySeconds: { min: 12, max: 60 } });
	// Six batches a minute at the soonest: the server's rate per display.
	expect(60_000 / playTiming.soonestMilliseconds).toBeLessThanOrEqual(6);
	expect(60_000 / (playTiming.everySeconds.min * 1000)).toBeLessThanOrEqual(6);
});

test("a play starts by the server's clock, to the millisecond, and lasts the time it was on screen", () => {
	const log = new PlayLog(minute, 0);
	// The server's clock says 09:30:12.345 when the image shows; the page's monotonic clock reads 5,000.25 then.
	log.start(id(1), serverStart + 12_345, 5_000.25);
	expect(log.playing).toBe(id(1));
	const play = log.end(true, 9_987.9);
	expect(play).toEqual({ mediaId: id(1), startedUtc: '2026-10-18T09:30:12.345Z', durationMilliseconds: 4_988, completed: true });
	expect(play!.startedUtc).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/);
	expect(log.playing).toBeNull();
	expect(log.queued).toBe(1);

	// At most 125 seconds, however long it stayed; never negative.
	log.start(id(2), serverStart, 0);
	expect(log.end(true, 400_000)!.durationMilliseconds).toBe(125_000);
	expect(playDuration(10, 5)).toBe(0);
	expect(playDuration(0, Number.NaN)).toBe(0);
	expect(playDuration(0, 124_999.6)).toBe(125_000);
});

test('completed at the slot end, interrupted otherwise; a new play cuts the one on screen short; no play without a start', () => {
	const log = new PlayLog(minute, 0);
	expect(log.end(true, 1_000), 'nothing on screen').toBeNull();

	log.start(id(1), serverStart, 0);
	log.end(true, 5_000);
	log.start(id(2), serverStart + 5_000, 5_000);
	log.end(false, 7_500);
	log.start(id(3), serverStart + 10_000, 10_000);
	// Another advertisement shows before the first went: the first is cut short then.
	log.start(id(4), serverStart + 12_000, 12_000);
	log.end(true, 17_000);
	// Ended twice: one play.
	log.end(true, 18_000);

	const batch = log.take(minute)!;
	expect(batch.map((play) => [play.mediaId, play.durationMilliseconds, play.completed])).toEqual([
		[id(1), 5_000, true],
		[id(2), 2_500, false],
		[id(3), 2_000, false],
		[id(4), 5_000, true]
	]);

	// Nothing starts for an id that is not a GUID, or a server time that is not one.
	const strict = new PlayLog(minute, 0);
	strict.start('../../etc/passwd', serverStart, 0);
	strict.start('<img src=x onerror=alert(1)>', serverStart, 0);
	strict.start(id(5), Number.NaN, 0);
	strict.start(id(6), Number.POSITIVE_INFINITY, 0);
	expect(strict.end(true, 1_000)).toBeNull();
	expect(strict.queued).toBe(0);
});

test('less than a second on screen is no play: nothing is reported, as the server refuses it', () => {
	const log = new PlayLog(minute, 0);
	log.start(id(1), serverStart, 0);
	expect(log.end(false, 999), 'cut short within its first second').toBeNull();
	log.start(id(2), serverStart + 1_000, 1_000);
	// Another advertisement at once cuts it short within the second too.
	log.start(id(3), serverStart + 1_400, 1_400);
	expect(log.end(true, 2_400)!.durationMilliseconds, 'a second exactly is a play').toBe(1_000);
	expect(log.queued).toBe(1);
	expect(log.take(minute)!.map((play) => play.mediaId)).toEqual([id(3)]);
});

test('the queue holds at most 500 plays, the oldest out first, and counts the dropped ones', () => {
	const log = new PlayLog(minute, 0);
	filled(log, 501);
	expect(log.queued).toBe(500);
	expect(log.dropped).toBe(1);
	const first = log.take(10 * minute)!;
	expect(first, 'a batch of at most 100').toHaveLength(100);
	expect(first[0].mediaId, 'the oldest went first').toBe(id(1));
	expect(first.map((play) => play.mediaId)).toEqual(Array.from({ length: 100 }, (_, i) => id(i + 1)));

	// With a batch in flight the bound still holds: the oldest waiting play goes, never one already sent.
	filled(log, 3, 600);
	expect(log.queued).toBe(500);
	expect(log.dropped).toBe(4);
	log.settle(503);
	expect(log.queued, 'kept for the next turn, within the bound').toBe(500);
	const again = log.take(20 * minute)!;
	expect(again[0].mediaId, 'the failed batch first, in its order').toBe(id(1));
	expect(again[99].mediaId).toBe(id(100));

	// A small log keeps its own bound, and a batch never holds more than it does.
	const small = new PlayLog(minute, 0, { queue: 3, batch: 10 });
	filled(small, 5);
	expect(small.queued).toBe(3);
	expect(small.dropped).toBe(2);
	expect(small.take(minute)!.map((play) => play.mediaId)).toEqual([id(2), id(3), id(4)]);
});

test('a batch goes every minute, sooner when a whole batch waits but never within 12 seconds, one request at a time', () => {
	const log = new PlayLog(minute, 1_000);
	filled(log, 1);
	expect(log.due(30_000), 'not before the minute').toBe(false);
	expect(log.take(30_000)).toBeNull();
	expect(log.due(61_000)).toBe(true);
	const one = log.take(61_000)!;
	expect(one).toHaveLength(1);
	expect(log.sending).toBe(true);

	// One request at a time: nothing else goes while the batch is in flight, whatever waits.
	filled(log, 250, 10);
	expect(log.due(10 * minute)).toBe(false);
	expect(log.take(10 * minute)).toBeNull();
	expect(log.settle(200)).toBe('answered');
	expect(log.sent).toBe(1);

	// 250 waiting: a whole batch may go 12 seconds after the last turn, not sooner.
	expect(log.due(61_000 + 11_999)).toBe(false);
	expect(log.take(61_000 + 12_000)).toHaveLength(100);
	log.settle(200);
	expect(log.take(61_000 + 24_000)).toHaveLength(100);
	log.settle(200);
	// 50 left: they wait for the minute.
	expect(log.due(61_000 + 24_000 + 59_000)).toBe(false);
	expect(log.take(61_000 + 24_000 + minute)).toHaveLength(50);
	log.settle(200);
	expect(log.sent).toBe(251);
	expect(log.queued).toBe(0);
	expect(log.due(100 * minute), 'nothing to send').toBe(false);
	expect(log.settle(200), 'no batch in flight').toBeNull();
});

test('what each answer does: 200 lets the batch go, 400 and 413 drop it, 401 drops it and stops, others keep it for the next full turn', () => {
	expect(outcomeOf(200)).toBe('answered');
	expect(outcomeOf(204)).toBe('answered');
	expect(outcomeOf(400)).toBe('unsendable');
	expect(outcomeOf(413)).toBe('unsendable');
	expect(outcomeOf(401)).toBe('unauthorized');
	for (const status of [null, undefined, 0, 403, 404, 408, 429, 500, 502, 503, 504, Number.NaN, 200.5])
		expect(outcomeOf(status as number | null), String(status)).toBe('retry');

	// A 200 lets the plays go, accepted, duplicates and refused alike, and counts them as sent.
	const ok = new PlayLog(minute, 0);
	filled(ok, 3);
	ok.take(minute);
	ok.settle(200);
	expect([ok.queued, ok.sent, ok.dropped]).toEqual([0, 3, 0]);

	// A 400 can never succeed: dropped, not sent.
	const bad = new PlayLog(minute, 0);
	filled(bad, 3);
	bad.take(minute);
	expect(bad.settle(400)).toBe('unsendable');
	expect([bad.queued, bad.sent]).toEqual([0, 0]);
	expect(bad.stopped).toBe(false);

	// No answer, a 429 or a 5xx: kept in order, and tried again only at the next full turn (no tight loop), even with a
	// whole batch waiting.
	for (const status of [null, 429, 500, 503]) {
		const failing = new PlayLog(minute, 0);
		filled(failing, 150);
		const sent = failing.take(minute)!;
		expect(failing.settle(status)).toBe('retry');
		expect(failing.queued).toBe(150);
		expect(failing.sent).toBe(0);
		expect(failing.due(minute + 12_000), `${status}: not at the soonest`).toBe(false);
		expect(failing.due(2 * minute - 1), `${status}: not before the next turn`).toBe(false);
		const retried = failing.take(2 * minute)!;
		expect(retried, `${status}: the same plays again, first`).toEqual(sent);
		// After a good answer, the soonest rule applies again.
		failing.settle(200);
		expect(failing.due(2 * minute + 12_000)).toBe(false);
		filled(failing, 60, 1_000);
		expect(failing.due(2 * minute + 12_000), `${status}: a whole batch waits again`).toBe(true);
	}

	// A 401: dropped, and nothing more goes (not even as the page goes) until a good board comes back.
	const refused = new PlayLog(minute, 0);
	filled(refused, 3);
	refused.take(minute);
	expect(refused.settle(401)).toBe('unauthorized');
	expect(refused.queued).toBe(0);
	expect(refused.stopped).toBe(true);
	filled(refused, 2, 10);
	expect(refused.due(10 * minute)).toBe(false);
	expect(refused.take(10 * minute)).toBeNull();
	expect(refused.final()).toEqual([]);
	refused.resume();
	expect(refused.stopped).toBe(false);
	expect(refused.take(10 * minute)!.map((play) => play.mediaId)).toEqual([id(10), id(11)]);
});

test('as the page goes: one batch of the oldest plays, the batch in flight first, and nothing let go', () => {
	const log = new PlayLog(minute, 0);
	filled(log, 130);
	const flying = log.take(minute)!;
	filled(log, 5, 200);
	const last: Play[] = log.final();
	expect(last).toHaveLength(100);
	expect(last.slice(0, 100)).toEqual(flying);
	expect(log.queued, 'nothing let go').toBe(135);
	log.settle(200);
	const after = log.final();
	expect(after.map((play) => play.mediaId)).toEqual([...Array.from({ length: 30 }, (_, i) => id(i + 100)), ...Array.from({ length: 5 }, (_, i) => id(i + 200))]);
	// A copy: changing it changes nothing in the log.
	after[0].completed = false;
	expect(log.final()[0].completed).toBe(true);
});

test('the playsEvery parameter is whole seconds held to 12 to 60, and ignored otherwise', () => {
	expect(sendEvery(null)).toBe(60_000);
	expect(sendEvery(undefined)).toBe(60_000);
	expect(sendEvery('20')).toBe(20_000);
	expect(sendEvery('12')).toBe(12_000);
	expect(sendEvery('60')).toBe(60_000);
	expect(sendEvery('5'), 'held to the soonest').toBe(12_000);
	expect(sendEvery('0')).toBe(12_000);
	expect(sendEvery('600'), 'held to the minute').toBe(60_000);
	for (const text of ['', ' 20', '20 ', '-5', '20.5', '1e3', '0x10', 'abc', '20s', '<script>', '99999', 'Infinity'])
		expect(sendEvery(text), JSON.stringify(text)).toBe(60_000);
	const log = new PlayLog(sendEvery('20'), 0);
	filled(log, 1);
	expect(log.due(19_999)).toBe(false);
	expect(log.due(20_000)).toBe(true);
});
