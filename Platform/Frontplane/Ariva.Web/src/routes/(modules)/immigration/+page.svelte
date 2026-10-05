<script lang="ts">
	import { ExternalLink, Radio, ScanLine } from '@lucide/svelte';
	import { onDestroy, onMount } from 'svelte';
	import { _, locale } from 'svelte-i18n';
	import { estimateOnly, statusTone, waitStatus } from '$lib/components/pages/live/waits';
	import SimplePageHeader from '$lib/components/shared/SimplePageHeader.svelte';
	import StatusBadge, { type StatusTone } from '$lib/components/shared/StatusBadge.svelte';
	import { LiveConnection, type ZoneSnapshot } from '$lib/core/Live.svelte';
	import * as operations from '$lib/core/operations';
	import type { Immigration, ImmigrationHall } from '$lib/core/operations';
	import * as topology from '$lib/core/topology';
	import type { Site } from '$lib/core/topology';
	import { cn } from '$lib/utils';

	const lanesKnown = ['CIT', 'RES', 'VIS', 'CRW', 'EG'];
	const desksKnown = ['Opened', 'Paused', 'Closed', 'Unknown'];
	const deskTone: Record<string, string> = {
		Opened: 'border-status-success-border bg-status-success text-status-success-foreground',
		Paused: 'border-status-warning-border bg-status-warning text-status-warning-foreground',
		Closed: 'border-status-neutral-border bg-status-neutral text-status-neutral-foreground',
		Unknown: 'border-dashed border-status-neutral-border bg-card text-muted-foreground'
	};
	const rejectKeys = [
		'documentRead',
		'biometricCapture',
		'eligibility',
		'referredToOfficer',
		'technical',
		'other'
	] as const;

	let siteList = $state<Site[]>([]);
	let siteCode = $state('');
	let view = $state<Immigration | null>(null);
	let problem = $state('');
	let hallKind = $state<'Immigration' | 'Emigration'>('Immigration');
	let snapshots = $state<Record<string, ZoneSnapshot>>({});
	let now = $state(Date.now());
	let destroyed = false;
	let timers: ReturnType<typeof setInterval>[] = [];

	/** The zone keys of the halls' lane queues: the only snapshots this screen keeps. */
	let watched = new Set<string>();
	const live = new LiveConnection(
		(snapshot) => {
			if (watched.has(snapshot.zoneKey)) snapshots[snapshot.zoneKey] = snapshot;
		},
		() => undefined
	);

	const isArabic = $derived($locale?.startsWith('ar') ?? false);
	const numberFormat = $derived(
		new Intl.NumberFormat(isArabic ? 'ar-u-nu-latn' : 'en', { maximumFractionDigits: 1 })
	);
	const percent = $derived(
		new Intl.NumberFormat(isArabic ? 'ar-u-nu-latn' : 'en', {
			style: 'percent',
			maximumFractionDigits: 0
		})
	);
	const hall = $derived<ImmigrationHall | null>(
		view?.halls.find((h) => h.kind === hallKind) ?? null
	);

	/** Every lane the hall has a queue or desks for, in the reference order, then any other the site configures. */
	const laneCodes = $derived.by(() => {
		if (!hall) return [];
		const codes = new Set([...hall.queues.map((q) => q.lane), ...hall.lanes.map((l) => l.lane)]);
		return [
			...lanesKnown.filter((c) => codes.has(c)),
			...[...codes].filter((c) => !lanesKnown.includes(c))
		];
	});

	function laneName(code: string | null): string {
		if (!code) return '';
		return lanesKnown.includes(code) ? $_(`immigration.lanes.${code}`) : code;
	}

	function deskState(state: string): string {
		return desksKnown.includes(state) ? $_(`immigration.deskStates.${state}`) : state;
	}

	/** The lane's wait now: the longest fresh nowcast among its queue zones. */
	function laneWait(code: string): { snapshot: ZoneSnapshot | undefined; tone: StatusTone } {
		const fresh = (hall?.queues ?? [])
			.filter((q) => q.lane === code)
			.map((q) => snapshots[q.zoneKey])
			.filter((s): s is ZoneSnapshot => !!s)
			.sort((a, b) => (b.nowcastMinutes ?? -1) - (a.nowcastMinutes ?? -1));
		const snapshot = fresh[0];
		return { snapshot, tone: statusTone[waitStatus(snapshot, now)] };
	}

	function seconds(value: number | null | undefined): string {
		return value == null
			? '–'
			: $_('immigration.seconds', { values: { value: numberFormat.format(value) } });
	}

	async function load(): Promise<void> {
		if (!siteCode) return;
		const result = await operations.immigration(siteCode);
		if (destroyed) return;
		if (result.hasErrors || !result.data) {
			problem = result.errorMessages.join(' ');
			return;
		}
		problem = '';
		view = result.data;
	}

	async function loadSite(): Promise<void> {
		view = null;
		snapshots = {};
		await load();
		if (destroyed || !view) return;
		watched = new Set(view.halls.flatMap((h) => h.queues.map((q) => q.zoneKey)));
		await live.watch([...watched], null);
	}

	onMount(async () => {
		const result = await topology.sites();
		siteList = result.data ?? [];
		siteCode = siteList.find((s) => s.code === 'DMO')?.code ?? siteList[0]?.code ?? '';
		if (siteCode) await loadSite();
		if (destroyed) return;
		timers = [
			setInterval(() => (now = Date.now()), 10_000),
			setInterval(() => void load(), 60_000)
		];
	});

	onDestroy(() => {
		destroyed = true;
		for (const timer of timers) clearInterval(timer);
		void live.stop();
	});
</script>

<svelte:head>
	<title>{$_('immigration.title')} · {$_('app.title')}</title>
</svelte:head>

<SimplePageHeader
	icon={ScanLine}
	title={$_('immigration.title')}
	description={$_('immigration.description')}
>
	{#snippet actions()}
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
		<label for="immigration-site" class="sr-only">{$_('topology.site')}</label>
		<select
			id="immigration-site"
			bind:value={siteCode}
			onchange={loadSite}
			class="h-10 rounded-lg border border-input bg-card px-3 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			{#each siteList as site (site.code)}
				<option value={site.code}
					>{site.name && site.name !== site.code ? `${site.code}, ${site.name}` : site.code}</option
				>
			{/each}
		</select>
	{/snippet}
</SimplePageHeader>

<div class="flex flex-col gap-4">
	<div class="flex flex-wrap items-center justify-between gap-3">
		<div
			role="tablist"
			aria-label={$_('immigration.halls')}
			class="inline-flex rounded-lg border bg-card p-1"
		>
			{#each ['Immigration', 'Emigration'] as const as kind (kind)}
				<button
					type="button"
					role="tab"
					aria-selected={hallKind === kind}
					data-testid="hall-{kind}"
					onclick={() => (hallKind = kind)}
					class={cn(
						'h-8 rounded-md px-3 text-sm font-medium focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none',
						hallKind === kind ? 'bg-primary text-primary-foreground' : 'hover:bg-accent'
					)}
				>
					{$_(`immigration.hallNames.${kind}`)}
				</button>
			{/each}
		</div>
		<!-- Officer-level analytics stay in the border system: shown as a disabled link, never a request. -->
		<span
			role="link"
			aria-disabled="true"
			title={$_('immigration.amanHint')}
			data-testid="aman-link"
			class="inline-flex h-9 cursor-not-allowed items-center gap-2 rounded-md border bg-muted px-3 text-sm text-muted-foreground"
		>
			<ExternalLink class="size-4" aria-hidden="true" />
			{$_('immigration.amanLink')}
		</span>
	</div>

	{#if problem}
		<p
			class="rounded-xl border bg-card p-4 text-sm text-status-danger-foreground"
			data-testid="immigration-problem"
		>
			{problem}
		</p>
	{/if}

	{#if hall}
		<section class="rounded-xl border bg-card" aria-labelledby="lanes-title" data-testid="lanes">
			<div class="border-b px-4 py-3">
				<h2 id="lanes-title" class="text-base font-semibold">{$_('immigration.lanesTitle')}</h2>
				<p class="text-xs text-muted-foreground">
					{$_('immigration.lanesDescription', { values: { minutes: view?.windowMinutes ?? 15 } })}
				</p>
			</div>
			{#if laneCodes.length === 0}
				<p class="px-4 py-6 text-sm text-muted-foreground">{$_('immigration.noLanes')}</p>
			{:else}
				<div class="overflow-x-auto">
					<table class="w-full text-sm" data-testid="lanes-table">
						<thead class="bg-muted/50 text-xs text-muted-foreground">
							<tr>
								<th class="px-4 py-2 text-start font-medium">{$_('immigration.columns.lane')}</th>
								<th class="px-4 py-2 text-end font-medium">{$_('immigration.columns.wait')}</th>
								<th class="px-4 py-2 text-end font-medium">{$_('immigration.columns.queue')}</th>
								<th class="px-4 py-2 text-end font-medium">{$_('immigration.columns.open')}</th>
								<th class="px-4 py-2 text-end font-medium">{$_('immigration.columns.paused')}</th>
								<th class="px-4 py-2 text-end font-medium">{$_('immigration.columns.served')}</th>
								<th class="px-4 py-2 text-end font-medium">{$_('immigration.columns.service')}</th>
								<th class="px-4 py-2 text-end font-medium">{$_('immigration.columns.p90')}</th>
							</tr>
						</thead>
						<tbody>
							{#each laneCodes as code (code)}
								{@const wait = laneWait(code)}
								{@const lane = hall.lanes.find((l) => l.lane === code)}
								<tr class="border-t" data-testid="lane-row" data-lane={code}>
									<th scope="row" class="px-4 py-2 text-start font-medium">
										{laneName(code)}
										<span class="ms-1 font-mono text-xs text-muted-foreground">{code}</span>
									</th>
									<td class="px-4 py-2 text-end tabular-nums" data-testid="lane-wait">
										{#if wait.snapshot?.nowcastMinutes != null}
											{#if estimateOnly(wait.snapshot)}<span
													data-testid="estimate-marker"
													class="me-1 text-muted-foreground"
													title={$_('liveOperations.estimateHint')}
													><span aria-hidden="true">≈</span><span class="sr-only"
														>{$_('liveOperations.estimateHint')}</span
													></span
												>{/if}<StatusBadge
												tone={wait.tone}
												label={$_('immigration.minutes', {
													values: { value: numberFormat.format(wait.snapshot.nowcastMinutes) }
												})}
											/>
										{:else}
											<span class="text-xs text-muted-foreground">{$_('immigration.noWait')}</span>
										{/if}
									</td>
									<td class="px-4 py-2 text-end tabular-nums"
										>{wait.snapshot ? numberFormat.format(wait.snapshot.queueLength) : '–'}</td
									>
									<td class="px-4 py-2 text-end tabular-nums">{lane?.desksOpen ?? 0}</td>
									<td class="px-4 py-2 text-end tabular-nums">{lane?.desksPaused ?? 0}</td>
									<td class="px-4 py-2 text-end tabular-nums">{lane?.transactions ?? 0}</td>
									<td class="px-4 py-2 text-end tabular-nums"
										>{seconds(lane?.meanServiceSeconds)}</td
									>
									<td class="px-4 py-2 text-end tabular-nums"
										>{seconds(lane?.maxP90ServiceSeconds)}</td
									>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
			{/if}
		</section>

		<div class="grid gap-4 xl:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
			<section
				class="flex flex-col gap-3 rounded-xl border bg-card p-4"
				aria-labelledby="egates-title"
				data-testid="egates"
			>
				<div>
					<h2 id="egates-title" class="text-base font-semibold">{$_('immigration.eGatesTitle')}</h2>
					<p class="text-xs text-muted-foreground">
						{$_('immigration.eGatesUsed', {
							values: { used: hall.eGates.gatesUsed, configured: hall.eGates.gatesConfigured }
						})}
					</p>
				</div>
				<dl class="grid grid-cols-2 gap-3 sm:grid-cols-4">
					<div>
						<dt class="text-xs text-muted-foreground">{$_('immigration.utilisation')}</dt>
						<dd class="text-lg font-semibold tabular-nums" data-testid="egate-utilisation">
							{hall.eGates.utilisation == null ? '–' : percent.format(hall.eGates.utilisation)}
						</dd>
					</div>
					<div>
						<dt class="text-xs text-muted-foreground">{$_('immigration.attempts')}</dt>
						<dd class="text-lg font-semibold tabular-nums" data-testid="egate-attempts">
							{hall.eGates.attempts}
						</dd>
					</div>
					<div>
						<dt class="text-xs text-muted-foreground">{$_('immigration.rejected')}</dt>
						<dd class="text-lg font-semibold tabular-nums" data-testid="egate-rejected">
							{hall.eGates.rejected}
						</dd>
					</div>
					<div>
						<dt class="text-xs text-muted-foreground">{$_('immigration.rejectRate')}</dt>
						<dd class="text-lg font-semibold tabular-nums">
							{hall.eGates.rejectRate == null ? '–' : percent.format(hall.eGates.rejectRate)}
						</dd>
					</div>
				</dl>
				<div>
					<h3 class="mb-1.5 text-xs font-semibold tracking-wide text-tertiary uppercase">
						{$_('immigration.rejectsTitle')}
					</h3>
					<ul class="flex flex-col gap-1 text-sm" data-testid="rejects">
						{#each rejectKeys as key (key)}
							<li class="flex justify-between gap-3" data-reject={key}>
								<span>{$_(`immigration.rejects.${key}`)}</span>
								<span class="tabular-nums">{hall.eGates.rejects[key]}</span>
							</li>
						{/each}
					</ul>
				</div>
				<p class="rounded-md bg-muted/60 p-2 text-sm" data-testid="extra-load">
					{#if hall.eGates.rejected === 0}
						{$_('immigration.extraNone')}
					{:else}
						{$_('immigration.extraLoad', {
							values: { deskMinutes: numberFormat.format(hall.eGates.extraManualDeskMinutes) }
						})}
						{#if hall.eGates.extraManualWaitMinutes != null}
							{$_('immigration.extraWait', {
								values: { minutes: numberFormat.format(hall.eGates.extraManualWaitMinutes) }
							})}
						{/if}
					{/if}
				</p>
			</section>

			<section
				class="flex flex-col gap-3 rounded-xl border bg-card p-4"
				aria-labelledby="desks-title"
				data-testid="desk-grid"
			>
				<div>
					<h2 id="desks-title" class="text-base font-semibold">{$_('immigration.desksTitle')}</h2>
					<p class="text-xs text-muted-foreground">{$_('immigration.desksDescription')}</p>
				</div>
				{#if !view?.desksIncluded}
					<p class="text-sm text-muted-foreground" data-testid="desks-hidden">
						{$_('immigration.desksHidden')}
					</p>
				{:else if hall.desks.length === 0}
					<p class="text-sm text-muted-foreground">{$_('immigration.noDesks')}</p>
				{:else}
					<ul class="grid grid-cols-[repeat(auto-fill,minmax(7.5rem,1fr))] gap-1.5">
						{#each hall.desks as desk (desk.desk)}
							<li
								data-testid="desk"
								data-desk={desk.desk}
								data-state={desk.state}
								class={cn(
									'rounded-md border px-2 py-1.5 text-xs',
									deskTone[desk.state] ?? deskTone.Unknown
								)}
							>
								<div class="flex items-baseline justify-between gap-1">
									<span class="font-mono font-medium">{desk.desk}</span>
									<span>{deskState(desk.state)}</span>
								</div>
								<div class="mt-0.5 flex justify-between gap-1 tabular-nums">
									<span>{desk.lane ?? ''}</span>
									<span title={$_('immigration.columns.service')}
										>{seconds(desk.meanServiceSeconds)}</span
									>
								</div>
								<div class="tabular-nums">
									{$_('immigration.served', { values: { count: desk.transactions } })}
								</div>
							</li>
						{/each}
					</ul>
					{#if hall.gates.length}
						<div class="overflow-x-auto">
							<table class="w-full text-xs" data-testid="gates-table">
								<thead class="text-muted-foreground">
									<tr>
										<th class="py-1 pe-3 text-start font-medium"
											>{$_('immigration.columns.gate')}</th
										>
										<th class="py-1 pe-3 text-end font-medium">{$_('immigration.attempts')}</th>
										<th class="py-1 pe-3 text-end font-medium">{$_('immigration.rejected')}</th>
										<th class="py-1 text-end font-medium">{$_('immigration.utilisation')}</th>
									</tr>
								</thead>
								<tbody>
									{#each hall.gates as gate (gate.gate)}
										<tr class="border-t" data-testid="gate" data-gate={gate.gate}>
											<td class="py-1 pe-3 font-mono">{gate.gate}</td>
											<td class="py-1 pe-3 text-end tabular-nums">{gate.attempts}</td>
											<td class="py-1 pe-3 text-end tabular-nums">{gate.rejected}</td>
											<td class="py-1 text-end tabular-nums"
												>{gate.utilisation == null ? '–' : percent.format(gate.utilisation)}</td
											>
										</tr>
									{/each}
								</tbody>
							</table>
						</div>
					{/if}
				{/if}
			</section>
		</div>
	{/if}
</div>
