<script lang="ts" module>
	export interface ColumnItem {
		id: string;
		code: string;
		name: string;
		/** A short second line (kind, floor, lanes). */
		detail?: string;
		/** Greyed out (a desk out of service). */
		muted?: boolean;
	}
</script>

<script lang="ts">
	import { ChevronRight, Plus } from '@lucide/svelte';
	import type { Snippet } from 'svelte';
	import { _ } from 'svelte-i18n';
	import { cn } from '$lib/utils';

	interface Props {
		id: string;
		title: string;
		items: ColumnItem[];
		total: number;
		selectedId: string | null;
		/** Shown instead of the list when the parent is not chosen yet. */
		waitingFor?: string | null;
		canAdd?: boolean;
		adding?: boolean;
		onAdd?: () => void;
		onSelect: (id: string) => void;
		/** The create form, shown above the list while adding. */
		form?: Snippet;
	}

	let {
		id,
		title,
		items,
		total,
		selectedId,
		waitingFor = null,
		canAdd = false,
		adding = false,
		onAdd,
		onSelect,
		form
	}: Props = $props();
</script>

<!-- One level of the tree (Miller column). Names come from the server and are interpolated as text only (CWE-79). -->
<section
	data-testid="column-{id}"
	aria-labelledby="column-{id}-title"
	class="flex min-w-0 flex-col rounded-xl border bg-card"
>
	<header class="flex items-center justify-between gap-2 border-b px-4 py-3">
		<div class="min-w-0">
			<h2 id="column-{id}-title" class="truncate text-sm font-semibold">{title}</h2>
			{#if !waitingFor}
				<p class="text-xs text-muted-foreground tabular-nums">
					{$_('topology.count', { values: { shown: items.length, total } })}
				</p>
			{/if}
		</div>
		{#if canAdd && !waitingFor}
			<button
				type="button"
				data-testid="add-{id}"
				aria-expanded={adding}
				onclick={() => onAdd?.()}
				class="inline-flex h-8 shrink-0 items-center gap-1 rounded-md border bg-card px-2 text-xs font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
			>
				<Plus class="size-3.5" aria-hidden="true" />
				{$_('topology.actions.add')}
				<span class="sr-only">{title}</span>
			</button>
		{/if}
	</header>
	{#if adding && form}
		<div class="border-b bg-surface-2 p-3">{@render form()}</div>
	{/if}
	{#if waitingFor}
		<p class="p-4 text-sm text-muted-foreground">{waitingFor}</p>
	{:else if items.length === 0}
		<p class="p-4 text-sm text-muted-foreground">{$_('topology.empty')}</p>
	{:else}
		<ul class="sidebar-scroll max-h-[28rem] overflow-y-auto p-1.5">
			{#each items as item (item.id)}
				<li>
					<button
						type="button"
						aria-current={selectedId === item.id ? 'true' : undefined}
						onclick={() => onSelect(item.id)}
						class={cn(
							'group flex w-full items-center gap-2 rounded-md px-2.5 py-2 text-start text-sm transition-colors hover:bg-muted focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none',
							selectedId === item.id && 'bg-primary/10 font-medium dark:bg-button-primary/20',
							item.muted && 'text-muted-foreground'
						)}
					>
						<span class="grid min-w-0 flex-1">
							<span class="truncate"
								><span class="font-mono text-xs tabular-nums">{item.code}</span> {item.name}</span
							>
							{#if item.detail}<span class="truncate text-xs text-muted-foreground"
									>{item.detail}</span
								>{/if}
						</span>
						<ChevronRight
							class="size-4 shrink-0 text-muted-foreground rtl:-scale-x-100"
							aria-hidden="true"
						/>
					</button>
				</li>
			{/each}
		</ul>
	{/if}
</section>
