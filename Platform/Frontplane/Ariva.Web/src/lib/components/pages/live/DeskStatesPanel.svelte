<script lang="ts">
	import { _ } from 'svelte-i18n';
	import type { DeskStates } from '$lib/core/operations';
	import { cn } from '$lib/utils';
	import { knownLabels, labelOf } from './waits';

	let { states }: { states: DeskStates | null } = $props();

	const tone: Record<string, string> = {
		Serving: 'border-status-success-border bg-status-success text-status-success-foreground',
		Idle: 'border-status-info-border bg-status-info text-status-info-foreground',
		Paused: 'border-status-warning-border bg-status-warning text-status-warning-foreground',
		Closed: 'border-status-neutral-border bg-status-neutral text-status-neutral-foreground',
		Unknown: 'border-dashed border-status-neutral-border bg-card text-muted-foreground'
	};

	const byCheckpoint = $derived.by(() => {
		const groups = new Map<string, NonNullable<typeof states>['desks']>();
		for (const desk of states?.desks ?? [])
			groups.set(desk.checkpoint, [...(groups.get(desk.checkpoint) ?? []), desk]);
		return [...groups.entries()];
	});
</script>

<!-- Every state is a word as well as a colour. Desk codes come from the server and are text. -->
<section
	class="flex flex-col gap-3 rounded-xl border bg-card p-4"
	aria-labelledby="desks-title"
	data-testid="desk-states"
>
	<div>
		<h2 id="desks-title" class="text-base font-semibold">{$_('liveOperations.desks.title')}</h2>
		<p class="text-xs text-muted-foreground">{$_('liveOperations.desks.description')}</p>
	</div>
	{#if states && states.airportIncluded && !states.borderIncluded}
		<p class="text-xs text-muted-foreground" data-testid="border-desks-hidden">
			{$_('liveOperations.desks.borderHidden')}
		</p>
	{/if}
	{#if byCheckpoint.length === 0}
		<p class="text-sm text-muted-foreground">{$_('liveOperations.desks.none')}</p>
	{:else}
		{#each byCheckpoint as [checkpoint, desks] (checkpoint)}
			<div>
				<h3 class="mb-1.5 text-xs font-semibold tracking-wide text-tertiary uppercase">
					{checkpoint}
				</h3>
				<ul class="flex flex-wrap gap-1.5">
					{#each desks as desk (desk.desk)}
						<li
							data-testid="desk-state"
							data-desk={desk.desk}
							data-state={desk.state}
							title={$_('liveOperations.desks.transactions', {
								values: { count: desk.transactions }
							})}
							class={cn(
								'rounded-md border px-2 py-1 text-xs',
								tone[desk.state] ?? tone.Unknown,
								desk.degraded && 'opacity-70'
							)}
						>
							<span class="font-mono font-medium">{desk.desk}</span>
							<span class="ms-1"
								>{labelOf(
									$_,
									'liveOperations.desks.states',
									desk.state,
									knownLabels.deskStates
								)}</span
							>
						</li>
					{/each}
				</ul>
			</div>
		{/each}
	{/if}
</section>
