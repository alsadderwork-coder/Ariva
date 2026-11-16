<script lang="ts">
	import type { Component } from 'svelte';
	import { cn } from '$lib/utils';

	interface Props {
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		icon: Component<any>;
		/** What is missing, as the screen said it before ARV-299a. */
		title: string;
		/** One line on what would fill it. */
		hint?: string;
		/** Less room around it, for a panel or a column. */
		compact?: boolean;
		/** On the title, which tests read. */
		testId?: string;
		class?: string;
	}

	let { icon: Icon, title, hint, compact = false, testId, class: className = '' }: Props = $props();
</script>

<!-- An empty list (ARV-299a): an icon chip, what is missing, and what would fill it. Text only, from the dictionaries. -->
<div
	class={cn(
		'flex flex-col items-center justify-center gap-1.5 text-center',
		compact ? 'px-4 py-6' : 'px-6 py-10',
		className
	)}
>
	<span
		class={cn(
			'mb-1 flex items-center justify-center rounded-full bg-primary/10 text-primary',
			compact ? 'size-9' : 'size-11'
		)}
		aria-hidden="true"
	>
		<Icon class={compact ? 'size-4' : 'size-5'} />
	</span>
	<p class="text-sm font-medium text-foreground" data-testid={testId}>{title}</p>
	{#if hint}
		<p class="max-w-sm text-[13px] text-muted-foreground">{hint}</p>
	{/if}
</div>
