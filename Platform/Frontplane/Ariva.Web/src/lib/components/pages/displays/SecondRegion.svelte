<script lang="ts">
	import { onMount } from 'svelte';
	import * as displays from '$lib/core/displays';
	import type { Instructions } from '$lib/core/displays';

	/**
	 * The second region of a split passenger display (ARV-140): instructions in each of the display's languages. It loads
	 * its content on its own, never through the board, so its content failing to load shows a quiet empty panel and the
	 * queue region beside it keeps its share and its data; the player also wraps it in an error boundary. Text only.
	 */
	let {
		languages,
		rtl,
		onloaded
	}: { languages: string[]; rtl: Set<string>; onloaded?: () => void } = $props();

	let content = $state<Record<string, Instructions> | null>(null);
	let failed = $state(false);

	onMount(async () => {
		const loaded = await displays.instructions();
		if (loaded === null) failed = true;
		else content = loaded;
		onloaded?.();
	});

	/** One block per display language; a language without instructions shows the English ones, once. */
	const shown = $derived.by(() => {
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
</script>

<div
	data-testid="board-second"
	data-state={failed ? 'failed' : content ? 'ready' : 'loading'}
	class="flex h-full w-full flex-col justify-center gap-[2em] px-[3em] py-[2em]"
>
	{#each shown as block (block.language)}
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
				{#each block.text.steps as step, index (index)}
					<li>{step}</li>
				{/each}
			</ol>
		</section>
	{/each}
</div>
