<script lang="ts">
	import { onMount, untrack } from 'svelte';
	import * as displays from '$lib/core/displays';
	import type { BoardSlide, Instructions } from '$lib/core/displays';
	import SlideView from './SlideView.svelte';

	/**
	 * The second region of a split passenger display. ARV-141: it rotates the instruction slides that apply to the display,
	 * in the site's order, each for its own time on screen; a new board that keeps the same slides keeps the rotation going.
	 * Without slides it shows the default instructions (ARV-140), which it loads on its own, never through the board, so
	 * their failing to load shows a quiet empty panel and the queue region beside it keeps its share and its data; the
	 * player also wraps it in an error boundary. Text only. `onchanged` runs whenever what it shows changes, so the player
	 * fits the region's type again.
	 */
	let {
		languages,
		rtl,
		slides = [],
		onchanged
	}: {
		languages: string[];
		rtl: Set<string>;
		slides?: BoardSlide[];
		onchanged?: () => void;
	} = $props();

	let content = $state<Record<string, Instructions> | null>(null);
	let failed = $state(false);
	let index = $state(0);

	onMount(async () => {
		const loaded = await displays.instructions();
		if (loaded === null) failed = true;
		else content = loaded;
		onchanged?.();
	});

	/** One block per display language; a language without instructions shows the English ones, once. */
	const shownInstructions = $derived.by(() => {
		if (!content) return [];
		const blocks: { language: string; text: Instructions }[] = [];
		for (const wanted of languages) {
			// Own keys only: a language name never resolves to something the object inherits.
			const language = Object.hasOwn(content, wanted) ? wanted : 'en';
			const text = Object.hasOwn(content, language) ? content[language] : undefined;
			if (text && !blocks.some((block) => block.language === language))
				blocks.push({ language, text });
		}
		return blocks;
	});

	/** The rotation changes only when the slides or their times change, not with every board the player fetches. */
	const rotationKey = $derived(slides.map((s) => `${s.position}:${s.durationSeconds}`).join(','));
	const slide = $derived(slides.length > 0 ? slides[index % slides.length] : null);

	$effect(() => {
		const key = rotationKey;
		const at = index;
		if (!key) return;
		const list = untrack(() => slides);
		const current = list[at % list.length];
		const seconds = Math.min(60, Math.max(5, current?.durationSeconds ?? 10));
		const timer = setTimeout(() => (index = (at + 1) % list.length), seconds * 1000);
		return () => clearTimeout(timer);
	});

	$effect(() => {
		void slide?.position;
		void rotationKey;
		untrack(() => onchanged?.());
	});
</script>

{#if slide}
	<div
		data-testid="board-second"
		data-state="slides"
		data-count={slides.length}
		class="flex h-full w-full flex-col justify-center px-[3em] py-[2em]"
	>
		{#key `${slide.position}:${index % slides.length}`}
			<div data-testid="board-slide" data-position={slide.position} data-icon={slide.icon}>
				<SlideView icon={slide.icon} texts={slide.texts} {rtl} />
			</div>
		{/key}
	</div>
{:else}
	<div
		data-testid="board-second"
		data-state={failed ? 'failed' : content ? 'ready' : 'loading'}
		class="flex h-full w-full flex-col justify-center gap-[2em] px-[3em] py-[2em]"
	>
		{#each shownInstructions as block (block.language)}
			<section
				dir={rtl.has(block.language) ? 'rtl' : 'ltr'}
				lang={block.language}
				class="flex flex-col gap-[0.6em]"
				data-testid="board-instructions"
			>
				<h2 class="text-[2.2em] leading-tight font-bold">{block.text.title}</h2>
				<ol
					class="flex list-decimal flex-col gap-[0.4em] ps-[1.2em] text-[1.7em] leading-snug text-white/85"
				>
					{#each block.text.steps as step, stepIndex (stepIndex)}
						<li>{step}</li>
					{/each}
				</ol>
			</section>
		{/each}
	</div>
{/if}
