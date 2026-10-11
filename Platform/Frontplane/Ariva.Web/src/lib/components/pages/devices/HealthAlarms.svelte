<script lang="ts">
	import { Activity } from '@lucide/svelte';
	import { onDestroy, untrack } from 'svelte';
	import { _, locale } from 'svelte-i18n';
	import { knownLabels, labelOf } from '$lib/components/pages/live/waits';
	import StatusBadge, { type StatusTone } from '$lib/components/shared/StatusBadge.svelte';
	import { healthChecks, unitOf } from '$lib/core/alertRules';
	import * as operations from '$lib/core/operations';
	import type { Alert } from '$lib/core/operations';

	let { siteCode }: { siteCode: string } = $props();

	/** One page of the site's open alerts, the most the API gives at once. */
	const pageSize = 500;
	const refreshMs = 60_000;

	let alarms = $state<Alert[]>([]);
	let more = $state(false);
	let failed = $state(false);
	let loaded = $state(false);
	let generation = 0;
	let destroyed = false;

	const severityTone: Record<string, StatusTone> = {
		Critical: 'danger',
		Warning: 'warning',
		Info: 'info'
	};
	const numbers = $derived(
		new Intl.NumberFormat($locale?.startsWith('ar') ? 'ar-u-nu-latn' : 'en', {
			maximumFractionDigits: 1
		})
	);

	/** The alarms per queue zone, zones in name order (an alarm without a zone last). */
	const byZone = $derived.by(() => {
		const groups = new Map<string, Alert[]>();
		for (const alarm of alarms) {
			const zone = alarm.zoneName ?? '';
			groups.set(zone, [...(groups.get(zone) ?? []), alarm]);
		}
		return [...groups.entries()].sort(([a], [b]) =>
			a === '' ? 1 : b === '' ? -1 : a < b ? -1 : a > b ? 1 : 0
		);
	});

	/**
	 * The site's open alerts of the three health-check metrics (ARV-114b) that the caller's role is responsible for (the
	 * server decides which alerts a role sees). An answer for a site no longer chosen is dropped.
	 */
	async function load(site: string): Promise<void> {
		const mine = ++generation;
		const result = await operations.openAlerts(site, pageSize);
		if (destroyed || mine !== generation) return;
		failed = result.hasErrors || !result.data;
		const page = result.data;
		alarms = (page?.data ?? []).filter((alert) => healthChecks.has(alert.metric));
		more = !!page && page.totalCount > page.data.length;
		loaded = true;
	}

	$effect(() => {
		const site = siteCode;
		untrack(() => {
			loaded = false;
			alarms = [];
			void load(site);
		});
		const timer = setInterval(() => void load(site), refreshMs);
		return () => clearInterval(timer);
	});

	onDestroy(() => {
		destroyed = true;
	});

	function time(value: string | null | undefined): string {
		return value
			? new Date(value).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
			: '';
	}

	function value(alarm: Alert): string {
		const unit = $_(`alertRules.units.${unitOf(alarm.metric)}`);
		return `${numbers.format(alarm.raisedValue)} ${unit}`.trim();
	}
</script>

<!-- Rule and zone names are text from the server; state and severity are words as well as colours. -->
<section
	class="flex flex-col gap-3 rounded-xl border bg-card p-4"
	aria-labelledby="health-alarms-title"
	data-testid="health-alarms"
>
	<div>
		<h2 id="health-alarms-title" class="flex items-center gap-2 text-base font-semibold">
			<Activity class="size-4" aria-hidden="true" />
			{$_('devices.healthAlarms.title')}
		</h2>
		<p class="text-xs text-muted-foreground">{$_('devices.healthAlarms.description')}</p>
	</div>
	{#if !loaded}
		<p class="text-sm text-muted-foreground" aria-busy="true">
			{$_('liveOperations.quality.loading')}
		</p>
	{:else if failed}
		<p class="text-sm text-status-danger-foreground" data-testid="health-alarms-failed">
			{$_('devices.healthAlarms.failed')}
		</p>
	{:else if byZone.length === 0}
		<p class="text-sm text-muted-foreground" data-testid="health-alarms-none">
			{$_('devices.healthAlarms.none')}
		</p>
	{:else}
		<div class="flex flex-col gap-3">
			{#each byZone as [zone, list] (zone)}
				<div data-testid="health-alarm-zone" data-zone={zone}>
					<h3 class="mb-1.5 text-xs font-semibold tracking-wide text-tertiary uppercase">
						{zone === '' ? $_('devices.healthAlarms.noZone') : zone}
					</h3>
					<ul class="flex flex-col gap-1.5">
						{#each list as alarm (alarm.id)}
							<li
								data-testid="health-alarm"
								data-rule={alarm.ruleCode}
								data-metric={alarm.metric}
								data-state={alarm.state}
								class="flex flex-wrap items-center justify-between gap-2 rounded-lg border px-3 py-2"
							>
								<div class="flex min-w-0 flex-col">
									<span class="text-sm font-medium"
										><span class="font-mono text-xs">{alarm.ruleCode}</span>
										{alarm.ruleName}</span
									>
									<span class="text-xs text-muted-foreground">
										{$_(`alertRules.metrics.${alarm.metric}`)}: {value(alarm)}
										{#if alarm.binStartUtc}
											· {$_('devices.healthAlarms.bin', {
												values: { time: time(alarm.binStartUtc) }
											})}
										{/if}
										· {$_('devices.healthAlarms.raised', {
											values: { time: time(alarm.raisedUtc) }
										})}
									</span>
								</div>
								<span class="flex gap-1.5">
									<StatusBadge
										tone={severityTone[alarm.severity] ?? 'neutral'}
										label={labelOf(
											$_,
											'liveOperations.alerts.severities',
											alarm.severity,
											knownLabels.severities
										)}
									/>
									<StatusBadge
										tone="neutral"
										label={labelOf(
											$_,
											'liveOperations.alerts.states',
											alarm.state,
											knownLabels.alertStates
										)}
									/>
								</span>
							</li>
						{/each}
					</ul>
				</div>
			{/each}
		</div>
		{#if more}
			<p class="text-xs text-muted-foreground" data-testid="health-alarms-more">
				{$_('devices.healthAlarms.more')}
			</p>
		{/if}
	{/if}
</section>
