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
		onchanged
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

	// Each slot: show the place's item, fetch the next one ahead, and move on at the slot's end.
	$effect(() => {
		const shownSlot = slot;
		if (!key || !running) return;
		const seconds = untrack(() => begin(shownSlot));
		const timer = setTimeout(() => advance(shownSlot), seconds * 1000);
		return () => clearTimeout(timer);
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
		failedSlot = failedAt;
		cache.drop(id);
		if (own?.id === id) own = null;
		cached = cache.size;
		version++;
	}

	onDestroy(() => {
		destroyed = true;
		release();
	});

	/** An image's failure belongs to the slot it was shown in. */
	function picture(node: HTMLImageElement, shown: { slot: number; id: string }) {
		const failure = () => mediaFailed(shown.slot, shown.id);
		node.addEventListener('error', failure);
		return { destroy: () => node.removeEventListener('error', failure) };
	}

	/**
	 * A video plays muted, without controls or loop, from the start of its slot; leaving the slot stops it and lets its
	 * decoder go. A refusal to play (other than being stopped) is a failure like an error.
	 */
	function playback(node: HTMLVideoElement, shown: { slot: number; id: string }) {
		node.defaultMuted = true;
		node.muted = true;
		node.loop = false;
		node.controls = false;
		const failure = () => mediaFailed(shown.slot, shown.id);
		node.addEventListener('error', failure);
		node.play()?.catch((error: unknown) => {
			const name = error instanceof DOMException ? error.name : '';
			if (name === 'NotAllowedError' || name === 'NotSupportedError') failure();
		});
		return {
			destroy: () => {
				node.removeEventListener('error', failure);
				node.pause();
				node.removeAttribute('src');
				node.load();
			}
		};
	}
</script>

<svelte:window onpagehide={() => release()} />

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
