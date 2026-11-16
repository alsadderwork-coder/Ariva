<script lang="ts">
	import { _ } from 'svelte-i18n';
	import type { Point } from '$lib/core/zones';
	import { cn } from '$lib/utils';
	import type { PlanView } from './plan-view.svelte';

	interface Props {
		view: PlanView;
		/** The floor point under the pointer, in metres, or null to show none. */
		cursor?: Point | null;
		/** Floating over the plan's end corner (the zones editor) or docked in a bar above it (the live plan). */
		docked?: boolean;
	}

	let { view, cursor = null, docked = false }: Props = $props();
</script>

<!-- The pointer position and a scale bar at the end corner of a floor plan (ARV-298, shared by ARV-299a). Decorative:
     the figures they show are on screen as text elsewhere. -->
<div
	class={cn(
		'pointer-events-none flex flex-col items-end gap-1 text-xs text-muted-foreground',
		!docked && 'absolute end-3 bottom-3'
	)}
	aria-hidden="true"
>
	{#if cursor}
		<span data-testid="zone-cursor" class="rounded-md bg-card/90 px-2 py-0.5 tabular-nums shadow-sm"
			>{$_('planView.position', {
				values: { x: cursor.x.toFixed(1), y: cursor.y.toFixed(1) }
			})}</span
		>
	{/if}
	{#if view.bar}
		<span
			data-testid="plan-scale"
			class={cn('flex flex-col items-end rounded-md px-2 py-1', !docked && 'bg-card/90 shadow-sm')}
		>
			<span class="tabular-nums"
				>{$_('planView.scale', { values: { metres: view.bar.metres } })}</span
			>
			<span
				class="h-1.5 border-x-2 border-b-2 border-foreground/70"
				style:width="{view.bar.pixels}px"
			></span>
		</span>
	{/if}
</div>
