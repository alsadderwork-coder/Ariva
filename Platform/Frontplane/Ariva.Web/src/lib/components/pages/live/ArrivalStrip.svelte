<script lang="ts">
	import { _ } from 'svelte-i18n';
	import type { ArrivalWave } from '$lib/core/operations';

	let { wave }: { wave: ArrivalWave | null } = $props();

	const W = 600;
	const H = 90;
	const minutes = $derived(wave?.minutes ?? []);
	const max = $derived(Math.max(1, ...minutes.map((m) => m.lanes.total)));
	const bar = $derived(minutes.length ? W / minutes.length : W);
</script>

<section
	class="flex flex-col gap-2 rounded-xl border bg-card p-4"
	aria-labelledby="wave-title"
	data-testid="arrival-strip"
>
	<div class="flex flex-wrap items-baseline justify-between gap-2">
		<h2 id="wave-title" class="text-base font-semibold">{$_('liveOperations.wave.title')}</h2>
		{#if wave}
			<span class="text-xs text-muted-foreground tabular-nums" data-testid="wave-window">
				{$_('liveOperations.wave.alertWindow', {
					values: { count: Math.round(wave.alertWindow.total) }
				})}
			</span>
		{/if}
	</div>
	<p class="text-xs text-muted-foreground">
		{$_('liveOperations.wave.description', { values: { minutes: wave?.windowMinutes ?? 60 } })}
	</p>
	{#if !wave || minutes.every((m) => m.lanes.total === 0)}
		<p class="text-sm text-muted-foreground">{$_('liveOperations.wave.none')}</p>
	{:else}
		<svg
			viewBox="0 0 {W} {H}"
			class="w-full"
			role="img"
			aria-label={$_('liveOperations.wave.label')}
		>
			{#each minutes as minute, index (minute.minuteUtc)}
				{@const h = (minute.lanes.total / max) * (H - 4)}
				<rect
					x={index * bar + 0.5}
					y={H - h}
					width={Math.max(bar - 1, 0.5)}
					height={h}
					class={index >= 5 && index < 25 ? 'fill-status-warning-solid' : 'fill-status-info-solid'}
				>
					<title
						>{new Date(minute.minuteUtc).toLocaleTimeString([], {
							hour: '2-digit',
							minute: '2-digit'
						})}: {Math.round(minute.lanes.total)}</title
					>
				</rect>
			{/each}
		</svg>
	{/if}
</section>
