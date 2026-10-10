<script lang="ts">
	import { onDestroy, onMount } from 'svelte';
	import { _, locale } from 'svelte-i18n';
	import {
		campaignRuns,
		maxProgressRuns,
		type Campaign,
		type TracerRun
	} from '$lib/core/validation';
	import { percent, spread, waitRanges, whole } from './campaignFormat';

	// A campaign's capture progress (ARV-104h): bins captured per line against the target, tracer runs per queue zone by
	// wait range (from every observer's runs, Validation.View), and minutes observed per border desk, the last only when
	// the server lists the desks to this caller (desksIncluded: desk states are border data).

	let { siteCode, campaign }: { siteCode: string; campaign: Campaign } = $props();

	let runs = $state<TracerRun[]>([]);
	let total = $state(0);
	let loadingRuns = $state(true);
	let runsProblem = $state('');
	let destroyed = false;

	const binTarget = $derived(campaign.targets.binsPerLine);
	const lines = $derived(
		[...campaign.lines].sort(
			(a, b) => a.queueZone.localeCompare(b.queueZone) || a.name.localeCompare(b.name)
		)
	);
	const ranges = $derived(
		spread(
			runs,
			campaign.zones.map((z) => z.name)
		)
	);
	const desks = $derived(
		[...campaign.desks].sort(
			(a, b) => a.checkpoint.localeCompare(b.checkpoint) || a.code.localeCompare(b.code)
		)
	);

	function share(captured: number, target: number): number {
		return target > 0 ? Math.min(1, captured / target) : 1;
	}

	async function loadRuns(): Promise<void> {
		const found: TracerRun[] = [];
		for (let pageIndex = 1; found.length < maxProgressRuns; pageIndex++) {
			const result = await campaignRuns(siteCode, campaign.id, pageIndex);
			if (destroyed) return;
			if (result.hasErrors || !result.data) {
				runsProblem = $_('validationCampaigns.failed', {
					values: { reason: result.errorMessages.join(' ') }
				});
				break;
			}
			found.push(...result.data.data);
			total = result.data.totalCount;
			if (result.data.data.length === 0 || found.length >= total) break;
		}
		runs = found.slice(0, maxProgressRuns);
		loadingRuns = false;
	}

	onMount(() => {
		void loadRuns();
	});

	onDestroy(() => {
		destroyed = true;
	});
</script>

<div class="flex flex-col gap-4">
	<section aria-labelledby="progress-lines-title" class="rounded-xl border bg-card p-5">
		<h3 id="progress-lines-title" class="text-xl font-semibold">
			{$_('validationCampaigns.progress.linesTitle')}
		</h3>
		<p class="mt-1 max-w-prose text-sm text-secondary-foreground">
			{$_('validationCampaigns.progress.linesIntro', { values: { target: binTarget } })}
		</p>
		{#if lines.length === 0}
			<p class="mt-3 text-sm text-muted-foreground">
				{$_('validationCampaigns.progress.noLines')}
			</p>
		{:else}
			<div class="mt-3 overflow-x-auto rounded-lg border">
				<table class="w-full text-sm" data-testid="progress-lines">
					<caption class="sr-only">{$_('validationCampaigns.progress.linesCaption')}</caption>
					<thead class="bg-surface-2">
						<tr>
							{#each ['line', 'role', 'zone', 'bins', 'share'] as column (column)}
								<th
									scope="col"
									class="p-4 text-start text-xs font-semibold tracking-wide text-tertiary uppercase"
									>{$_(`validationCampaigns.progress.columns.${column}`)}</th
								>
							{/each}
						</tr>
					</thead>
					<tbody>
						{#each lines as line, index (index)}
							{@const done = share(line.binsCaptured, binTarget)}
							<tr
								data-testid="progress-line"
								data-line={line.name}
								class="odd:bg-surface even:bg-surface-2/50 hover:bg-muted"
							>
								<th scope="row" class="px-4 py-3 text-start font-medium break-all">{line.name}</th>
								<td class="px-4 py-3">{$_(`validation.tally.roles.${line.role}`)}</td>
								<td class="px-4 py-3 break-all">{line.queueZone}</td>
								<td class="px-4 py-3 whitespace-nowrap tabular-nums" data-testid="progress-bins"
									>{$_('validationCampaigns.progress.binsValue', {
										values: { captured: line.binsCaptured, target: binTarget }
									})}</td
								>
								<td class="px-4 py-3">
									<span class="flex min-w-32 items-center gap-2">
										<span
											class="h-2 flex-1 overflow-hidden rounded-full bg-muted"
											aria-hidden="true"
										>
											<span
												class="block h-full rounded-full {done >= 1
													? 'bg-status-success-solid'
													: 'bg-primary'}"
												style:width="{done * 100}%"
											></span>
										</span>
										<span class="w-14 text-end text-xs tabular-nums">{percent(done, $locale)}</span>
									</span>
								</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		{/if}
	</section>

	<section aria-labelledby="progress-tracers-title" class="rounded-xl border bg-card p-5">
		<div class="flex flex-wrap items-baseline justify-between gap-2">
			<h3 id="progress-tracers-title" class="text-xl font-semibold">
				{$_('validationCampaigns.progress.tracersTitle')}
			</h3>
			{#if !loadingRuns}
				<p class="text-sm tabular-nums" data-testid="progress-runs-target">
					{$_('validationCampaigns.progress.runsTarget', {
						values: { timed: ranges.total.timed, target: campaign.targets.tracerRuns }
					})}
				</p>
			{/if}
		</div>
		<p class="mt-1 max-w-prose text-sm text-secondary-foreground">
			{$_('validationCampaigns.progress.tracersIntro')}
		</p>
		{#if loadingRuns}
			<p class="mt-3 text-sm text-muted-foreground" role="status">
				{$_('validationCampaigns.progress.loadingRuns')}
			</p>
		{:else if runsProblem}
			<p role="alert" class="mt-3 text-sm text-status-danger-foreground">{runsProblem}</p>
		{:else}
			<div class="mt-3 overflow-x-auto rounded-lg border">
				<table class="w-full text-sm" data-testid="progress-tracers">
					<caption class="sr-only">{$_('validationCampaigns.progress.tracersCaption')}</caption>
					<thead class="bg-surface-2">
						<tr>
							<th
								scope="col"
								class="p-4 text-start text-xs font-semibold tracking-wide text-tertiary uppercase"
								>{$_('validationCampaigns.progress.zone')}</th
							>
							{#each waitRanges as _upper, index (index)}
								<th
									scope="col"
									class="p-4 text-end text-xs font-semibold tracking-wide text-tertiary uppercase"
									>{$_(`validationCampaigns.progress.ranges.r${index}`)}</th
								>
							{/each}
							<th
								scope="col"
								class="p-4 text-end text-xs font-semibold tracking-wide text-tertiary uppercase"
								>{$_('validationCampaigns.progress.timed')}</th
							>
							<th
								scope="col"
								class="p-4 text-end text-xs font-semibold tracking-wide text-tertiary uppercase"
								>{$_('validationCampaigns.progress.abandoned')}</th
							>
						</tr>
					</thead>
					<tbody>
						{#each ranges.zones as row, index (index)}
							<tr
								data-testid="progress-tracer-zone"
								data-zone={row.zone}
								class="odd:bg-surface even:bg-surface-2/50 hover:bg-muted"
							>
								<th scope="row" class="px-4 py-3 text-start font-medium break-all">{row.zone}</th>
								{#each row.ranges as count, index (index)}
									<td class="px-4 py-3 text-end tabular-nums" data-testid="progress-range"
										>{whole(count, $locale)}</td
									>
								{/each}
								<td class="px-4 py-3 text-end tabular-nums" data-testid="progress-timed"
									>{whole(row.timed, $locale)}</td
								>
								<td class="px-4 py-3 text-end tabular-nums" data-testid="progress-abandoned"
									>{whole(row.abandoned, $locale)}</td
								>
							</tr>
						{/each}
					</tbody>
					<tfoot class="border-t bg-surface-2 font-semibold">
						<tr data-testid="progress-tracer-total">
							<th scope="row" class="px-4 py-3 text-start"
								>{$_('validationCampaigns.progress.total')}</th
							>
							{#each ranges.total.ranges as count, index (index)}
								<td class="px-4 py-3 text-end tabular-nums" data-testid="progress-range"
									>{whole(count, $locale)}</td
								>
							{/each}
							<td class="px-4 py-3 text-end tabular-nums" data-testid="progress-timed"
								>{whole(ranges.total.timed, $locale)}</td
							>
							<td class="px-4 py-3 text-end tabular-nums" data-testid="progress-abandoned"
								>{whole(ranges.total.abandoned, $locale)}</td
							>
						</tr>
					</tfoot>
				</table>
			</div>
			{#if total > runs.length}
				<p class="mt-2 text-xs text-muted-foreground">
					{$_('validationCampaigns.progress.truncated', {
						values: { shown: runs.length, total }
					})}
				</p>
			{/if}
		{/if}
	</section>

	{#if campaign.desksIncluded}
		<section
			aria-labelledby="progress-desks-title"
			class="rounded-xl border bg-card p-5"
			data-testid="progress-desks-section"
		>
			<h3 id="progress-desks-title" class="text-xl font-semibold">
				{$_('validationCampaigns.progress.desksTitle')}
			</h3>
			<p class="mt-1 max-w-prose text-sm text-secondary-foreground">
				{$_('validationCampaigns.progress.desksIntro')}
			</p>
			{#if desks.length === 0}
				<p class="mt-3 text-sm text-muted-foreground">
					{$_('validationCampaigns.progress.noDesks')}
				</p>
			{:else}
				<div class="mt-3 overflow-x-auto rounded-lg border">
					<table class="w-full text-sm" data-testid="progress-desks">
						<caption class="sr-only">{$_('validationCampaigns.progress.desksCaption')}</caption>
						<thead class="bg-surface-2">
							<tr>
								<th
									scope="col"
									class="p-4 text-start text-xs font-semibold tracking-wide text-tertiary uppercase"
									>{$_('validationCampaigns.progress.deskColumns.desk')}</th
								>
								<th
									scope="col"
									class="p-4 text-end text-xs font-semibold tracking-wide text-tertiary uppercase"
									>{$_('validationCampaigns.progress.deskColumns.minutes')}</th
								>
							</tr>
						</thead>
						<tbody>
							{#each desks as desk, index (index)}
								<tr
									data-testid="progress-desk"
									data-desk="{desk.checkpoint} {desk.code}"
									class="odd:bg-surface even:bg-surface-2/50 hover:bg-muted"
								>
									<th scope="row" class="px-4 py-3 text-start font-mono text-xs font-medium"
										>{desk.checkpoint} {desk.code}</th
									>
									<td class="px-4 py-3 text-end tabular-nums" data-testid="progress-desk-minutes"
										>{whole(desk.minutesObserved, $locale)}</td
									>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
			{/if}
		</section>
	{/if}
</div>
