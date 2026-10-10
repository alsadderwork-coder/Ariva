<script lang="ts">
	import { ChevronLeft, ChevronRight, TriangleAlert } from '@lucide/svelte';
	import { onDestroy, onMount } from 'svelte';
	import { _, locale } from 'svelte-i18n';
	import { campaignCounts, type Campaign, type ManualCount } from '$lib/core/validation';
	import { observerLabel, safeTimeZone, siteDateTime } from './campaignFormat';
	import { siteDayClock } from './format';

	// Every observer's counts of a campaign (ARV-104h, Validation.View): current revisions or every revision, per line,
	// newest bin first. Line names and correction reasons are text other people wrote: interpolated as text only (CWE-79).
	// Observers are shown by the start of their Ariva user id, never a name (data boundary).

	let { siteCode, campaign }: { siteCode: string; campaign: Campaign } = $props();

	let rows = $state<ManualCount[]>([]);
	let total = $state(0);
	let pageIndex = $state(1);
	let lineId = $state('');
	let every = $state(false);
	let loading = $state(true);
	let problem = $state('');
	let destroyed = false;
	let ticket = 0;

	const pageSize = 100;
	const pages = $derived(Math.max(1, Math.ceil(total / pageSize)));
	const zone = $derived(safeTimeZone(campaign.timeZoneId));
	const lines = $derived([...campaign.lines].sort((a, b) => a.name.localeCompare(b.name)));

	async function load(): Promise<void> {
		const mine = ++ticket;
		loading = true;
		const result = await campaignCounts(siteCode, campaign.id, {
			lineId: lineId || undefined,
			currentOnly: !every,
			pageIndex
		});
		if (destroyed || mine !== ticket) return;
		loading = false;
		if (result.hasErrors || !result.data) {
			problem = $_('validationCampaigns.failed', {
				values: { reason: result.errorMessages.join(' ') }
			});
			rows = [];
			total = 0;
			return;
		}
		problem = '';
		rows = result.data.data;
		total = result.data.totalCount;
	}

	function filter(): void {
		pageIndex = 1;
		void load();
	}

	function go(next: number): void {
		pageIndex = Math.min(Math.max(1, next), pages);
		void load();
	}

	onMount(() => {
		void load();
	});

	onDestroy(() => {
		destroyed = true;
	});
</script>

<section aria-labelledby="counts-title" class="rounded-xl border bg-card p-5">
	<h3 id="counts-title" class="text-xl font-semibold">{$_('validationCampaigns.counts.title')}</h3>
	<p class="mt-1 max-w-prose text-sm text-secondary-foreground">
		{$_('validationCampaigns.counts.intro')}
	</p>
	<p
		role="note"
		data-testid="counts-no-names"
		class="mt-3 flex items-start gap-2 rounded-lg border border-status-info-border bg-status-info p-3 text-sm text-status-info-foreground"
	>
		<TriangleAlert class="mt-0.5 size-4 shrink-0" aria-hidden="true" />
		<span>{$_('validationCampaigns.counts.names')}</span>
	</p>

	<div class="mt-4 flex flex-wrap items-end gap-4">
		<div class="flex min-w-0 flex-col gap-1">
			<label for="counts-line" class="text-xs font-medium"
				>{$_('validationCampaigns.counts.line')}</label
			>
			<select
				id="counts-line"
				data-testid="counts-line"
				bind:value={lineId}
				onchange={filter}
				class="h-10 max-w-full rounded-lg border border-input bg-background px-3 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
			>
				<option value="">{$_('validationCampaigns.counts.allLines')}</option>
				{#each lines as line, index (index)}
					<option value={line.id}>{line.name}</option>
				{/each}
			</select>
		</div>
		<label class="inline-flex min-h-10 items-center gap-2 text-sm">
			<input type="checkbox" data-testid="counts-every" bind:checked={every} onchange={filter} />
			{$_('validationCampaigns.counts.every')}
		</label>
	</div>

	{#if problem}
		<p role="alert" class="mt-4 text-sm text-status-danger-foreground">{problem}</p>
	{:else if loading && rows.length === 0}
		<p class="mt-4 text-sm text-muted-foreground" role="status">
			{$_('validationCampaigns.loading')}
		</p>
	{:else if rows.length === 0}
		<p class="mt-4 text-sm text-muted-foreground" data-testid="counts-none">
			{$_('validationCampaigns.counts.none')}
		</p>
	{:else}
		<div class="mt-4 overflow-x-auto rounded-lg border">
			<table class="w-full text-sm" data-testid="counts-table">
				<caption class="sr-only">{$_('validationCampaigns.counts.caption')}</caption>
				<thead class="bg-surface-2">
					<tr>
						{#each ['line', 'bin', 'observer', 'in', 'out', 'revision', 'reason', 'recorded'] as column (column)}
							<th
								scope="col"
								class="p-4 text-start text-xs font-semibold tracking-wide text-tertiary uppercase"
								>{$_(`validationCampaigns.counts.columns.${column}`)}</th
							>
						{/each}
					</tr>
				</thead>
				<tbody>
					{#each rows as count, index (index)}
						<tr
							data-testid="count-row"
							data-current={count.current}
							class="odd:bg-surface even:bg-surface-2/50 hover:bg-muted"
						>
							<td class="px-4 py-3 break-all" data-testid="count-line">{count.lineName}</td>
							<td class="px-4 py-3 whitespace-nowrap tabular-nums">
								<time datetime={count.binStartUtc}
									>{siteDayClock(Date.parse(count.binStartUtc), zone, $locale)}</time
								>
							</td>
							<td class="px-4 py-3 font-mono text-xs" data-testid="count-observer"
								>{observerLabel(count.observerId)}</td
							>
							<td class="px-4 py-3 tabular-nums" data-testid="count-in">{count.crossingsIn}</td>
							<td class="px-4 py-3 tabular-nums" data-testid="count-out">{count.crossingsOut}</td>
							<td class="px-4 py-3 whitespace-nowrap tabular-nums">
								{count.revision}
								<span class="text-xs text-muted-foreground"
									>({count.current
										? $_('validationCampaigns.counts.current')
										: $_('validationCampaigns.counts.corrected')})</span
								>
							</td>
							<td class="px-4 py-3 break-all" data-testid="count-reason">{count.reason ?? ''}</td>
							<td class="px-4 py-3 whitespace-nowrap tabular-nums">
								<time datetime={count.recordedUtc}
									>{siteDateTime(count.recordedUtc, zone, $locale)}</time
								>
							</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
		<p class="mt-2 text-xs text-muted-foreground">
			{$_('validationCampaigns.counts.observerHint')}
		</p>
		{#if pages > 1}
			<nav
				class="mt-3 flex items-center gap-2"
				aria-label={$_('validationCampaigns.counts.title')}
				data-testid="counts-pages"
			>
				<button
					type="button"
					disabled={pageIndex <= 1 || loading}
					onclick={() => go(pageIndex - 1)}
					class="inline-flex h-9 items-center gap-1 rounded-md border bg-card px-3 text-sm hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
				>
					<ChevronLeft class="size-4 rtl:-scale-x-100" aria-hidden="true" />
					{$_('validationCampaigns.counts.previous')}
				</button>
				<span class="text-sm tabular-nums"
					>{$_('validationCampaigns.counts.page', { values: { page: pageIndex, pages } })}</span
				>
				<button
					type="button"
					disabled={pageIndex >= pages || loading}
					onclick={() => go(pageIndex + 1)}
					class="inline-flex h-9 items-center gap-1 rounded-md border bg-card px-3 text-sm hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
				>
					{$_('validationCampaigns.counts.next')}
					<ChevronRight class="size-4 rtl:-scale-x-100" aria-hidden="true" />
				</button>
			</nav>
		{/if}
	{/if}
</section>
