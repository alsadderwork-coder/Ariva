<script lang="ts">
	import { _, locale } from 'svelte-i18n';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import type { OverflowZone } from '$lib/core/operations';
	import { cn } from '$lib/utils';

	interface Props {
		/** The queue zones whose bands this marker stands for (one on the live screen, a lane's queues on the immigration screen). */
		zones: (OverflowZone | undefined)[];
		/** The server's window for a band's latest reading, in minutes. */
		freshMinutes: number;
		/** Classes for the marker's own box (a margin beside other content). */
		class?: string;
	}

	let { zones, freshMinutes, class: className = '' }: Props = $props();

	const numberFormat = $derived(
		new Intl.NumberFormat($locale?.startsWith('ar') ? 'ar-u-nu-latn' : 'en', {
			maximumFractionDigits: 0
		})
	);

	/** Every occupied band of the zones, named with the people it held and when (band names are text). */
	const occupied = $derived(
		zones.flatMap((zone) =>
			zone?.state === 'Occupied' ? zone.bands.filter((band) => band.state === 'Occupied') : []
		)
	);

	function time(value: string | null): string {
		return value
			? new Date(value).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
			: '';
	}

	const detail = $derived(
		[
			$_('liveOperations.overflow.hint', { values: { minutes: freshMinutes } }),
			...occupied.map((band) =>
				$_('liveOperations.overflow.band', {
					values: {
						band: band.band,
						people: numberFormat.format(band.maxOccupancy ?? 0),
						time: time(band.minuteUtc)
					}
				})
			)
		].join(' ')
	);
</script>

<!-- Shown only while a band is occupied; the words carry the state, the colour repeats it. -->
{#if occupied.length > 0}
	<span
		data-testid="overflow-marker"
		data-bands={occupied.map((band) => band.band).join(',')}
		title={detail}
		class={cn('inline-flex', className)}
	>
		<StatusBadge tone="warning" label={$_('liveOperations.overflow.inUse')} />
		<span class="sr-only">{detail}</span>
	</span>
{/if}
