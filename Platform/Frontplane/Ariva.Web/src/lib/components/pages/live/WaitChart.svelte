<script lang="ts">
	import { _ } from 'svelte-i18n';
	import { overMinutes } from './waits';

	interface Props {
		zone: string;
		points: { minute: number; wait: number | null }[];
	}

	let { zone, points }: Props = $props();

	const W = 600;
	const H = 180;
	const pad = { left: 36, right: 12, top: 12, bottom: 24 };
	const valid = $derived(
		points.filter((p): p is { minute: number; wait: number } => p.wait !== null)
	);
	const maxWait = $derived(Math.max(overMinutes + 5, ...valid.map((p) => p.wait)));
	const minMinute = $derived(points.length ? points[0].minute : 0);
	const maxMinute = $derived(
		points.length ? Math.max(points[points.length - 1].minute, minMinute + 1) : 1
	);
	const x = (minute: number) =>
		pad.left + ((minute - minMinute) / (maxMinute - minMinute)) * (W - pad.left - pad.right);
	const y = (wait: number) => pad.top + (1 - wait / maxWait) * (H - pad.top - pad.bottom);
	const path = $derived(
		valid
			.map((p, i) => `${i === 0 ? 'M' : 'L'}${x(p.minute).toFixed(1)},${y(p.wait).toFixed(1)}`)
			.join('')
	);
	const ticks = $derived([0, Math.round(maxWait / 2), Math.round(maxWait)]);
	const last = $derived(valid.at(-1));
</script>

<figure class="flex flex-col gap-1" data-testid="wait-chart">
	<figcaption class="text-sm font-semibold">
		{$_('liveOperations.chart.title', { values: { zone } })}
	</figcaption>
	{#if valid.length < 2}
		<p class="text-sm text-muted-foreground">{$_('liveOperations.chart.empty')}</p>
	{:else}
		<svg
			viewBox="0 0 {W} {H}"
			class="w-full"
			role="img"
			aria-label={$_('liveOperations.chart.label', { values: { zone } })}
		>
			{#each ticks as tick (tick)}
				<line
					x1={pad.left}
					x2={W - pad.right}
					y1={y(tick)}
					y2={y(tick)}
					class="stroke-border"
					stroke-width="1"
				/>
				<text
					x={pad.left - 6}
					y={y(tick)}
					font-size="11"
					text-anchor="end"
					dominant-baseline="middle"
					class="fill-muted-foreground tabular-nums">{tick}</text
				>
			{/each}
			<line
				x1={pad.left}
				x2={W - pad.right}
				y1={y(overMinutes)}
				y2={y(overMinutes)}
				class="stroke-status-danger-solid"
				stroke-width="1.5"
				stroke-dasharray="5 4"
			/>
			<path d={path} fill="none" class="stroke-primary dark:stroke-foreground" stroke-width="2" />
			{#if last}<circle
					cx={x(last.minute)}
					cy={y(last.wait)}
					r="3.5"
					class="fill-primary dark:fill-foreground"
				/>{/if}
			<text x={pad.left} y={H - 6} font-size="11" class="fill-muted-foreground"
				>{$_('liveOperations.chart.axis')}</text
			>
		</svg>
		<p class="sr-only">
			{last ? `${zone}: ${Math.round(last.wait)} ${$_('liveOperations.metrics.minutes')}` : ''}
		</p>
	{/if}
</figure>
