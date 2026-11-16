<svelte:options namespace="svg" />

<script lang="ts">
	import type { PlanView } from './plan-view.svelte';

	let { view }: { view: PlanView } = $props();
</script>

<!-- The metre grid of a floor plan (ARV-298, shared by ARV-299a): drawn under the shapes, never hit by the pointer. -->
<g class="pointer-events-none" aria-hidden="true">
	{#each view.grid.minor as [x1, y1, x2, y2], i (i)}
		<line
			{x1}
			{y1}
			{x2}
			{y2}
			class="stroke-border"
			stroke-width="0.5"
			vector-effect="non-scaling-stroke"
		/>
	{/each}
	{#each view.grid.major as [x1, y1, x2, y2], i (i)}
		<line
			{x1}
			{y1}
			{x2}
			{y2}
			class="stroke-muted-foreground/30"
			stroke-width="1"
			vector-effect="non-scaling-stroke"
		/>
	{/each}
</g>
