<script lang="ts">
	import { _ } from 'svelte-i18n';
	import type { Device } from '$lib/core/devices';
	import type { Point } from '$lib/core/zones';
	import { cn } from '$lib/utils';

	interface Props {
		width: number;
		depth: number;
		zones: { id: string; name: string; points: Point[] }[];
		devices: Device[];
		selectedId: string | null;
		plan?: { url: string; x: number; y: number; width: number; height: number } | null;
		onSelect: (id: string) => void;
	}

	let { width, depth, zones, devices, selectedId, plan = null, onSelect }: Props = $props();

	const size = $derived(Math.max(width, depth));
	const tone: Record<string, string> = {
		Online: 'fill-status-success/25 stroke-status-success-solid',
		Commissioning: 'fill-status-info/25 stroke-status-info-solid',
		Degraded: 'fill-status-warning/30 stroke-status-warning-solid',
		Offline: 'fill-status-danger/25 stroke-status-danger-solid',
		Retired: 'fill-status-neutral/30 stroke-status-neutral-foreground'
	};
</script>

<!-- Coverage as the server checks it: a rectangle (length along the orientation) or a circle at the device. Device
     codes and zone names are text (titles and labels); the device list beside the plan is the keyboard path. -->
<svg
	data-testid="coverage-plan"
	role="img"
	aria-label={$_('devices.planLabel')}
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
			opacity="0.8"
		/>
	{/if}
	{#each zones as zone (zone.id)}
		<polygon
			points={zone.points.map((p) => `${p.x},${p.y}`).join(' ')}
			class="fill-status-info/15 stroke-status-info-solid"
			stroke-width="1"
			vector-effect="non-scaling-stroke"
			stroke-dasharray="4 3"
		>
			<title>{zone.name}</title>
		</polygon>
	{/each}
	{#each devices as device (device.id)}
		{@const f = device.footprint}
		{@const selected = device.id === selectedId}
		<g
			data-testid="device-marker"
			data-device={device.code}
			class="cursor-pointer"
			onpointerdown={() => onSelect(device.id)}
			role="presentation"
		>
			<title>{device.code}: {$_(`devices.states.${device.state}`)}</title>
			{#if f.radiusMetres}
				<circle
					cx={device.x}
					cy={device.y}
					r={f.radiusMetres}
					class={tone[device.state]}
					stroke-width={selected ? 3 : 1.5}
					vector-effect="non-scaling-stroke"
				/>
			{:else if f.lengthMetres && f.widthMetres}
				<rect
					x={-f.lengthMetres / 2}
					y={-f.widthMetres / 2}
					width={f.lengthMetres}
					height={f.widthMetres}
					transform="translate({device.x} {device.y}) rotate({device.orientationDegrees})"
					class={tone[device.state]}
					stroke-width={selected ? 3 : 1.5}
					vector-effect="non-scaling-stroke"
					stroke-dasharray={f.source === 'AssumedFromBoq' ? '6 3' : undefined}
				/>
			{/if}
			<circle
				cx={device.x}
				cy={device.y}
				r={size / 160}
				class={cn('fill-foreground', selected && 'fill-primary')}
			/>
			<text
				x={device.x}
				y={device.y - size / 90}
				font-size={size / 65}
				text-anchor="middle"
				class="pointer-events-none fill-foreground font-mono">{device.code}</text
			>
		</g>
	{/each}
</svg>
