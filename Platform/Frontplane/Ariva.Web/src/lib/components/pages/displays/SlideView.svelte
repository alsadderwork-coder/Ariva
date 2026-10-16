<script lang="ts">
	import SlideIcon from './SlideIcon.svelte';

	/**
	 * One instruction slide (ARV-141): its icon and, per language, its title and body, each in its own direction. Used by the
	 * player (the second region, and the pinned slide under the waits) and by the slide screen's preview. Text only.
	 */
	let {
		icon,
		texts,
		rtl,
		compact = false,
		testid = 'slide'
	}: {
		icon: string;
		texts: { language: string; title: string; body: string | null }[];
		rtl: Set<string>;
		compact?: boolean;
		testid?: string;
	} = $props();
</script>

<div
	data-testid={testid}
	class={['flex items-start', compact ? 'gap-[1em]' : 'flex-col justify-center gap-[1.2em]']}
>
	<SlideIcon
		name={icon}
		class={compact ? 'size-[2.6em] shrink-0 text-[#7dd3fc]' : 'size-[4em] shrink-0 text-[#7dd3fc]'}
	/>
	<div class={['flex min-w-0 flex-col', compact ? 'gap-[0.5em]' : 'gap-[1em]']}>
		{#each texts as text (text.language)}
			<section
				dir={rtl.has(text.language) ? 'rtl' : 'ltr'}
				lang={text.language}
				class="flex flex-col gap-[0.3em]"
				data-testid="slide-text"
			>
				<h2 class={['leading-tight font-bold', compact ? 'text-[1.6em]' : 'text-[2.2em]']}>
					{text.title}
				</h2>
				{#if text.body}
					<p class={['leading-snug text-white/85', compact ? 'text-[1.3em]' : 'text-[1.7em]']}>
						{text.body}
					</p>
				{/if}
			</section>
		{/each}
	</div>
</div>
