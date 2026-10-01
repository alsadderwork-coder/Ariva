<script lang="ts">
	import type { Component } from 'svelte';
	import { cn } from '$lib/utils';

	interface Props {
		label: string;
		value: string;
		hint?: string;
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		icon: Component<any>;
		/** Tailwind classes for the icon chip, for example "bg-status-info text-status-info-foreground" */
		tone?: string;
		testId?: string;
	}

	let {
		label,
		value,
		hint,
		icon: Icon,
		tone = 'bg-primary/10 text-primary',
		testId
	}: Props = $props();
</script>

<!-- Aman.Web MetricCard: icon chip and label on the start side, a large tabular figure on the end side. -->
<div
	data-testid={testId}
	class="flex flex-1 items-center justify-between gap-4 bg-gradient-to-br from-slate-50/80 to-white/90 p-5 dark:from-slate-800/40 dark:to-slate-900/40"
>
	<div class="flex min-w-0 items-center gap-2">
		<div class={cn('flex size-9 shrink-0 items-center justify-center rounded-md', tone)}>
			<Icon class="size-5" aria-hidden="true" />
		</div>
		<div class="min-w-0">
			<p
				class="line-clamp-2 text-[15px] leading-tight font-semibold text-slate-700 dark:text-slate-300"
			>
				{label}
			</p>
			{#if hint}<p class="truncate text-xs text-muted-foreground">{hint}</p>{/if}
		</div>
	</div>
	<p
		class="shrink-0 text-end text-3xl leading-none font-bold tracking-[-0.6px] text-slate-900 tabular-nums dark:text-slate-100"
	>
		{value}
	</p>
</div>
