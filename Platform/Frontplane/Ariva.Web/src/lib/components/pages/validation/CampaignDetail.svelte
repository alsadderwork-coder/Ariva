<script lang="ts" module>
	/** The views of a campaign: its progress, every observer's counts, its results. */
	export type CampaignView = 'progress' | 'counts' | 'results';
</script>

<script lang="ts">
	import { ArrowLeft, Lock, Play, ShieldCheck } from '@lucide/svelte';
	import { onDestroy, onMount } from 'svelte';
	import { _, locale } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { closeCampaign, getCampaign, startCampaign, type Campaign } from '$lib/core/validation';
	import CampaignCounts from './CampaignCounts.svelte';
	import CampaignProgress from './CampaignProgress.svelte';
	import CampaignResults from './CampaignResults.svelte';
	import { dayLabel, safeTimeZone, siteDateTime, statusTone } from './campaignFormat';

	interface Props {
		siteCode: string;
		campaignId: string;
		view: CampaignView;
		canManage: boolean;
		/** The address of the site's campaign list. */
		listHref: string;
		onView: (view: CampaignView) => void;
	}

	let { siteCode, campaignId, view, canManage, listHref, onView }: Props = $props();

	const tabs: readonly CampaignView[] = ['progress', 'counts', 'results'];

	let campaign = $state<Campaign | null>(null);
	let loading = $state(true);
	let missing = $state(false);
	let problem = $state('');
	let asking = $state(false);
	let busy = $state(false);
	let actionProblem = $state('');
	let destroyed = false;
	/** Bumped after a start or a close, so the views read the campaign again. */
	let revision = $state(0);

	async function load(): Promise<void> {
		const result = await getCampaign(siteCode, campaignId);
		if (destroyed) return;
		loading = false;
		if (result.status === 404) {
			missing = true;
			campaign = null;
			return;
		}
		if (result.hasErrors || !result.data) {
			problem = $_('validationCampaigns.failed', {
				values: { reason: result.errorMessages.join(' ') }
			});
			return;
		}
		problem = '';
		campaign = result.data;
	}

	async function start(): Promise<void> {
		if (busy) return;
		busy = true;
		actionProblem = '';
		const result = await startCampaign(siteCode, campaignId);
		if (destroyed) return;
		busy = false;
		if (result.hasErrors || !result.data) {
			actionProblem = $_('validationCampaigns.detail.notStarted', {
				values: { reason: result.errorMessages.join(' ') }
			});
			return;
		}
		campaign = result.data;
		revision++;
		toast.success($_('validationCampaigns.detail.startedToast'));
	}

	// Closing is critical (security/critical-actions.json): Ariva asks for a second factor within 15 minutes, which Api.ts
	// answers with the step-up dialog before sending the close once more. A cancelled dialog leaves the answer at 401.
	async function close(): Promise<void> {
		if (busy) return;
		busy = true;
		actionProblem = '';
		const result = await closeCampaign(siteCode, campaignId);
		if (destroyed) return;
		busy = false;
		asking = false;
		if (result.hasErrors || !result.data) {
			actionProblem =
				result.status === 401
					? $_('validationCampaigns.detail.stepUpNeeded')
					: $_('validationCampaigns.detail.notClosed', {
							values: { reason: result.errorMessages.join(' ') }
						});
			return;
		}
		campaign = result.data;
		revision++;
		toast.success($_('validationCampaigns.detail.closedToast'));
	}

	/** Arrow keys move between the tabs in reading order (left goes on in Arabic), Home and End to the ends. */
	function moveTab(event: KeyboardEvent, current: CampaignView): void {
		const rtl = document.documentElement.dir === 'rtl';
		const index = tabs.indexOf(current);
		let next = -1;
		if (event.key === 'ArrowRight') next = rtl ? index - 1 : index + 1;
		else if (event.key === 'ArrowLeft') next = rtl ? index + 1 : index - 1;
		else if (event.key === 'Home') next = 0;
		else if (event.key === 'End') next = tabs.length - 1;
		else return;
		event.preventDefault();
		const target = tabs[(next + tabs.length) % tabs.length];
		onView(target);
		document.getElementById(`campaign-tab-${target}`)?.focus();
	}

	onMount(() => {
		void load();
	});

	onDestroy(() => {
		destroyed = true;
	});
</script>

<a
	href={listHref}
	data-testid="campaigns-back"
	class="mb-3 inline-flex items-center gap-1.5 text-sm font-medium text-primary underline-offset-4 hover:underline focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
>
	<ArrowLeft class="size-4 rtl:-scale-x-100" aria-hidden="true" />
	{$_('validationCampaigns.detail.back')}
</a>

{#if loading}
	<p class="text-sm text-muted-foreground" role="status">
		{$_('validationCampaigns.detail.loading')}
	</p>
{:else if missing}
	<p class="rounded-xl border bg-card p-6 text-sm" data-testid="campaign-missing">
		{$_('validationCampaigns.detail.notFound')}
	</p>
{:else if problem || !campaign}
	<p role="alert" class="text-sm text-status-danger-foreground">{problem}</p>
{:else}
	{@const zone = safeTimeZone(campaign.timeZoneId)}
	<section
		aria-labelledby="campaign-name"
		class="mb-4 flex flex-col gap-4 rounded-xl border bg-card p-5"
		data-testid="campaign-detail"
		data-status={campaign.status}
	>
		<div class="flex flex-wrap items-start justify-between gap-3">
			<div class="flex min-w-0 flex-wrap items-center gap-3">
				<h2 id="campaign-name" data-testid="campaign-name" class="text-xl font-semibold break-all">
					{campaign.name}
				</h2>
				<span data-testid="campaign-status">
					<StatusBadge
						tone={statusTone(campaign.status)}
						label={$_(`validationCampaigns.statuses.${campaign.status}`)}
					/>
				</span>
			</div>
			{#if canManage && campaign.status !== 'Closed' && !asking}
				<div class="flex flex-wrap gap-2">
					{#if campaign.status === 'Planned'}
						<button
							type="button"
							data-testid="start-campaign"
							disabled={busy}
							onclick={start}
							title={$_('validationCampaigns.detail.startHint')}
							class="inline-flex h-10 items-center gap-2 rounded-lg bg-button-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60 dark:text-white"
						>
							<Play class="size-4" aria-hidden="true" />
							{$_('validationCampaigns.detail.start')}
						</button>
					{/if}
					<button
						type="button"
						data-testid="close-campaign"
						disabled={busy}
						onclick={() => {
							asking = true;
							actionProblem = '';
						}}
						class="inline-flex h-10 items-center gap-2 rounded-lg border bg-card px-4 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
					>
						<Lock class="size-4" aria-hidden="true" />
						{$_('validationCampaigns.detail.close')}
					</button>
				</div>
			{/if}
		</div>

		{#if asking}
			<div
				role="alertdialog"
				aria-labelledby="close-campaign-title"
				aria-describedby="close-campaign-text"
				data-testid="close-campaign-confirm"
				class="flex flex-col gap-3 rounded-lg border border-status-warning-border bg-status-warning p-4 text-status-warning-foreground"
			>
				<h3 id="close-campaign-title" class="text-base font-semibold">
					{$_('validationCampaigns.detail.closeTitle')}
				</h3>
				<p id="close-campaign-text" class="text-sm">{$_('validationCampaigns.detail.closeText')}</p>
				<div class="flex flex-wrap gap-2">
					<button
						type="button"
						data-testid="confirm-close-campaign"
						disabled={busy}
						onclick={close}
						class="inline-flex h-10 items-center gap-2 rounded-lg border border-status-danger-border bg-status-danger px-4 text-sm font-medium text-status-danger-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
					>
						<ShieldCheck class="size-4" aria-hidden="true" />
						{$_('validationCampaigns.detail.closeConfirm')}
					</button>
					<button
						type="button"
						data-testid="cancel-close-campaign"
						disabled={busy}
						onclick={() => (asking = false)}
						class="inline-flex h-10 items-center rounded-lg border bg-card px-4 text-sm font-medium text-foreground hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
					>
						{$_('validationCampaigns.detail.closeCancel')}
					</button>
				</div>
			</div>
		{/if}
		{#if actionProblem}
			<p
				role="alert"
				data-testid="campaign-action-problem"
				class="rounded-lg border border-status-danger-border bg-status-danger p-3 text-sm text-status-danger-foreground"
			>
				{actionProblem}
			</p>
		{/if}

		<h3 class="sr-only">{$_('validationCampaigns.detail.facts')}</h3>
		<dl class="grid gap-x-6 gap-y-3 text-sm sm:grid-cols-2 xl:grid-cols-3">
			<div class="min-w-0">
				<dt class="text-xs font-semibold tracking-wide text-tertiary uppercase">
					{$_('validationCampaigns.detail.profile')}
				</dt>
				<dd class="tabular-nums" data-testid="campaign-profile">
					{$_('validationCampaigns.detail.profileValue', {
						values: {
							version: campaign.profileVersion,
							status: $_(
								`validationCampaigns.detail.profileStatus.${campaign.profileStatus === 'Published' ? 'Published' : 'Retired'}`
							)
						}
					})}
				</dd>
			</div>
			<div class="min-w-0">
				<dt class="text-xs font-semibold tracking-wide text-tertiary uppercase">
					{$_('validationCampaigns.detail.geometry')}
				</dt>
				<dd class="font-mono text-xs break-all" data-testid="campaign-geometry">
					{campaign.geometryHash}
				</dd>
			</div>
			<div class="min-w-0">
				<dt class="text-xs font-semibold tracking-wide text-tertiary uppercase">
					{$_('validationCampaigns.detail.timeZone')}
				</dt>
				<dd class="break-all">{campaign.timeZoneId}</dd>
			</div>
			<div class="min-w-0">
				<dt class="text-xs font-semibold tracking-wide text-tertiary uppercase">
					{$_('validationCampaigns.detail.days')}
				</dt>
				<dd class="flex flex-wrap gap-x-2 tabular-nums" data-testid="campaign-days">
					{#each campaign.days as day, index (index)}
						<time datetime={day}
							>{dayLabel(day, $locale)}{index < campaign.days.length - 1 ? ',' : ''}</time
						>
					{/each}
				</dd>
			</div>
			<div class="min-w-0">
				<dt class="text-xs font-semibold tracking-wide text-tertiary uppercase">
					{$_('validationCampaigns.detail.targets')}
				</dt>
				<dd class="tabular-nums" data-testid="campaign-targets">
					{$_('validationCampaigns.detail.targetsValue', {
						values: { bins: campaign.targets.binsPerLine, runs: campaign.targets.tracerRuns }
					})}
					{#if campaign.targets.placeholder}
						<span class="block text-xs text-status-warning-foreground"
							>{$_('validationCampaigns.detail.placeholder')}</span
						>
					{/if}
				</dd>
			</div>
			<div class="min-w-0">
				<dt class="text-xs font-semibold tracking-wide text-tertiary uppercase">
					{$_('validationCampaigns.detail.planned')}
				</dt>
				<dd class="tabular-nums">
					<time datetime={campaign.createdUtc}
						>{siteDateTime(campaign.createdUtc, zone, $locale)}</time
					>
				</dd>
			</div>
			<div class="min-w-0">
				<dt class="text-xs font-semibold tracking-wide text-tertiary uppercase">
					{$_('validationCampaigns.detail.started')}
				</dt>
				<dd class="tabular-nums">
					{#if campaign.startedUtc}
						<time datetime={campaign.startedUtc}
							>{siteDateTime(campaign.startedUtc, zone, $locale)}</time
						>
					{:else}
						{$_('validationCampaigns.detail.notYet')}
					{/if}
				</dd>
			</div>
			<div class="min-w-0">
				<dt class="text-xs font-semibold tracking-wide text-tertiary uppercase">
					{$_('validationCampaigns.detail.closed')}
				</dt>
				<dd class="tabular-nums">
					{#if campaign.closedUtc}
						<time datetime={campaign.closedUtc}
							>{siteDateTime(campaign.closedUtc, zone, $locale)}</time
						>
					{:else}
						{$_('validationCampaigns.detail.notYet')}
					{/if}
				</dd>
			</div>
		</dl>
	</section>

	<div
		role="tablist"
		aria-label={$_('validationCampaigns.detail.tabs.label')}
		class="mb-4 flex gap-1 border-b"
	>
		{#each tabs as id (id)}
			<button
				type="button"
				role="tab"
				id="campaign-tab-{id}"
				aria-selected={view === id}
				aria-controls={view === id ? `campaign-panel-${id}` : undefined}
				tabindex={view === id ? 0 : -1}
				data-testid="campaign-tab-{id}"
				onclick={() => onView(id)}
				onkeydown={(event) => moveTab(event, id)}
				class="-mb-px min-h-11 border-b-2 px-4 text-sm font-medium focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none {view ===
				id
					? 'border-primary text-foreground'
					: 'border-transparent text-muted-foreground hover:text-foreground'}"
			>
				{$_(`validationCampaigns.detail.tabs.${id}`)}
			</button>
		{/each}
	</div>

	<div role="tabpanel" id="campaign-panel-{view}" aria-labelledby="campaign-tab-{view}">
		{#key revision}
			{#if view === 'progress'}
				<CampaignProgress {siteCode} {campaign} />
			{:else if view === 'counts'}
				<CampaignCounts {siteCode} {campaign} />
			{:else}
				<CampaignResults {siteCode} {campaign} />
			{/if}
		{/key}
	</div>
{/if}
