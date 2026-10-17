<script lang="ts">
	import { Clock } from '@lucide/svelte';
	import { _, locale } from 'svelte-i18n';
	import { minuteMs } from '$lib/core/validation';
	import type { ArivaClock } from './arivaClock.svelte';
	import { offsetParts, siteClockSeconds } from './format';

	interface Props {
		clock: ArivaClock;
		/** Ariva's time now (the tablet's clock corrected by the measured offset), updated every second. */
		arivaNow: number;
		timeZone: string;
	}

	let { clock, arivaNow, timeZone }: Props = $props();

	// One card for the whole tablet (ARV-104c1): the line tally and the desk log keep to the clock it describes.
	const offset = $derived(clock.offsetMs);
	const offsetLarge = $derived(offset !== null && Math.abs(offset) > minuteMs);
</script>

<section
	aria-labelledby="ariva-clock-title"
	data-testid="ariva-clock"
	class="mb-4 flex items-start gap-3 rounded-xl border bg-card p-5"
>
	<Clock class="mt-0.5 size-5 shrink-0 text-primary" aria-hidden="true" />
	<div class="min-w-0">
		<h2 id="ariva-clock-title" class="text-base font-semibold">
			{$_('validation.clock.title')}
		</h2>
		{#if !clock.held}
			<!-- Never read, the time shown is the tablet's own and is labelled so (the warning below says why). -->
			<p class="text-sm tabular-nums" data-testid="ariva-clock-time">
				{$_(offset === null ? 'validation.clock.tabletTime' : 'validation.clock.time', {
					values: { time: siteClockSeconds(arivaNow, timeZone, $locale), zone: timeZone }
				})}
			</p>
		{/if}
		{#if clock.state === 'lost' || clock.state === 'implausible'}
			<p
				role="alert"
				class="mt-1 text-sm font-medium text-status-warning-foreground"
				data-testid="ariva-clock-held"
			>
				{$_(clock.state === 'lost' ? 'validation.clock.lost' : 'validation.clock.implausible')}
			</p>
		{:else if offset === null}
			<p class="mt-1 text-sm text-status-warning-foreground" data-testid="ariva-clock-unknown">
				{$_('validation.clock.unknown')}
			</p>
		{:else if offsetLarge}
			<p class="mt-1 text-sm text-status-warning-foreground" data-testid="ariva-clock-off">
				{$_(offset > 0 ? 'validation.clock.tabletBehind' : 'validation.clock.tabletAhead', {
					values: offsetParts(offset)
				})}
			</p>
		{/if}
	</div>
</section>
