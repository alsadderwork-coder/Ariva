<script lang="ts">
	import { Activity, BellRing, Clock, Radio, Users } from '@lucide/svelte';
	import { onDestroy, onMount } from 'svelte';
	import { _, locale } from 'svelte-i18n';
	import AlertsPanel from '$lib/components/pages/live/AlertsPanel.svelte';
	import ArrivalStrip from '$lib/components/pages/live/ArrivalStrip.svelte';
	import DeskStatesPanel from '$lib/components/pages/live/DeskStatesPanel.svelte';
	import NowcastPlan from '$lib/components/pages/live/NowcastPlan.svelte';
	import WaitChart from '$lib/components/pages/live/WaitChart.svelte';
	import {
		knownLabels,
		labelOf,
		nearMinutes,
		overMinutes,
		statusTone,
		waitStatus,
		estimateOnly
	} from '$lib/components/pages/live/waits';
	import MetricCard from '$lib/components/shared/MetricCard.svelte';
	import SimplePageHeader from '$lib/components/shared/SimplePageHeader.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { auth } from '$lib/core/auth.svelte';
	import {
		isStale,
		LiveConnection,
		staleAfterSeconds,
		type AlertNotice,
		type ZoneSnapshot
	} from '$lib/core/Live.svelte';
	import * as operations from '$lib/core/operations';
	import type { Alert, ArrivalWave, DeskStates } from '$lib/core/operations';
	import * as topology from '$lib/core/topology';
	import type { Level, Site } from '$lib/core/topology';
	import * as zonesApi from '$lib/core/zones';
	import type { Zone } from '$lib/core/zones';
	import { cn } from '$lib/utils';

	interface QueueZone {
		key: string;
		name: string;
		levelId: string;
		points: zonesApi.Point[];
	}

	let siteList = $state<Site[]>([]);
	let siteCode = $state('');
	let levels = $state<Level[]>([]);
	let levelId = $state('');
	let queueZones = $state<QueueZone[]>([]);
	let snapshots = $state<Record<string, ZoneSnapshot>>({});
	let history = $state<Record<string, { minute: number; wait: number | null }[]>>({});
	let selectedKey = $state<string | null>(null);
	let alerts = $state<Alert[]>([]);
	let desks = $state<DeskStates | null>(null);
	let wave = $state<ArrivalWave | null>(null);
	let plan = $state<zonesApi.PlanImage | null>(null);
	/** The site's floor plans by level (asked once per site). */
	let plans: Record<string, zonesApi.FloorPlan> = {};
	let now = $state(Date.now());
	let destroyed = false;
	let timers: ReturnType<typeof setInterval>[] = [];
	let alertReload: ReturnType<typeof setTimeout> | undefined;

	const live = new LiveConnection(receive, notice);

	const isArabic = $derived($locale?.startsWith('ar') ?? false);
	// Latin digits in both languages, as on the operations floor displays.
	const numberFormat = $derived(
		new Intl.NumberFormat(isArabic ? 'ar-u-nu-latn' : 'en', { maximumFractionDigits: 1 })
	);
	const level = $derived(levels.find((l) => l.id === levelId) ?? null);
	const levelZones = $derived(queueZones.filter((z) => z.levelId === levelId));
	const fresh = $derived(
		queueZones
			.map((z) => snapshots[z.key])
			.filter((s): s is ZoneSnapshot => !!s && !isStale(s, now))
	);
	const waiting = $derived(fresh.reduce((sum, s) => sum + s.queueLength, 0));
	const longest = $derived(Math.max(0, ...fresh.map((s) => s.nowcastMinutes ?? 0)));
	const selected = $derived(queueZones.find((z) => z.key === selectedKey) ?? null);

	function receive(snapshot: ZoneSnapshot): void {
		if (!queueZones.some((z) => z.key === snapshot.zoneKey)) return;
		snapshots[snapshot.zoneKey] = snapshot;
		const minute = Date.parse(snapshot.minuteUtc);
		const series = history[snapshot.zoneKey] ?? [];
		if (Number.isFinite(minute) && !series.some((p) => p.minute === minute)) {
			history[snapshot.zoneKey] = [...series, { minute, wait: snapshot.nowcastMinutes }]
				.sort((a, b) => a.minute - b.minute)
				.slice(-60);
		}
	}

	/** An alert changed: read the alerts again (once for a burst of notices). */
	function notice(change: AlertNotice): void {
		if (change.siteCode !== siteCode) return;
		clearTimeout(alertReload);
		alertReload = setTimeout(() => void loadAlerts(), 300);
	}

	async function loadAlerts(): Promise<void> {
		if (!siteCode) return;
		const result = await operations.openAlerts(siteCode);
		if (!result.hasErrors) alerts = result.data?.data ?? [];
	}

	/** Desk states are shown to roles that may see airport or border desks (ARV-055); a handler's own counters come later. */
	const seesDesks = $derived(auth.can('AirportDesks.View') || auth.can('BorderDesks.View'));

	async function loadDesks(): Promise<void> {
		if (!siteCode || !seesDesks) return;
		const result = await operations.deskStates(siteCode);
		desks = result.hasErrors ? null : result.data;
	}

	async function loadWave(): Promise<void> {
		if (!siteCode || !auth.can('ArrivalWave.View')) return;
		const result = await operations.arrivalWave(siteCode, 60);
		wave = result.hasErrors ? null : result.data;
	}

	function dropPlan(): void {
		if (plan) URL.revokeObjectURL(plan.url);
		plan = null;
	}

	async function loadPlan(): Promise<void> {
		dropPlan();
		const target = levelId;
		const meta = target ? plans[target] : undefined;
		if (!meta) return;
		const image = await zonesApi.planImage(meta, () => target === levelId && !destroyed);
		if (!image) return;
		dropPlan();
		plan = image;
	}

	/** The site's queue zones from its published zone profile: what the hub streams and the plan shows. */
	async function loadSite(): Promise<void> {
		plans = await zonesApi.floorPlans(siteCode);
		snapshots = {};
		history = {};
		selectedKey = null;
		const [levelResult, profiles] = await Promise.all([
			topology.search<Level>('level', { siteCode }),
			zonesApi.history(siteCode)
		]);
		levels = levelResult.data?.data ?? [];
		const published = (profiles.data ?? []).find((p) => p.status === 'Published');
		const zones: Zone[] = published ? ((await zonesApi.get(published.id)).data?.zones ?? []) : [];
		queueZones = zones
			.filter((z) => z.kind === 'Queue')
			.map((z) => ({
				key: `${siteCode}/${z.name}`,
				name: z.name,
				levelId: z.levelId,
				points: zonesApi.parsePolygon(z.polygon) ?? []
			}));
		const counts = new Map<string, number>();
		for (const zone of queueZones) counts.set(zone.levelId, (counts.get(zone.levelId) ?? 0) + 1);
		levelId = [...counts.entries()].sort((a, b) => b[1] - a[1])[0]?.[0] ?? levels[0]?.id ?? '';
		selectedKey = queueZones[0]?.key ?? null;
		// The screen may have been left while this loaded: open nothing more.
		if (destroyed) return;
		await Promise.all([
			live.watch(
				queueZones.map((z) => z.key),
				auth.can('Alert.View') ? siteCode : null
			),
			loadAlerts(),
			loadDesks(),
			loadWave(),
			loadPlan()
		]);
	}

	/** The live screen is for roles with LiveQueue.View; anyone else (a user without a role yet) is asked for nothing. */
	const canSee = $derived(auth.can('LiveQueue.View'));

	onMount(async () => {
		if (!canSee) return;
		const result = await topology.sites();
		siteList = result.data ?? [];
		siteCode = siteList.find((s) => s.code === 'DMO')?.code ?? siteList[0]?.code ?? '';
		if (siteCode) await loadSite();
		if (destroyed) return;
		timers = [
			setInterval(() => (now = Date.now()), 10_000),
			setInterval(() => void loadDesks(), 60_000),
			setInterval(() => void loadWave(), 60_000),
			setInterval(() => void loadAlerts(), 60_000)
		];
	});

	onDestroy(() => {
		destroyed = true;
		for (const timer of timers) clearInterval(timer);
		clearTimeout(alertReload);
		dropPlan();
		void live.stop();
	});

	function updated(snapshot: ZoneSnapshot | undefined): string {
		return snapshot
			? new Date(snapshot.minuteUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
			: '';
	}
</script>

<svelte:head>
	<title>{$_('app.title')}</title>
</svelte:head>

<SimplePageHeader
	icon={Activity}
	title={$_('liveOperations.title')}
	description={$_('liveOperations.description')}
>
	{#snippet actions()}
		{#if canSee}
			<span
				data-testid="live-state"
				data-state={live.state}
				class={cn(
					'inline-flex h-8 items-center gap-1.5 rounded-full border px-3 text-xs font-medium',
					live.state === 'connected'
						? 'border-status-success-border bg-status-success text-status-success-foreground'
						: 'border-status-warning-border bg-status-warning text-status-warning-foreground'
				)}
			>
				<Radio class="size-3.5" aria-hidden="true" />
				{$_(`liveOperations.connection.${live.state}`)}
			</span>
			<label for="live-site" class="sr-only">{$_('topology.site')}</label>
			<select
				id="live-site"
				bind:value={siteCode}
				onchange={loadSite}
				class="h-10 rounded-lg border border-input bg-card px-3 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
			>
				{#each siteList as site (site.code)}
					<option value={site.code}
						>{site.name && site.name !== site.code
							? `${site.code}, ${site.name}`
							: site.code}</option
					>
				{/each}
			</select>
		{/if}
	{/snippet}
</SimplePageHeader>

{#if !canSee}
	<p
		class="rounded-xl border bg-card p-4 text-sm text-muted-foreground"
		data-testid="no-live-access"
	>
		{$_('liveOperations.noAccess')}
	</p>
{:else}
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
			value={numberFormat.format(Math.round(longest))}
			tone="bg-status-warning text-status-warning-foreground"
		/>
		<MetricCard
			testId="metric-alerts"
			icon={BellRing}
			label={$_('liveOperations.metrics.openAlerts')}
			value={numberFormat.format(alerts.length)}
			tone="bg-status-danger text-status-danger-foreground"
		/>
		<MetricCard
			testId="metric-zones"
			icon={Radio}
			label={$_('liveOperations.metrics.zonesLive')}
			value={`${numberFormat.format(fresh.length)} / ${numberFormat.format(queueZones.length)}`}
			tone="bg-status-success text-status-success-foreground"
		/>
	</section>

	<div class="grid gap-4 xl:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]">
		<div class="flex min-w-0 flex-col gap-4">
			<section
				class="flex flex-col gap-2 rounded-xl border bg-card p-4"
				aria-labelledby="plan-title"
			>
				<div class="flex flex-wrap items-center justify-between gap-2">
					<h2 id="plan-title" class="text-base font-semibold">{$_('liveOperations.plan')}</h2>
					<label class="flex items-center gap-2 text-xs font-medium">
						{$_('zones.level')}
						<select
							bind:value={levelId}
							onchange={loadPlan}
							class="h-9 rounded-md border border-input bg-card px-2 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
						>
							{#each levels as item (item.id)}
								<option value={item.id}>{item.code}, {item.name}</option>
							{/each}
						</select>
					</label>
				</div>
				{#if level}
					<NowcastPlan
						width={level.widthMetres}
						depth={level.depthMetres}
						zones={levelZones}
						{snapshots}
						selected={selectedKey}
						{plan}
						{now}
						onSelect={(key) => (selectedKey = key)}
					/>
				{/if}
				<p class="text-xs text-muted-foreground">
					{$_('liveOperations.thresholds', { values: { near: nearMinutes, over: overMinutes } })}
				</p>
			</section>

			<section class="rounded-xl border bg-card" aria-labelledby="zones-title">
				<div class="border-b px-5 py-3">
					<h2 id="zones-title" class="text-base font-semibold">
						{$_('liveOperations.zones.title')}
					</h2>
					<p class="text-xs text-muted-foreground">{$_('liveOperations.zones.description')}</p>
				</div>
				<div class="overflow-x-auto">
					<table class="w-full text-sm" data-testid="zones-table">
						<thead class="bg-surface-2 text-xs font-semibold tracking-wide text-tertiary uppercase">
							<tr>
								<th scope="col" class="px-4 py-2.5 text-start"
									>{$_('liveOperations.zones.columns.zone')}</th
								>
								<th scope="col" class="px-4 py-2.5 text-end"
									>{$_('liveOperations.zones.columns.inQueue')}</th
								>
								<th scope="col" class="px-4 py-2.5 text-end"
									>{$_('liveOperations.zones.columns.wait')}</th
								>
								<th scope="col" class="px-4 py-2.5 text-start"
									>{$_('liveOperations.zones.columns.status')}</th
								>
								<th scope="col" class="px-4 py-2.5 text-start"
									>{$_('liveOperations.zones.columns.updated')}</th
								>
							</tr>
						</thead>
						<tbody>
							{#each queueZones as zone (zone.key)}
								{@const snapshot = snapshots[zone.key]}
								{@const status = waitStatus(snapshot, now)}
								{@const stale = !!snapshot && isStale(snapshot, now)}
								<tr
									data-testid="zone-row"
									data-zone={zone.name}
									class={cn(
										'odd:bg-surface even:bg-surface-2/50 hover:bg-muted',
										selectedKey === zone.key && 'bg-primary/10'
									)}
								>
									<th scope="row" class="px-4 py-2 text-start font-normal">
										<button
											type="button"
											aria-current={selectedKey === zone.key ? 'true' : undefined}
											onclick={() => (selectedKey = zone.key)}
											class="font-medium underline-offset-4 hover:underline focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
										>
											{zone.name}
										</button>
									</th>
									<td class="px-4 py-2 text-end tabular-nums"
										>{snapshot ? numberFormat.format(snapshot.queueLength) : ''}</td
									>
									<td class="px-4 py-2 text-end tabular-nums">
										{#if snapshot?.nowcastMinutes != null}
											{#if estimateOnly(snapshot)}<span
													data-testid="estimate-marker"
													class="me-1 text-muted-foreground"
													title={$_('liveOperations.estimateHint')}
													><span aria-hidden="true">≈</span><span class="sr-only"
														>{$_('liveOperations.estimateHint')}</span
													></span
												>{/if}{numberFormat.format(snapshot.nowcastMinutes)}
										{:else if snapshot?.noService}
											<span class="text-xs text-muted-foreground"
												>{labelOf(
													$_,
													'liveOperations.noService',
													snapshot.noService,
													knownLabels.noService
												)}</span
											>
										{:else if snapshot}
											<span class="text-xs text-muted-foreground"
												>{$_('liveOperations.zones.noEstimate')}</span
											>
										{/if}
									</td>
									<td class="px-4 py-2">
										{#if live.refused.includes(zone.key)}
											<StatusBadge tone="neutral" label={$_('liveOperations.refused')} />
										{:else if !snapshot}
											<StatusBadge tone="neutral" label={$_('liveOperations.noData')} />
										{:else if stale}
											<span
												title={$_('liveOperations.staleHint', {
													values: { seconds: staleAfterSeconds }
												})}><StatusBadge tone="neutral" label={$_('liveOperations.stale')} /></span
											>
										{:else}
											<StatusBadge
												tone={statusTone[status]}
												label={$_(`liveOperations.status.${status}`)}
											/>
										{/if}
									</td>
									<td class="px-4 py-2 tabular-nums">{updated(snapshot)}</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
			</section>

			{#if selected}
				<section class="rounded-xl border bg-card p-4">
					<WaitChart zone={selected.name} points={history[selected.key] ?? []} />
				</section>
			{/if}
		</div>

		<aside class="flex min-w-0 flex-col gap-4">
			<AlertsPanel {alerts} onChanged={loadAlerts} />
			{#if auth.can('ArrivalWave.View')}
				<ArrivalStrip {wave} />
			{/if}
			{#if seesDesks}
				<DeskStatesPanel states={desks} />
			{/if}
		</aside>
	</div>
{/if}
