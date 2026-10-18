/**
 * The bounded cache of the passenger display player's advertisements (ARV-143b), on its own and without imports so that a
 * test can hold it to its bounds outside the browser (tests/api/advertising-player-cache.spec.ts).
 */

/** The cache never holds more than this: 40 items (a playlist's most) and 150 MB of bytes, least recently used out first. */
export const cacheLimits = { items: 40, bytes: 150 * 1024 * 1024 } as const;

/** How object URLs are made and let go (the browser's, or a test's). */
export interface ObjectUrls {
	create(blob: Blob): string;
	revoke(url: string): void;
}

const browserUrls: ObjectUrls = {
	create: (blob) => URL.createObjectURL(blob),
	revoke: (url) => URL.revokeObjectURL(url)
};

/**
 * The advertisements' bytes in memory only, each as an object URL: at most `maxItems` items and `maxBytes` bytes, least
 * recently used out first (a Map keeps its insertion order, so the first entry is the oldest). Nothing is written to any
 * storage, so the cache never outlives the page; `clear` revokes every URL.
 */
export class MediaCache {
	private readonly entries = new Map<string, { url: string; bytes: number }>();
	private total = 0;

	constructor(
		private readonly maxItems: number = cacheLimits.items,
		private readonly maxBytes: number = cacheLimits.bytes,
		private readonly urls: ObjectUrls = browserUrls
	) {}

	get size(): number {
		return this.entries.size;
	}

	get bytes(): number {
		return this.total;
	}

	has(id: string): boolean {
		return this.entries.has(id);
	}

	/** The item's URL without marking it used. */
	peek(id: string): string | null {
		return this.entries.get(id)?.url ?? null;
	}

	/** Marks the item used now; false when it is not cached. */
	touch(id: string): boolean {
		const entry = this.entries.get(id);
		if (!entry) return false;
		this.entries.delete(id);
		this.entries.set(id, entry);
		return true;
	}

	/**
	 * Keeps the bytes, letting the least recently used items go until they fit, but never one in `keep` (the item on screen).
	 * False when they do not fit (larger than the whole budget, or than what `keep` leaves): the caller plays them from an
	 * object URL of their own and lets it go after the slot.
	 */
	put(id: string, blob: Blob, keep: ReadonlySet<string> = new Set()): boolean {
		if (blob.size > this.maxBytes) return false;
		this.drop(id);
		// What must stay (the item on screen) decides first, so nothing is let go for bytes that would not fit anyway.
		let keptItems = 0;
		let keptBytes = 0;
		for (const [other, entry] of this.entries) {
			if (!keep.has(other)) continue;
			keptItems++;
			keptBytes += entry.bytes;
		}
		if (keptItems >= this.maxItems || keptBytes + blob.size > this.maxBytes) return false;
		for (const [other] of this.entries) {
			if (this.fits(blob.size)) break;
			if (!keep.has(other)) this.drop(other);
		}
		if (!this.fits(blob.size)) return false;
		this.entries.set(id, { url: this.urls.create(blob), bytes: blob.size });
		this.total += blob.size;
		return true;
	}

	/** Lets one item go (its URL revoked). */
	drop(id: string): void {
		const entry = this.entries.get(id);
		if (!entry) return;
		this.entries.delete(id);
		this.total -= entry.bytes;
		this.urls.revoke(entry.url);
	}

	/** Lets go every item not in `ids` (a new plan's advertisements). */
	retain(ids: ReadonlySet<string>): void {
		for (const id of [...this.entries.keys()]) if (!ids.has(id)) this.drop(id);
	}

	/** Lets everything go. */
	clear(): void {
		for (const id of [...this.entries.keys()]) this.drop(id);
	}

	private fits(bytes: number): boolean {
		return this.entries.size < this.maxItems && this.total + bytes <= this.maxBytes;
	}
}
