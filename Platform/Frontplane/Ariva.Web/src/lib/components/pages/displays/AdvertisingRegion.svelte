<script lang="ts">
	import { onDestroy, untrack } from 'svelte';
	import * as player from '$lib/core/advertising-player';
	import type { BoardAdvertising, BoardAdvertisingItem, BoardSlide } from '$lib/core/displays';
	import SecondRegion from './SecondRegion.svelte';
	import SlideView from './SlideView.svelte';

	/**
	 * The second region of a split display set to advertising (ARV-143b). It walks the plan's order: place k shows
	 * items[order[k]] for its slot, then the next place, looping; a new board with the same plan keeps the place, a changed
	 * plan starts again at place 0. An advertisement is fetched once (through `load`, which holds the display's credential, so
	 * this component never sees it) into a bounded in-memory cache of object URLs, and the next place's item is fetched while
	 * the current one plays, so it starts on time. An image shows in an img element, a video muted in a video element without
	 * controls or loop, each ended at its slot's end whatever its media holds (a video that ends sooner holds its last frame);
	 * a slide shows as SlideView does. The region shows the display's instructions (SecondRegion, kept loaded) when there is
	 * no plan, the board is stale, the plan has run out by the server's clock or the wait rule pauses it, and for the slot of
	 * an item that fails to load or play (tried again at its next turn). Text only; nothing here builds a URL from the board.
	 *
	 * ARV-144: it tells the page when an advertisement is on screen (`onplaystart`: its image has loaded, or its video plays, in
	 * its slot, with the slot's seconds; never while its bytes are on their way or for a slide) and when it goes (`onplayend`):
	 * at its slot's end (completed when it was on screen for the slot, which the play log judges), or cut short when the wait
	 * rule pauses, the board goes stale, the plan runs out or changes, advertising stops, the item fails after it showed, or
	 * the region or the page goes. An item that fails before it shows is no play. The page keeps the plays and reports them
	 * with the display's credential, which this component never sees.
	 */
	let {
		plan,
		stale,
		paused,
		serverNow,
		languages,
		rtl,
		slides = [],
		load,
		onchanged,
		onplaystart,
		onplayend
	}: {
		/** The board's plan, or null (instructions). */
		plan: BoardAdvertising | null;
		/** No good board for the display's stale threshold. */
		stale: boolean;
		/** The wait rule holds advertising back. */
		paused: boolean;
		/** The server's clock, in milliseconds. */
		serverNow: number;
		languages: string[];
		rtl: Set<string>;
		/** The display's own instruction slides, for the fallback. */
		slides?: BoardSlide[];
		/** Fetches one advertisement with the display's credential; null when it cannot play now. */
		load: (mediaId: string, signal: AbortSignal) => Promise<Blob | null>;
		onchanged?: () => void;
		/** ARV-144: an advertisement is on screen in its slot. */
		onplaystart?: (mediaId: string, slotSeconds: number) => void;
		/** ARV-144: the advertisement on screen has gone: at its slot's end (completed) or cut short. */
		onplayend?: (completed: boolean) => void;
	} = $props();

	type Media = BoardAdvertisingItem & { mediaId: string; contentType: string };

	const cache = new player.MediaCache();
	/** Fetches under way by id, so the item on screen and the one fetched ahead never ask twice. */
	let inflight = new Map<string, Promise<Blob | null>>();
	let controller = new AbortController();
	/** Bumped whenever the plan changes: a fetch that ends after it keeps nothing. */
	let generation = 0;
	/** An advertisement fetched ahead that does not fit the cache, held for its turn only. */
	let ahead: { id: string; blob: Blob } | null = null;
	let playingKey = '';
	let wasRunning = false;
	let destroyed = false;
	/** ARV-144: the advertisement on screen, by its slot, from the moment it shows until it goes. */
	let onScreen: { slot: number; id: string } | null = null;
	/** The last slot a play started in: a slot is one play at most, even when its video plays again after a stall. */
	let playedSlot = -1;

	/** Bumped when the cache changes, so the URL on screen is read again. */
	let version = $state(0);
	let cached = $state(0);
	let place = $state(0);
	/** Counts the slots shown since the page opened: each place played is a new slot, even the same place again. */
	let slot = $state(0);
	let failedSlot = $state(-1);
	/** An item larger than the cache plays from its own URL, revoked after its slot. */
	let own = $state<{ slot: number; id: string; url: string } | null>(null);

	const current = $derived(player.playable(plan));
	const key = $derived(player.planKey(current));
	const expired = $derived(current !== null && player.expired(current, serverNow));
	const running = $derived(current !== null && !stale && !expired && !paused);
	const at = $derived(current ? place % current.order.length : -1);
	const index = $derived(current && at >= 0 ? current.order[at] : -1);
	const item = $derived(current && index >= 0 ? (current.items[index] ?? null) : null);
	const failed = $derived(failedSlot === slot);
	const phase = $derived(
		!current || stale || expired ? 'fallback' : paused ? 'paused' : failed ? 'fallback' : 'playing'
	);
	const reason = $derived(
		!current
			? 'none'
			: stale
				? 'stale'
				: expired
					? 'expired'
					: paused
						? 'wait'
						: failed
							? 'failed'
							: undefined
	);
	const showing = $derived(phase === 'playing');
	const mediaUrl = $derived.by(() => {
		void version;
		if (!player.isPlayableMedia(item)) return null;
		if (own && own.slot === slot && own.id === item.mediaId) return own.url;
		return cache.peek(item.mediaId);
	});
	/** An advertisement whose bytes are still on their way: the instructions show meanwhile, never a blank region. */
	const loading = $derived(showing && player.isPlayableMedia(item) && !mediaUrl);

	/**
	 * Stops every fetch under way and lets go of the cached items the plan does not name (all of them without `keep`): when
	 * the plan changes or stops, and when the page goes.
	 */
	function release(keep: Set<string> = new Set()): void {
		generation++;
		controller.abort();
		controller = new AbortController();
		inflight = new Map();
		ahead = null;
		if (keep.size === 0) cache.clear();
		else cache.retain(keep);
		cached = cache.size;
		version++;
	}

	/** A changed plan starts again at place 0 with the items it names; no plan lets go of everything. */
	function restart(keep: Set<string>): void {
		release(keep);
		own = null;
		place = 0;
		failedSlot = -1;
		slot++;
	}

	// A changed plan restarts; a plan that resumes (after the wait rule, a stale board or its end) starts its slot afresh.
	$effect(() => {
		const next = key;
		const on = running;
		untrack(() => {
			if (next !== playingKey) {
				playingKey = next;
				restart(next ? player.mediaIds(current) : new Set());
			} else if (on && !wasRunning && next) {
				slot++;
			}
			wasRunning = on;
		});
	});

	// Each slot: show the place's item, fetch the next one ahead, and move on at the slot's end. Leaving a slot any other way
	// (the plan stops, pauses or changes, or the region goes) cuts its advertisement's play short (ARV-144).
	$effect(() => {
		const shownSlot = slot;
		if (!key || !running) return;
		const seconds = untrack(() => begin(shownSlot));
		const timer = setTimeout(() => advance(shownSlot), seconds * 1000);
		return () => {
			clearTimeout(timer);
			gone(false, shownSlot);
		};
	});

	// An item larger than the cache lets its URL go once its slot is over (and when the region goes).
	$effect(() => {
		const mine = own;
		return () => {
			if (mine) URL.revokeObjectURL(mine.url);
		};
	});

	// The player fits the region's type again whenever what it shows changes.
	$effect(() => {
		void phase;
		void slot;
		untrack(() => onchanged?.());
	});

	function begin(shownSlot: number): number {
		const shown = item;
		const media = player.isPlayableMedia(shown) ? shown : null;
		if (media) {
			if (cache.touch(media.mediaId)) void prefetch(shownSlot);
			else void fetchForSlot(media, shownSlot);
		} else {
			// A slide without its words, or an item the player cannot show: instructions for its slot.
			if (!(shown?.kind === 'Slide' && shown.slide)) failedSlot = shownSlot;
			void prefetch(shownSlot);
		}
		return player.slotSeconds(shown);
	}

	function advance(fromSlot: number): void {
		if (fromSlot !== slot || !current) return;
		// The slot ran to its end: its advertisement played its whole slot.
		gone(true, fromSlot);
		own = null;
		place = (place + 1) % current.order.length;
		slot++;
	}

	async function fetchForSlot(shown: Media, shownSlot: number): Promise<void> {
		const kept = await keep(shown, shownSlot);
		if (shownSlot !== slot) return;
		if (!kept) failedSlot = shownSlot;
		await prefetch(shownSlot);
	}

	/** Fetches the next place's advertisement while the current item plays. */
	async function prefetch(shownSlot: number): Promise<void> {
		const plan = current;
		if (!plan || shownSlot !== slot) return;
		const next = plan.items[plan.order[(place + 1) % plan.order.length]];
		if (!player.isPlayableMedia(next) || cache.has(next.mediaId) || ahead?.id === next.mediaId)
			return;
		await keep(next, null);
	}

	/**
	 * Fetches an advertisement once and keeps it: in the cache, or, when it is larger than the cache allows, from a URL of its
	 * own for its slot (`shownSlot`), or held for its turn when fetched ahead (null). False when it cannot play now.
	 */
	async function keep(shown: Media, shownSlot: number | null): Promise<boolean> {
		const id = shown.mediaId;
		if (cache.has(id)) return true;
		const started = generation;
		let blob: Blob | null;
		if (ahead?.id === id) {
			blob = ahead.blob;
			ahead = null;
		} else {
			blob = await fetchOnce(id);
		}
		if (destroyed || started !== generation) return false;
		// The same fetch may have been kept already by the slot or the look-ahead that shared it: never replace its URL.
		if (cache.has(id)) return true;
		if (!blob || !player.matchesType(blob, shown.contentType)) return false;
		// The item on screen stays while the next one is kept.
		const onScreen = new Set<string>();
		if (player.isPlayableMedia(item) && item.mediaId !== id) onScreen.add(item.mediaId);
		if (cache.put(id, blob, onScreen)) {
			cached = cache.size;
			version++;
			return true;
		}
		if (shownSlot === null) ahead = { id, blob };
		else own = { slot: shownSlot, id, url: URL.createObjectURL(blob) };
		return true;
	}

	function fetchOnce(id: string): Promise<Blob | null> {
		const pending = inflight.get(id);
		if (pending) return pending;
		const fetches = inflight;
		const fetched: Promise<Blob | null> = load(id, controller.signal)
			.catch(() => null)
			.finally(() => {
				if (fetches.get(id) === fetched) fetches.delete(id);
			});
		fetches.set(id, fetched);
		return fetched;
	}

	/** An image or a video that cannot be shown: instructions for the rest of its slot, and a new fetch at its next turn. */
	function mediaFailed(failedAt: number, id: string): void {
		if (destroyed || failedAt !== slot) return;
		// Failing after it showed cuts its play short; failing before it showed was no play.
		gone(false, failedAt);
		failedSlot = failedAt;
		cache.drop(id);
		if (own?.id === id) own = null;
		cached = cache.size;
		version++;
	}

	onDestroy(() => {
		gone(false);
		destroyed = true;
		release();
	});

	/**
	 * ARV-144: the advertisement of `shown` is on screen (its image loaded, its video playing): its play starts, once per slot,
	 * and only while that slot is the one playing it.
	 */
	function appeared(shown: { slot: number; id: string }): void {
		if (destroyed || shown.slot !== slot || shown.slot === playedSlot || !showing) return;
		if (!player.isPlayableMedia(item) || item.mediaId !== shown.id) return;
		playedSlot = shown.slot;
		// After a plan change the new slot's image may show before the old slot is torn down: the page's play log then ends
		// the old play, cut short, as this one starts (PlayLog.start).
		onScreen = shown;
		onplaystart?.(shown.id, item.seconds);
	}

	/**
	 * ARV-144: the advertisement on screen goes; nothing happens when none is, or when it belongs to another slot than
	 * `fromSlot` (when given). A slot that ends never ends the next slot's play: a browser that already holds an image's bytes
	 * may show it, and so start its play, before the slot that ended is torn down (Chromium 153 does).
	 */
	function gone(completed: boolean, fromSlot?: number): void {
		if (!onScreen) return;
		if (fromSlot !== undefined && onScreen.slot !== fromSlot) return;
		onScreen = null;
		onplayend?.(completed);
	}

	/** An image shows once it has loaded; its failure belongs to the slot it was shown in. */
	function picture(node: HTMLImageElement, shown: { slot: number; id: string }) {
		const failure = () => mediaFailed(shown.slot, shown.id);
		const loaded = () => appeared(shown);
		node.addEventListener('error', failure);
		node.addEventListener('load', loaded);
		if (node.complete && node.naturalWidth > 0) loaded();
		return {
			destroy: () => {
				node.removeEventListener('error', failure);
				node.removeEventListener('load', loaded);
			}
		};
	}

	/**
	 * A video plays muted, without controls or loop, from the start of its slot, and shows once it plays; leaving the slot
	 * stops it and lets its decoder go. A refusal to play (other than being stopped) is a failure like an error.
	 */
	function playback(node: HTMLVideoElement, shown: { slot: number; id: string }) {
		node.defaultMuted = true;
		node.muted = true;
		node.loop = false;
		node.controls = false;
		const failure = () => mediaFailed(shown.slot, shown.id);
		const playing = () => appeared(shown);
		node.addEventListener('error', failure);
		node.addEventListener('playing', playing);
		node.play()?.catch((error: unknown) => {
			const name = error instanceof DOMException ? error.name : '';
			if (name === 'NotAllowedError' || name === 'NotSupportedError') failure();
		});
		return {
			destroy: () => {
				node.removeEventListener('error', failure);
				node.removeEventListener('playing', playing);
				node.pause();
				node.removeAttribute('src');
				node.load();
			}
		};
	}

	/** The page goes: the advertisement on screen is cut short (the page sends what it holds), and everything is let go. */
	function leaving(): void {
		gone(false);
		release();
	}
</script>

<svelte:window onpagehide={leaving} />

<div
	data-testid="board-advertising"
	data-state={phase}
	data-reason={reason}
	data-place={current ? at : undefined}
	data-item={current ? index : undefined}
	data-kind={item?.kind}
	data-slot={slot}
	data-cached={cached}
	data-loading={loading ? 'true' : undefined}
	class="relative h-full w-full"
>
	{#if showing && item}
		{#key slot}
			{#if item.kind === 'Slide' && item.slide}
				<div class="flex h-full w-full flex-col justify-center px-[3em] py-[2em]">
					<SlideView
						icon={item.slide.icon}
						texts={item.slide.texts}
						{rtl}
						testid="board-advertising-slide"
					/>
				</div>
			{:else if mediaUrl && player.isVideo(item)}
				<video
					use:playback={{ slot, id: item.mediaId ?? '' }}
					src={mediaUrl}
					muted
					autoplay
					playsinline
					preload="auto"
					aria-hidden="true"
					data-testid="board-advertising-video"
					data-media-id={item.mediaId}
					class="block h-full w-full bg-black object-contain"
				></video>
			{:else if mediaUrl}
				<img
					use:picture={{ slot, id: item.mediaId ?? '' }}
					src={mediaUrl}
					alt=""
					data-testid="board-advertising-image"
					data-media-id={item.mediaId}
					class="block h-full w-full object-contain"
				/>
			{/if}
		{/key}
	{/if}
	<!-- The instructions stay loaded while advertising plays, so falling back needs no request and works offline. -->
	<div class={['h-full w-full', showing && !loading ? 'hidden' : '']}>
		<SecondRegion {languages} {rtl} {slides} {onchanged} />
	</div>
</div>
