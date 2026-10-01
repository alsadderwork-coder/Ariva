<script lang="ts">
	import type { Component, Snippet } from 'svelte';
	import { cn } from '$lib/utils';

	interface Props {
		title: string;
		description?: string;
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		icon?: Component<any>;
		actions?: Snippet;
		class?: string;
	}

	let { title, description, icon, actions, class: className = '' }: Props = $props();
</script>

<!-- Aman.Web SimplePageHeader: 44px icon chip, 28px title, 13px description, actions on the end side. -->
<div
	class={cn(
		'mb-5 flex flex-col gap-3 pt-4.5 sm:flex-row sm:items-center sm:justify-between',
		className
	)}
>
	<div class="flex items-center gap-4">
		{#if icon}
			{@const Icon = icon}
			<div
				class="flex size-11 shrink-0 items-center justify-center rounded-[9px] bg-primary/10 text-primary dark:bg-button-primary/20 dark:text-foreground"
			>
				<Icon class="size-5.5" aria-hidden="true" />
			</div>
		{/if}
		<div>
			<h1
				class={cn(
					'text-[28px] leading-none font-semibold tracking-[-0.14px]',
					description && 'mb-1'
				)}
			>
				{title}
			</h1>
			{#if description}<p class="text-[13px] text-secondary-foreground">{description}</p>{/if}
		</div>
	</div>
	{#if actions}
		<div class="flex items-center gap-2">{@render actions()}</div>
	{/if}
</div>
