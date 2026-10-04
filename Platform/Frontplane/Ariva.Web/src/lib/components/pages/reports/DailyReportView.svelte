<script lang="ts">
	import { BellRing, ChevronDown, ChevronRight, Cpu, Gauge, Timer } from '@lucide/svelte';
	import { _, locale } from 'svelte-i18n';
	import { knownLabels, labelOf, nearMinutes, overMinutes } from '$lib/components/pages/live/waits';
	import MetricCard from '$lib/components/shared/MetricCard.svelte';
	import StatusBadge, { type StatusTone } from '$lib/components/shared/StatusBadge.svelte';
	import type { DailyReport, LaneHour } from '$lib/core/reports';
	import { cn } from '$lib/utils';

	let { report }: { report: DailyReport } = $props();

	const lanesKnown = ['CIT', 'RES', 'VIS', 'CRW', 'EG'];
	let open = $state<Record<string, boolean>>({});

	const isArabic = $derived($locale?.startsWith('ar') ?? false);
	const numbers = $derived(
		new Intl.NumberFormat(isArabic ? 'ar-u-nu-latn' : 'en', { maximumFractionDigits: 1 })
	);
	const whole = $derived(new Intl.NumberFormat(isArabic ? 'ar-u-nu-latn' : 'en'));
	const utc = $derived(
		new Intl.DateTimeFormat(isArabic ? 'ar-u-nu-latn' : 'en-GB', {
			dateStyle: 'medium',
			timeStyle: 'short',
			timeZone: 'UTC'
		})
	);

	/** Every row of the hour strip: the lane's 24 (or 23 or 25) local hours in order. */
	function tone(minutes: number | null): StatusTone {
		if (minutes === null) return 'neutral';
		if (minutes > overMinutes) return 'danger';
		if (minutes >= nearMinutes) return 'warning';
		return 'success';
	}

	const cell: Record<StatusTone, string> = {
		success: 'bg-status-success-solid',
		warning: 'bg-status-warning-solid',
		danger: 'bg-status-danger-solid',
		info: 'bg-status-info-solid',
		neutral: 'bg-muted'
	};

	function minutes(value: number | null | undefined): string {
		return value == null
			? '–'
			: $_('reports.minutes', { values: { value: numbers.format(value) } });
	}

	/** The local time of an alert of the day: its date is the report's. */
	function time(local: string): string {
		return /\d{2}:\d{2}$/.test(local) ? local.slice(-5) : local;
	}

	function count(value: number | null | undefined): string {
		return value == null ? '–' : whole.format(value);
	}

	function laneName(code: string | null): string {
		if (!code) return '';
		return lanesKnown.includes(code) ? $_(`immigration.lanes.${code}`) : code;
	}

	function span(hour: LaneHour | null): string {
		return hour ? $_('reports.hourSpan', { values: { start: hour.start, end: hour.end } }) : '–';
	}

	function hourLabel(hour: LaneHour): string {
		return `${span(hour)}: P90 ${minutes(hour.p90Minutes)}, ${$_('reports.columns.waits')} ${count(hour.waits)}`;
	}

	const severityTone: Record<string, StatusTone> = {
		Critical: 'danger',
		Warning: 'warning',
		Info: 'info'
	};
</script>

<div class="flex flex-col gap-4" data-testid="daily-report">
	<section
		class="grid overflow-hidden rounded-xl border bg-card sm:grid-cols-2 2xl:grid-cols-4 [&>*]:border-b sm:[&>*]:border-e"
		aria-label={$_('reports.headline.label')}
	>
		<MetricCard
			icon={Timer}
			testId="headline-peak"
			label={$_('reports.headline.worstPeak')}
			value={minutes(report.headline.worstPeakP90Minutes)}
			hint={report.headline.worstPeakLane
				? $_('reports.headline.worstPeakHint', {
						values: {
							zone: report.headline.worstPeakLane,
							hour: report.headline.worstPeakHour ?? ''
						}
					})
				: $_('reports.headline.noPeak')}
			tone="bg-status-danger text-status-danger-foreground"
		/>
		<MetricCard
			icon={Gauge}
			testId="headline-above-target"
			label={$_('reports.headline.aboveTarget', { values: { target: overMinutes } })}
			value={count(report.headline.zoneHoursAboveTarget)}
			hint={$_('reports.headline.aboveTargetHint')}
			tone="bg-status-warning text-status-warning-foreground"
		/>
		<MetricCard
			icon={BellRing}
			testId="headline-alerts"
			label={$_('reports.headline.alerts')}
			value={count(report.headline.alerts)}
			hint={$_('reports.headline.criticalHint', {
				values: { count: report.headline.criticalAlerts }
			})}
			tone="bg-status-info text-status-info-foreground"
		/>
		<MetricCard
			icon={Cpu}
			testId="headline-uptime"
			label={$_('reports.headline.lowestUptime')}
			value={report.headline.lowestUptimePercent == null
				? '–'
				: `${numbers.format(report.headline.lowestUptimePercent)}%`}
			hint={$_('reports.headline.uptimeHint')}
		/>
	</section>

	<section class="overflow-hidden rounded-xl border bg-card" aria-labelledby="report-lanes-title">
		<div class="flex flex-wrap items-baseline justify-between gap-2 border-b px-4 py-3">
			<h2 id="report-lanes-title" class="text-base font-semibold">{$_('reports.lanesTitle')}</h2>
			<p class="text-xs text-muted-foreground">
				{$_('reports.stripLegend', { values: { near: nearMinutes, over: overMinutes } })}
			</p>
		</div>
		{#if report.lanes.length === 0}
			<p class="px-4 py-6 text-sm text-muted-foreground">{$_('reports.noLanes')}</p>
		{:else}
			<div class="overflow-x-auto">
				<table class="w-full text-sm" data-testid="report-lanes">
					<thead class="bg-muted/50 text-xs text-muted-foreground">
						<tr>
							<th class="px-4 py-2 text-start font-medium">{$_('reports.columns.zone')}</th>
							<th class="px-4 py-2 text-end font-medium">{$_('reports.columns.passengers')}</th>
							<th class="px-4 py-2 text-end font-medium">{$_('reports.columns.waits')}</th>
							<th class="px-4 py-2 text-end font-medium">P50</th>
							<th class="px-4 py-2 text-end font-medium">P90</th>
							<th class="px-4 py-2 text-start font-medium">{$_('reports.columns.peak')}</th>
							<th class="px-4 py-2 text-end font-medium">{$_('reports.columns.maxQueue')}</th>
							<th class="px-4 py-2 text-start font-medium">{$_('reports.columns.hours')}</th>
						</tr>
					</thead>
					<tbody>
						{#each report.lanes as lane (lane.zone)}
							<tr class="border-t align-middle" data-testid="lane-row" data-zone={lane.zone}>
								<td class="px-4 py-2">
									<button
										type="button"
										data-testid="toggle-hours"
										aria-expanded={!!open[lane.zone]}
										aria-controls="hours-{lane.zone}"
										onclick={() => (open[lane.zone] = !open[lane.zone])}
										class="inline-flex items-center gap-1.5 rounded-md text-start font-medium hover:underline focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
									>
										{#if open[lane.zone]}
											<ChevronDown class="size-4 shrink-0" aria-hidden="true" />
										{:else}
											<ChevronRight class="size-4 shrink-0 rtl:rotate-180" aria-hidden="true" />
										{/if}
										<span class="font-mono text-xs">{lane.zone}</span>
									</button>
									<div
										class="flex flex-wrap items-center gap-1.5 ps-6 text-xs text-muted-foreground"
									>
										{#if lane.laneCategory}<span>{laneName(lane.laneCategory)}</span>{/if}
										{#if lane.provisional}
											<StatusBadge tone="info" label={$_('reports.provisional')} />
										{/if}
									</div>
								</td>
								<td class="px-4 py-2 text-end tabular-nums">{count(lane.passengers)}</td>
								<td class="px-4 py-2 text-end tabular-nums">{count(lane.waits)}</td>
								<td class="px-4 py-2 text-end tabular-nums">{minutes(lane.p50Minutes)}</td>
								<td class="px-4 py-2 text-end tabular-nums" data-testid="lane-p90"
									>{minutes(lane.p90Minutes)}</td
								>
								<td class="px-4 py-2 text-xs tabular-nums" data-testid="lane-peak">
									{#if lane.peak}
										<div>{span(lane.peak)}</div>
										<div class="text-muted-foreground">P90 {minutes(lane.peak.p90Minutes)}</div>
									{:else}
										<span class="text-muted-foreground">{$_('reports.noPeakHour')}</span>
									{/if}
								</td>
								<td class="px-4 py-2 text-end tabular-nums">{count(lane.maxQueueLength)}</td>
								<td class="px-4 py-2">
									<!-- A glance at the day: one cell per local hour by its P90; the hours table below is the full record. -->
									<div class="flex h-5 min-w-48 gap-px" aria-hidden="true">
										{#each lane.hours as hour (hour.start + hour.end)}
											<span
												title={hourLabel(hour)}
												class={cn(
													'flex-1 rounded-[2px]',
													cell[tone(hour.waits > 0 ? hour.p90Minutes : null)],
													hour.provisional && 'opacity-60'
												)}
											></span>
										{/each}
									</div>
								</td>
							</tr>
							{#if open[lane.zone]}
								<tr class="bg-muted/30" id="hours-{lane.zone}">
									<td colspan="8" class="px-4 py-3">
										<table class="w-full text-xs" data-testid="lane-hours">
											<caption class="sr-only"
												>{$_('reports.hoursCaption', { values: { zone: lane.zone } })}</caption
											>
											<thead class="text-muted-foreground">
												<tr>
													<th class="px-2 py-1 text-start font-medium"
														>{$_('reports.columns.hour')}</th
													>
													<th class="px-2 py-1 text-end font-medium"
														>{$_('reports.columns.passengers')}</th
													>
													<th class="px-2 py-1 text-end font-medium"
														>{$_('reports.columns.waits')}</th
													>
													<th class="px-2 py-1 text-end font-medium">P50</th>
													<th class="px-2 py-1 text-end font-medium">P90</th>
													<th class="px-2 py-1 text-end font-medium"
														>{$_('reports.columns.maxQueue')}</th
													>
													<th class="px-2 py-1 text-start font-medium"
														>{$_('reports.columns.status')}</th
													>
												</tr>
											</thead>
											<tbody>
												{#each lane.hours as hour (hour.start + hour.end)}
													<tr class="border-t" data-testid="hour-row" data-start={hour.start}>
														<td class="px-2 py-1 tabular-nums">{span(hour)}</td>
														<td class="px-2 py-1 text-end tabular-nums">{count(hour.passengers)}</td
														>
														<td class="px-2 py-1 text-end tabular-nums">{count(hour.waits)}</td>
														<td class="px-2 py-1 text-end tabular-nums"
															>{minutes(hour.p50Minutes)}</td
														>
														<td class="px-2 py-1 text-end tabular-nums"
															>{minutes(hour.p90Minutes)}</td
														>
														<td class="px-2 py-1 text-end tabular-nums"
															>{count(hour.maxQueueLength)}</td
														>
														<td class="px-2 py-1">
															{#if hour.provisional}{$_('reports.provisional')}{:else}{$_(
																	'reports.final'
																)}{/if}{#if hour.histogramMissing}, {$_(
																	'reports.histogramMissing'
																)}{/if}
														</td>
													</tr>
												{/each}
											</tbody>
										</table>
									</td>
								</tr>
							{/if}
						{/each}
					</tbody>
				</table>
			</div>
		{/if}
	</section>

	<div class="grid gap-4 xl:grid-cols-2">
		<section
			class="overflow-hidden rounded-xl border bg-card"
			aria-labelledby="report-alerts-title"
		>
			<div class="flex flex-wrap items-baseline justify-between gap-2 border-b px-4 py-3">
				<h2 id="report-alerts-title" class="text-base font-semibold">
					{$_('reports.alertsTitle')}
				</h2>
				<p class="text-xs text-muted-foreground">{$_('reports.alertsScope')}</p>
			</div>
			{#if report.alerts.length === 0}
				<p class="px-4 py-6 text-sm text-muted-foreground">{$_('reports.noAlerts')}</p>
			{:else}
				<div class="max-h-[28rem] overflow-auto">
					<table class="w-full text-sm" data-testid="report-alerts">
						<thead class="sticky top-0 bg-muted text-xs text-muted-foreground">
							<tr>
								<th class="px-4 py-2 text-start font-medium">{$_('reports.columns.raised')}</th>
								<th class="px-4 py-2 text-start font-medium">{$_('reports.columns.rule')}</th>
								<th class="px-4 py-2 text-start font-medium">{$_('reports.columns.where')}</th>
								<th class="px-4 py-2 text-start font-medium">{$_('reports.columns.severity')}</th>
								<th class="px-4 py-2 text-end font-medium">{$_('reports.columns.open')}</th>
							</tr>
						</thead>
						<tbody>
							{#each report.alerts as alert, index (index)}
								<tr class="border-t align-top" data-testid="alert-row">
									<td class="px-4 py-2 text-xs tabular-nums" title={alert.raisedLocal}
										>{time(alert.raisedLocal)}</td
									>
									<td class="px-4 py-2">
										<div class="font-mono text-xs">{alert.ruleCode}</div>
										<div class="text-xs text-muted-foreground" data-testid="alert-rule-name">
											{alert.ruleName}
										</div>
									</td>
									<td class="px-4 py-2 text-xs">{alert.zone ?? alert.device ?? ''}</td>
									<td class="px-4 py-2">
										<div class="flex flex-col items-start gap-1">
											<StatusBadge
												tone={severityTone[alert.severity] ?? 'neutral'}
												label={labelOf(
													$_,
													'liveOperations.alerts.severities',
													alert.severity,
													knownLabels.severities
												)}
											/>
											<span class="text-xs text-muted-foreground"
												>{labelOf(
													$_,
													'liveOperations.alerts.states',
													alert.state,
													knownLabels.alertStates
												)}</span
											>
										</div>
									</td>
									<td class="px-4 py-2 text-end text-xs tabular-nums">
										{alert.openMinutes == null
											? $_('reports.stillOpen')
											: minutes(alert.openMinutes)}
									</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
			{/if}
		</section>

		<section
			class="overflow-hidden rounded-xl border bg-card"
			aria-labelledby="report-devices-title"
		>
			<div class="border-b px-4 py-3">
				<h2 id="report-devices-title" class="text-base font-semibold">
					{$_('reports.devicesTitle')}
				</h2>
			</div>
			{#if report.devices.length === 0}
				<p class="px-4 py-6 text-sm text-muted-foreground">{$_('reports.noDevices')}</p>
			{:else}
				<div class="max-h-[28rem] overflow-auto">
					<table class="w-full text-sm" data-testid="report-devices">
						<thead class="sticky top-0 bg-muted text-xs text-muted-foreground">
							<tr>
								<th class="px-4 py-2 text-start font-medium">{$_('reports.columns.device')}</th>
								<th class="px-4 py-2 text-start font-medium">{$_('reports.columns.zone')}</th>
								<th class="px-4 py-2 text-end font-medium">{$_('reports.columns.uptime')}</th>
								<th class="px-4 py-2 text-end font-medium">{$_('reports.columns.outages')}</th>
							</tr>
						</thead>
						<tbody>
							{#each report.devices as device (device.code)}
								<tr class="border-t" data-testid="device-row" data-code={device.code}>
									<td class="px-4 py-2 font-mono text-xs">{device.code}</td>
									<td class="px-4 py-2 text-xs">{device.zone ?? ''}</td>
									<td
										class={cn(
											'px-4 py-2 text-end tabular-nums',
											device.uptimePercent < 99 && 'font-semibold text-status-danger-foreground'
										)}>{numbers.format(device.uptimePercent)}%</td
									>
									<td class="px-4 py-2 text-end text-xs tabular-nums">
										{$_('reports.outageSummary', {
											values: {
												count: device.outages,
												minutes: whole.format(device.outageMinutes)
											}
										})}
									</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
			{/if}
		</section>
	</div>

	<p class="text-xs text-muted-foreground" data-testid="report-footnote">
		{$_('reports.footnote', {
			values: {
				zone: report.timeZoneId,
				generated: utc.format(new Date(report.generatedUtc))
			}
		})}
	</p>
</div>
