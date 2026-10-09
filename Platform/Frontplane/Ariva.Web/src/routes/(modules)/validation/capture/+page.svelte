<script lang="ts">
	import { ClipboardCheck, RefreshCw, TriangleAlert } from '@lucide/svelte';
	import { onDestroy, onMount, untrack } from 'svelte';
	import { beforeNavigate, goto } from '$app/navigation';
	import { _, locale } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import SimplePageHeader from '$lib/components/shared/SimplePageHeader.svelte';
	import OutboxBanner from '$lib/components/pages/validation/OutboxBanner.svelte';
	import TallyPanel from '$lib/components/pages/validation/TallyPanel.svelte';
	import TracerPanel from '$lib/components/pages/validation/TracerPanel.svelte';
	import {
		Tally,
		Tracers,
		type CountPayload,
		type RunPayload
	} from '$lib/components/pages/validation/capture.svelte';
	import { duration, siteClock, siteClockSeconds } from '$lib/components/pages/validation/format';
	import { Outbox } from '$lib/components/pages/validation/outbox.svelte';
	import { primaryButton, secondaryButton } from '$lib/components/pages/validation/ui';
	import { auth } from '$lib/core/auth.svelte';
	import * as topology from '$lib/core/topology';
	import {
		binMs,
		binStartUtc,
		captureCount,
		captureRuns,
		localDate,
		ownCounts,
		ownRuns,
		running,
		type CaptureCampaign,
		type CaptureLine,
		type ManualCount,
		type TracerBatch,
		type TracerRun
	} from '$lib/core/validation';

	// The observer tablet (ARV-104c, wiki 07 section 8): a line tally per 15-minute bin and a tracer timer for one
	// running campaign. Only Validation.Capture holders (the Validation observer role) use it; the server checks every
	// call. Unsent bins and runs stay in memory with their Idempotency-Keys; nothing goes to web storage.

	type CountItem = CountPayload & { siteCode: string; campaignId: string };
	type RunItem = RunPayload & { siteCode: string; campaignId: string };

	const canCapture = $derived(auth.can('Validation.Capture'));

	let sites = $state<string[]>([]);
	let siteCode = $state('');
	let campaigns = $state<CaptureCampaign[]>([]);
	let campaign = $state<CaptureCampaign | null>(null);
	let loading = $state(false);
	let problem = $state('');
	let tab = $state<'tally' | 'tracers'>('tally');
	let now = $state(Date.now());
	let history = $state<ManualCount[]>([]);
	let recorded = $state<TracerRun[]>([]);
	let offset = $state<{ ms: number; at: number } | null>(null);
	/** A navigation held back because something on the screen is not sent (the observer decides). */
	let heldNavigation = $state<URL | null>(null);
	let leaving = false;
	let destroyed = false;
	let timer: ReturnType<typeof setInterval> | undefined;

	/** The account this screen captures for; its queued items go only under its token (security review M1). */
	const owner = untrack(() => auth.subject);
	const currentOwner = (): string | null => (auth.subject === owner ? owner : null);

	const counts = new Outbox<CountItem, ManualCount>(
		currentOwner,
		(item, key, itemOwner) =>
			captureCount(
				item.siteCode,
				item.campaignId,
				{
					lineId: item.lineId,
					binStartUtc: binStartUtc(item.binStartMs),
					crossingsIn: item.crossingsIn,
					crossingsOut: item.crossingsOut
				},
				key,
				itemOwner
			),
		(item, count) => {
			const zone = campaign?.timeZoneId ?? 'UTC';
			toast.success(
				$_('validation.tally.sent', {
					values: {
						start: siteClock(item.binStartMs, zone, $locale),
						end: siteClock(item.binStartMs + binMs, zone, $locale),
						in: count.crossingsIn,
						out: count.crossingsOut
					}
				})
			);
			if (tally.line?.id === item.lineId) void loadHistory();
		}
	);

	const runs = new Outbox<RunItem, TracerBatch>(
		currentOwner,
		(item, key, itemOwner) =>
			captureRuns(item.siteCode, item.campaignId, [item.run], key, itemOwner),
		(item, batch) => {
			offset = { ms: batch.clockOffsetMs, at: Date.parse(batch.receivedUtc) };
			const stored = batch.runs[0];
			toast.success(
				$_('validation.tracers.sent', {
					values: {
						code: item.run.tracerCode,
						wait: duration((stored?.waitSeconds ?? 0) * 1000),
						zone: item.zoneName
					}
				})
			);
			if (campaign?.id === item.campaignId) void loadRecorded();
		}
	);

	const tally = new Tally((payload) => {
		if (campaign) counts.add({ ...payload, siteCode: campaign.siteCode, campaignId: campaign.id });
	});

	const tracers = new Tracers();

	const busy = $derived(
		tally.counting || tracers.active.length > 0 || counts.unsent > 0 || runs.unsent > 0
	);
	/** The campaign stays while anything of it is on the screen: a tally, a tracer, an unsent or a refused item. */
	const canChange = $derived(!busy && counts.refused.length === 0 && runs.refused.length === 0);
	const today = $derived(campaign ? localDate(now, campaign.timeZoneId) : '');
	const notToday = $derived(campaign !== null && !campaign.days.includes(today));

	async function loadSites(): Promise<void> {
		const user = auth.user;
		if (!user) return;
		if (user.allSites && auth.can('Site.View')) {
			const result = await topology.sites();
			sites = (result.data ?? []).map((s) => s.code);
		} else {
			sites = [...user.sites];
		}
		siteCode = sites[0] ?? '';
	}

	async function loadCampaigns(): Promise<void> {
		if (!siteCode) return;
		loading = true;
		const result = await running(siteCode);
		if (destroyed) return;
		loading = false;
		if (result.hasErrors) {
			problem = $_('validation.failed', { values: { reason: result.errorMessages.join(' ') } });
			campaigns = [];
			return;
		}
		problem = '';
		campaigns = result.data ?? [];
	}

	async function loadHistory(): Promise<void> {
		const line = tally.line;
		if (!campaign || !line) return;
		const result = await ownCounts(campaign.siteCode, campaign.id, line.id);
		if (destroyed || tally.line?.id !== line.id) return;
		history = result.data?.data ?? [];
	}

	async function loadRecorded(): Promise<void> {
		if (!campaign) return;
		const id = campaign.id;
		const result = await ownRuns(campaign.siteCode, id);
		if (destroyed || campaign?.id !== id) return;
		recorded = result.data?.data ?? [];
	}

	function choose(next: CaptureCampaign): void {
		campaign = next;
		tab = 'tally';
		history = [];
		recorded = [];
		void loadRecorded();
	}

	function changeCampaign(): void {
		if (!canChange) return;
		campaign = null;
		void loadCampaigns();
	}

	function startLine(line: CaptureLine): void {
		history = [];
		tally.start(line, Date.now());
		void loadHistory();
	}

	function corrected(count: ManualCount): void {
		toast.success($_('validation.correction.saved', { values: { revision: count.revision } }));
		void loadHistory();
	}

	function retry(): void {
		void counts.flush();
		void runs.flush();
	}

	function finished(payload: RunPayload): void {
		if (campaign) runs.add({ ...payload, siteCode: campaign.siteCode, campaignId: campaign.id });
	}

	beforeNavigate((navigation) => {
		if (leaving || !busy) return;
		// A closed tab or a reload: the browser asks the observer (the only way to keep the in-memory items).
		if (navigation.type === 'leave') {
			navigation.cancel();
			return;
		}
		// A session that ended goes to sign-in regardless.
		if (!auth.token || navigation.to?.url.pathname.startsWith('/login')) return;
		navigation.cancel();
		heldNavigation = navigation.to?.url ?? null;
	});

	function leaveAnyway(): void {
		const target = heldNavigation;
		heldNavigation = null;
		if (!target) return;
		leaving = true;
		void goto(`${target.pathname}${target.search}`);
	}

	onMount(() => {
		timer = setInterval(() => {
			now = Date.now();
			tally.tick(now);
		}, 1000);
		const online = () => retry();
		window.addEventListener('online', online);
		// Without the capture permission the screen says so and asks the server for nothing.
		if (canCapture) void loadSites().then(loadCampaigns);
		return () => window.removeEventListener('online', online);
	});

	// A sign-out, an ended session or another account (another tab): nothing queued here is sent any more.
	$effect(() => {
		if (auth.subject !== owner) {
			counts.close();
			runs.close();
		}
	});

	onDestroy(() => {
		destroyed = true;
		if (timer) clearInterval(timer);
		counts.close();
		runs.close();
	});
</script>

<svelte:head>
	<title>{$_('validation.title')} · {$_('app.title')}</title>
</svelte:head>

<SimplePageHeader
	icon={ClipboardCheck}
	title={$_('validation.title')}
	description={$_('validation.description')}
>
	{#snippet actions()}
		{#if canCapture && sites.length > 1 && !campaign}
			<label for="capture-site" class="sr-only">{$_('validation.site')}</label>
			<select
				id="capture-site"
				data-testid="capture-site"
				bind:value={siteCode}
				onchange={loadCampaigns}
				class="h-12 rounded-lg border border-input bg-card px-3 text-base focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
			>
				{#each sites as code (code)}
					<option value={code}>{code}</option>
				{/each}
			</select>
		{/if}
	{/snippet}
</SimplePageHeader>

{#if !canCapture}
	<section class="rounded-xl border bg-card p-6" data-testid="no-access">
		<h2 class="text-base font-semibold">{$_('access.title')}</h2>
		<p class="mt-1 text-sm text-muted-foreground">{$_('access.description')}</p>
	</section>
{:else}
	{#if heldNavigation}
		<div
			role="alertdialog"
			aria-labelledby="leave-title"
			aria-describedby="leave-description"
			data-testid="leave-warning"
			class="mb-4 flex flex-wrap items-center justify-between gap-3 rounded-xl border border-status-warning-border bg-status-warning p-4 text-status-warning-foreground"
		>
			<div class="flex min-w-0 items-start gap-3">
				<TriangleAlert class="mt-0.5 size-5 shrink-0" aria-hidden="true" />
				<div>
					<h2 id="leave-title" class="text-base font-semibold">{$_('validation.leave.title')}</h2>
					<p id="leave-description" class="text-sm">{$_('validation.leave.description')}</p>
				</div>
			</div>
			<div class="flex flex-wrap gap-2">
				<button
					type="button"
					data-testid="leave-stay"
					onclick={() => (heldNavigation = null)}
					class={primaryButton}
				>
					{$_('validation.leave.stay')}
				</button>
				<button
					type="button"
					data-testid="leave-anyway"
					onclick={leaveAnyway}
					class={secondaryButton}
				>
					{$_('validation.leave.leave')}
				</button>
			</div>
		</div>
	{/if}

	{#if campaign}
		<OutboxBanner {counts} {runs} timeZone={campaign.timeZoneId} onRetry={retry} />
	{/if}

	{#if !campaign}
		<section aria-labelledby="campaigns-title" class="rounded-xl border bg-card p-5">
			<div class="flex flex-wrap items-start justify-between gap-3">
				<div>
					<h2 id="campaigns-title" class="text-xl font-semibold">
						{$_('validation.campaigns.title')}
					</h2>
					<p class="mt-1 max-w-prose text-sm text-secondary-foreground">
						{$_('validation.campaigns.intro')}
					</p>
				</div>
				<button
					type="button"
					data-testid="refresh-campaigns"
					disabled={loading || !siteCode}
					onclick={loadCampaigns}
					class={secondaryButton}
				>
					<RefreshCw class="size-4" aria-hidden="true" />
					{$_('validation.refresh')}
				</button>
			</div>
			{#if problem}
				<p role="alert" class="mt-3 text-sm text-status-danger-foreground">{problem}</p>
			{:else if auth.user && sites.length === 0}
				<p class="mt-3 text-sm text-muted-foreground">{$_('validation.noSites')}</p>
			{:else if loading && campaigns.length === 0}
				<p class="mt-3 text-sm text-muted-foreground" role="status">{$_('validation.loading')}</p>
			{:else if campaigns.length === 0}
				<p class="mt-3 text-sm text-muted-foreground" data-testid="no-campaigns">
					{$_('validation.campaigns.none')}
				</p>
			{:else}
				<ul class="mt-4 grid gap-3 md:grid-cols-2">
					{#each campaigns as option (option.id)}
						<li>
							<button
								type="button"
								data-testid="campaign-option"
								onclick={() => choose(option)}
								class="flex min-h-20 w-full flex-col items-start gap-1 rounded-xl border bg-surface-2 p-4 text-start hover:bg-muted focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
							>
								<span class="text-lg font-semibold break-all" data-testid="campaign-name"
									>{option.name}</span
								>
								<span class="text-sm text-secondary-foreground">
									{$_('validation.campaigns.scope', {
										values: { lines: option.lines.length, zones: option.zones.length }
									})}
								</span>
								<span class="text-sm text-secondary-foreground tabular-nums">
									{$_('validation.campaigns.days', { values: { days: option.days.join(', ') } })}
								</span>
							</button>
						</li>
					{/each}
				</ul>
			{/if}
		</section>
	{:else}
		<section
			aria-labelledby="campaign-title"
			class="mb-4 flex flex-wrap items-center justify-between gap-3 rounded-xl border bg-card p-5"
		>
			<div class="min-w-0">
				<h2
					id="campaign-title"
					data-testid="chosen-campaign"
					class="text-xl font-semibold break-all"
				>
					{campaign.name}
				</h2>
				<p class="text-sm text-secondary-foreground tabular-nums" data-testid="site-clock">
					{$_('validation.campaigns.clock', {
						values: {
							time: siteClockSeconds(now, campaign.timeZoneId, $locale),
							zone: campaign.timeZoneId
						}
					})}
				</p>
			</div>
			<button
				type="button"
				data-testid="change-campaign"
				disabled={!canChange}
				onclick={changeCampaign}
				class={secondaryButton}
			>
				{$_('validation.campaigns.change')}
			</button>
		</section>

		{#if notToday}
			<p
				role="status"
				data-testid="not-planned-day"
				class="mb-4 rounded-xl border border-status-warning-border bg-status-warning p-4 text-sm text-status-warning-foreground"
			>
				{$_('validation.campaigns.notToday', { values: { today } })}
			</p>
		{/if}

		<div role="tablist" aria-label={$_('validation.tabs.label')} class="mb-4 flex gap-1 border-b">
			{#each [['tally', 'validation.tabs.tally'], ['tracers', 'validation.tabs.tracers']] as [id, key] (id)}
				<button
					type="button"
					role="tab"
					id="capture-tab-{id}"
					aria-selected={tab === id}
					aria-controls="capture-panel-{id}"
					tabindex={tab === id ? 0 : -1}
					data-testid="capture-tab-{id}"
					onclick={() => (tab = id as 'tally' | 'tracers')}
					onkeydown={(event) => {
						if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
							event.preventDefault();
							tab = tab === 'tally' ? 'tracers' : 'tally';
							document.getElementById(`capture-tab-${tab}`)?.focus();
						}
					}}
					class="-mb-px min-h-12 border-b-2 px-4 text-base font-medium focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none {tab ===
					id
						? 'border-primary text-foreground'
						: 'border-transparent text-muted-foreground hover:text-foreground'}"
				>
					{$_(key)}
				</button>
			{/each}
		</div>

		<!-- Both panels stay mounted: a bin closes and a tracer's timer runs while the other panel is shown. -->
		<div
			role="tabpanel"
			id="capture-panel-tally"
			aria-labelledby="capture-tab-tally"
			hidden={tab !== 'tally'}
		>
			<TallyPanel
				{campaign}
				{tally}
				{now}
				{history}
				onStart={startLine}
				onCorrected={corrected}
				onReload={() => void loadHistory()}
			/>
		</div>
		<div
			role="tabpanel"
			id="capture-panel-tracers"
			aria-labelledby="capture-tab-tracers"
			hidden={tab !== 'tracers'}
		>
			<TracerPanel {campaign} {tracers} {now} {recorded} {offset} onFinished={finished} />
		</div>
	{/if}
{/if}
