<script lang="ts">
	import { Activity, Clock, Radar, TriangleAlert, Users } from '@lucide/svelte';
	import { _, locale } from 'svelte-i18n';
	import DemoDataBanner from '$lib/components/shared/DemoDataBanner.svelte';
	import MetricCard from '$lib/components/shared/MetricCard.svelte';
	import SimplePageHeader from '$lib/components/shared/SimplePageHeader.svelte';
	import StatusBadge, { type StatusTone } from '$lib/components/shared/StatusBadge.svelte';
	import { demoSensors, demoZones, zoneStatus, type ZoneStatus } from '$lib/demo/live-operations';

	const isArabic = $derived($locale?.startsWith('ar') ?? false);
	// Latin digits in both languages, as on the operations floor displays.
	const numberFormat = $derived(new Intl.NumberFormat(isArabic ? 'ar-u-nu-latn' : 'en'));

	const statusTone: Record<ZoneStatus, StatusTone> = {
		withinTarget: 'success',
		nearTarget: 'warning',
		overTarget: 'danger',
		degraded: 'neutral'
	};

	const rows = $derived(demoZones.map((zone) => ({ zone, status: zoneStatus(zone) })));
	const waiting = $derived(demoZones.reduce((sum, zone) => sum + zone.inQueue, 0));
	const longestWait = $derived(Math.max(...demoZones.map((zone) => zone.waitMinutes ?? 0)));
	const overTarget = $derived(rows.filter((row) => row.status === 'overTarget').length);

	function waitShare(wait: number | null, target: number): number {
		return wait === null ? 0 : Math.min(100, Math.round((wait / target) * 100));
	}
</script>

<svelte:head>
	<title>{$_('app.title')}</title>
</svelte:head>

<SimplePageHeader
	icon={Activity}
	title={$_('liveOperations.title')}
	description={$_('liveOperations.description')}
/>

<DemoDataBanner text={$_('liveOperations.demoNotice')} />

<section
	aria-label={$_('liveOperations.metrics.label')}
	class="mb-4 grid overflow-hidden rounded-xl border bg-card sm:grid-cols-2 xl:grid-cols-4 [&>*]:border-b sm:[&>*]:border-e xl:[&>*]:border-b-0"
>
	<MetricCard
		testId="metric-waiting"
		icon={Users}
		label={$_('liveOperations.metrics.waiting')}
		value={numberFormat.format(waiting)}
		tone="bg-status-info text-status-info-foreground"
	/>
	<MetricCard
		testId="metric-longest"
		icon={Clock}
		label={$_('liveOperations.metrics.longestWait')}
		hint={$_('liveOperations.metrics.minutes')}
		value={numberFormat.format(longestWait)}
		tone="bg-status-warning text-status-warning-foreground"
	/>
	<MetricCard
		testId="metric-over-target"
		icon={TriangleAlert}
		label={$_('liveOperations.metrics.overTarget')}
		value={numberFormat.format(overTarget)}
		tone="bg-status-danger text-status-danger-foreground"
	/>
	<MetricCard
		testId="metric-sensors"
		icon={Radar}
		label={$_('liveOperations.metrics.sensorsOnline')}
		value={`${numberFormat.format(demoSensors.online)}/${numberFormat.format(demoSensors.total)}`}
		tone="bg-status-success text-status-success-foreground"
	/>
</section>

<section aria-labelledby="zones-heading" class="rounded-xl border bg-card p-5">
	<div class="mb-4 flex flex-col gap-1 sm:flex-row sm:items-end sm:justify-between">
		<div>
			<h2 id="zones-heading" class="text-xl font-semibold">{$_('liveOperations.zones.title')}</h2>
			<p class="text-[13px] text-secondary-foreground">{$_('liveOperations.zones.description')}</p>
		</div>
	</div>

	<div class="overflow-x-auto rounded-lg border">
		<table data-testid="zones-table" class="w-full min-w-[760px] text-sm">
			<thead class="bg-surface-2">
				<tr>
					<th
						scope="col"
						class="p-4 text-start text-xs font-semibold tracking-wide text-tertiary uppercase"
						>{$_('liveOperations.zones.columns.zone')}</th
					>
					<th
						scope="col"
						class="p-4 text-start text-xs font-semibold tracking-wide text-tertiary uppercase"
						>{$_('liveOperations.zones.columns.area')}</th
					>
					<th
						scope="col"
						class="p-4 text-end text-xs font-semibold tracking-wide text-tertiary uppercase"
						>{$_('liveOperations.zones.columns.inQueue')}</th
					>
					<th
						scope="col"
						class="p-4 text-start text-xs font-semibold tracking-wide text-tertiary uppercase"
						>{$_('liveOperations.zones.columns.wait')}</th
					>
					<th
						scope="col"
						class="p-4 text-end text-xs font-semibold tracking-wide text-tertiary uppercase"
						>{$_('liveOperations.zones.columns.serversOpen')}</th
					>
					<th
						scope="col"
						class="p-4 text-start text-xs font-semibold tracking-wide text-tertiary uppercase"
						>{$_('liveOperations.zones.columns.status')}</th
					>
				</tr>
			</thead>
			<tbody>
				{#each rows as { zone, status } (zone.id)}
					<tr
						data-testid="zone-row"
						class="border-t odd:bg-surface even:bg-surface-2/50 hover:bg-muted"
					>
						<td class="px-4 py-3 font-medium">{isArabic ? zone.name.ar : zone.name.en}</td>
						<td class="px-4 py-3 text-muted-foreground"
							>{$_(`liveOperations.areas.${zone.area}`)}</td
						>
						<td class="px-4 py-3 text-end tabular-nums">{numberFormat.format(zone.inQueue)}</td>
						<td class="px-4 py-3">
							{#if zone.waitMinutes === null}
								<span class="text-muted-foreground">{$_('liveOperations.zones.noEstimate')}</span>
							{:else}
								<div class="flex items-center gap-3">
									<span class="w-16 tabular-nums">
										{$_('liveOperations.zones.waitOfTarget', {
											values: {
												wait: numberFormat.format(zone.waitMinutes),
												target: numberFormat.format(zone.targetMinutes)
											}
										})}
									</span>
									<div class="h-1.5 w-24 overflow-hidden rounded-full bg-muted" aria-hidden="true">
										<div
											class="h-full rounded-full bg-status-info-solid data-[status=nearTarget]:bg-status-warning-solid data-[status=overTarget]:bg-status-danger-solid"
											data-status={status}
											style:width={`${waitShare(zone.waitMinutes, zone.targetMinutes)}%`}
										></div>
									</div>
								</div>
							{/if}
						</td>
						<td class="px-4 py-3 text-end tabular-nums"
							>{numberFormat.format(zone.serversOpen)}/{numberFormat.format(zone.serversTotal)}</td
						>
						<td class="px-4 py-3"
							><StatusBadge
								tone={statusTone[status]}
								label={$_(`liveOperations.status.${status}`)}
							/></td
						>
					</tr>
				{/each}
			</tbody>
		</table>
	</div>
</section>
