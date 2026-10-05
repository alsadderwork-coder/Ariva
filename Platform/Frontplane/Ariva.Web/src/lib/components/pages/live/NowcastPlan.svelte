<script lang="ts">
	import { _ } from 'svelte-i18n';
	import type { ZoneSnapshot } from '$lib/core/Live.svelte';
	import type { Point } from '$lib/core/zones';
	import { cn } from '$lib/utils';
	import { waitStatus, type WaitStatus } from './waits';

	interface Props {
		width: number;
		depth: number;
		zones: { key: string; name: string; points: Point[] }[];
		snapshots: Record<string, ZoneSnapshot>;
		selected: string | null;
		plan?: { url: string; x: number; y: number; width: number; height: number } | null;
		now: number;
		onSelect: (key: string) => void;
	}

	let { width, depth, zones, snapshots, selected, plan = null, now, onSelect }: Props = $props();

	const size = $derived(Math.max(width, depth));
	const fill: Record<WaitStatus, string> = {
		withinTarget: 'fill-status-success/50 stroke-status-success-solid',
		nearTarget: 'fill-status-warning/55 stroke-status-warning-solid',
		overTarget: 'fill-status-danger/55 stroke-status-danger-solid',
		degraded: 'fill-status-neutral/60 stroke-status-neutral-foreground',
		noEstimate: 'fill-status-neutral/45 stroke-status-neutral-foreground',
		noData: 'fill-status-neutral/30 stroke-status-neutral-foreground'
	};

	function centre(points: Point[]): Point {
		return {
			x: points.reduce((s, p) => s + p.x, 0) / points.length,
			y: points.reduce((s, p) => s + p.y, 0) / points.length
		};
	}
</script>

<!-- Each zone shows its name and wait as text on the plan; the zones table beside it carries the same figures. -->
<svg
	data-testid="nowcast-plan"
	role="group"
	aria-label={$_('liveOperations.planLabel')}
	viewBox="0 0 {width} {depth}"
	class="w-full rounded-lg border bg-surface-2"
>
	<rect x="0" y="0" {width} height={depth} class="fill-card" />
	{#if plan}
		<image
			href={plan.url}
			x={plan.x}
			y={plan.y}
			width={plan.width}
			height={plan.height}
			preserveAspectRatio="none"
			opacity="0.7"
		/>
	{/if}
	{#each zones as zone (zone.key)}
		{@const snapshot = snapshots[zone.key]}
		{@const status = waitStatus(snapshot, now)}
		{@const middle = centre(zone.points)}
		<g data-testid="plan-zone" data-zone={zone.name} data-status={status}>
			<polygon
				points={zone.points.map((p) => `${p.x},${p.y}`).join(' ')}
				class={cn(fill[status], 'cursor-pointer')}
				stroke-width={selected === zone.key ? 3 : 1.5}
				vector-effect="non-scaling-stroke"
				role="button"
				tabindex="-1"
				aria-label={zone.name}
				onpointerdown={() => onSelect(zone.key)}
			>
				<title>{zone.name}</title>
			</polygon>
			<text
				x={middle.x}
				y={middle.y - size / 120}
				font-size={size / 60}
				text-anchor="middle"
				class="pointer-events-none fill-foreground">{zone.name}</text
			>
			<text
				x={middle.x}
				y={middle.y + size / 50}
				font-size={size / 45}
				text-anchor="middle"
				class="pointer-events-none fill-foreground font-semibold tabular-nums"
			>
				{snapshot?.nowcastMinutes != null ? `${Math.round(snapshot.nowcastMinutes)}′` : '·'}
			</text>
		</g>
	{/each}
</svg>
