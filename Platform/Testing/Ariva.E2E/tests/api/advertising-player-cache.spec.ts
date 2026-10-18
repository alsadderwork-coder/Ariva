import { expect, test } from '@playwright/test';
import { cacheLimits, MediaCache, type ObjectUrls } from '../../../../Frontplane/Ariva.Web/src/lib/core/media-cache';

// ARV-143b: the passenger display player's cache of advertisements, held to its bounds outside the browser (the web project
// has no unit test runner; the module has no imports, so Node runs it as it is). The cache keeps at most 40 items and 150 MB,
// the least recently used out first; it never lets the item on screen go and lets nothing go for bytes that would not fit
// anyway; and every item it lets go has its object URL revoked, exactly once, so a kiosk's memory stays bounded.

const MB = 1024 * 1024;

/** Object URLs as a test sees them: every one made, and every one let go. */
function recorder() {
	let next = 0;
	const created: string[] = [];
	const revoked: string[] = [];
	const urls: ObjectUrls = {
		create: () => {
			const url = `blob:test/${++next}`;
			created.push(url);
			return url;
		},
		revoke: (url) => revoked.push(url)
	};
	return { urls, created, revoked, live: () => created.filter((url) => !revoked.includes(url)) };
}

const bytes = (size: number) => new Blob([new Uint8Array(size)]);

test('the cache holds at most 40 items and 150 MB', () => {
	expect(cacheLimits).toEqual({ items: 40, bytes: 150 * MB });
	const record = recorder();
	const cache = new MediaCache(cacheLimits.items, cacheLimits.bytes, record.urls);

	for (let i = 0; i < 41; i++) expect(cache.put(`item-${i}`, bytes(1024))).toBe(true);

	expect(cache.size, 'the 41st let the oldest go').toBe(40);
	expect(cache.has('item-0')).toBe(false);
	expect(cache.has('item-40')).toBe(true);
	expect(record.revoked).toEqual([record.created[0]]);
});

test('the least recently used item goes first, and touching an item makes it recent', () => {
	const record = recorder();
	const cache = new MediaCache(3, 100 * MB, record.urls);
	cache.put('a', bytes(10));
	cache.put('b', bytes(10));
	cache.put('c', bytes(10));

	expect(cache.touch('a')).toBe(true);
	expect(cache.touch('missing')).toBe(false);
	cache.put('d', bytes(10));

	expect(['a', 'b', 'c', 'd'].filter((id) => cache.has(id))).toEqual(['a', 'c', 'd']);
	expect(record.revoked).toEqual([record.created[1]]);
});

test('bytes over the budget let the oldest items go until they fit', () => {
	const record = recorder();
	const cache = new MediaCache(40, 100, record.urls);
	cache.put('a', bytes(40));
	cache.put('b', bytes(40));
	cache.put('c', bytes(10));

	expect(cache.put('d', bytes(60))).toBe(true);

	expect(['a', 'b', 'c', 'd'].filter((id) => cache.has(id))).toEqual(['c', 'd']);
	expect(cache.bytes).toBe(70);
	expect(record.revoked).toEqual(record.created.slice(0, 2));
});

test('the item on screen is never let go, and nothing goes for bytes that would not fit beside it', () => {
	const record = recorder();
	const cache = new MediaCache(40, 100, record.urls);
	cache.put('screen', bytes(70));
	cache.put('other', bytes(20));

	expect(cache.put('next', bytes(40), new Set(['screen'])), 'only letting the item on screen go would make room').toBe(false);
	expect(cache.has('screen')).toBe(true);
	expect(cache.has('other'), 'not let go in vain').toBe(true);
	expect(record.revoked).toEqual([]);

	expect(cache.put('small', bytes(30), new Set(['screen'])), 'room once the other item goes').toBe(true);
	expect(['screen', 'other', 'small'].filter((id) => cache.has(id))).toEqual(['screen', 'small']);

	const full = new MediaCache(2, 100, recorder().urls);
	full.put('screen', bytes(1));
	full.put('also', bytes(1));
	expect(full.put('third', bytes(1), new Set(['screen', 'also'])), 'every place held by what stays').toBe(false);
	expect(full.size).toBe(2);
});

test('an item larger than the whole budget is refused and lets nothing go', () => {
	const record = recorder();
	const cache = new MediaCache(40, 100, record.urls);
	cache.put('a', bytes(50));

	expect(cache.put('huge', bytes(101))).toBe(false);

	expect(cache.has('a')).toBe(true);
	expect(cache.has('huge')).toBe(false);
	expect(record.created).toHaveLength(1);
	expect(record.revoked).toEqual([]);
});

test('every item let go has its URL revoked exactly once: a drop, a new plan and the end of the page', () => {
	const record = recorder();
	const cache = new MediaCache(40, 100 * MB, record.urls);
	for (const id of ['a', 'b', 'c', 'd']) cache.put(id, bytes(10));
	cache.put('a', bytes(20));

	cache.drop('b');
	cache.drop('b');
	cache.retain(new Set(['a', 'c']));
	expect(['a', 'b', 'c', 'd'].filter((id) => cache.has(id))).toEqual(['a', 'c']);
	expect(cache.bytes).toBe(30);
	cache.clear();

	expect(cache.size).toBe(0);
	expect(cache.bytes).toBe(0);
	expect(record.live(), 'no URL outlives the cache').toEqual([]);
	expect(new Set(record.revoked).size, 'each revoked once').toBe(record.revoked.length);
	expect(record.revoked.sort()).toEqual([...record.created].sort());
});
