<script lang="ts">
	import { Fingerprint, RefreshCw, TriangleAlert } from '@lucide/svelte';
	import { onDestroy, onMount } from 'svelte';
	import { _, locale } from 'svelte-i18n';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import {
		campaignResults,
		criteria as criterionOrder,
		type Campaign,
		type CampaignResults,
		type Criterion,
		type CriterionVerdict
	} from '$lib/core/validation';
	import ResultsTables from './ResultsTables.svelte';
	import {
		criterionKind,
		minutes,
		safeTimeZone,
		percent,
		signedPercent,
		siteDateTime,
		verdictTone,
		whole
	} from './campaignFormat';

	// A campaign's results (ARV-104g served, ARV-104h shown): each pilot criterion's value, target and verdict, what needs a
	// look first, the tables per line, tracer zone and desk, the ground-truth proof and, for a closed campaign, the frozen
	// revision with its SHA-256 content hash. The server projects the results per caller (desks only to border roles, the
	// proof only to Validation.View); this view shows what came. Names in it are text other people wrote (CWE-79).

	let { siteCode, campaign }: { siteCode: string; campaign: Campaign } = $props();

	let results = $state<CampaignResults | null>(null);
	let loading = $state(true);
	let problem = $state('');
	let busy = $state(false);
	let revision = $state<number | undefined>(undefined);
	let destroyed = false;
	let ticket = 0;

	const zone = $derived(safeTimeZone(results?.timeZoneId ?? campaign.timeZoneId));
	/** Every criterion the caller may read, in the pilot's order: the desk criterion comes with the desk section only. */
	const rows = $derived<CriterionVerdict[]>(
		results
			? [...results.criteria, ...(results.desks ? [results.desks.verdict] : [])].sort(
					(a, b) => criterionOrder.indexOf(a.criterion) - criterionOrder.indexOf(b.criterion)
				)
			: []
	);

	async function load(): Promise<void> {
		const mine = ++ticket;
		loading = true;
		problem = '';
		busy = false;
		const result = await campaignResults(siteCode, campaign.id, revision);
		if (destroyed || mine !== ticket) return;
		loading = false;
		if (result.hasErrors || !result.data) {
			// 429 and 503: Ariva computes others or ran out of time; the same request may go again shortly.
			busy = result.status === 429 || result.status === 503;
			problem = busy
				? $_('validationCampaigns.results.busy')
				: $_('validationCampaigns.results.failed', {
						values: { reason: result.errorMessages.join(' ') }
					});
			return;
		}
		// Lists the server may leave out of a result that compared nothing are read as empty.
		const data = result.data;
		results = {
			...data,
			criteria: data.criteria ?? [],
			review: data.review ?? [],
			trackCompletion: data.trackCompletion ?? [],
			calibrations: data.calibrations ?? []
		};
	}

	function pickRevision(event: Event): void {
		const value = Number((event.currentTarget as HTMLSelectElement).value);
		if (!Number.isInteger(value) || value < 1) return;
		revision = value;
		void load();
	}

	function valueText(criterion: Criterion, value: number | null): string {
		if (value === null) return $_('validationCampaigns.noValue');
		const kind = criterionKind(criterion);
		if (kind === 'bias') return signedPercent(value, $locale);
		if (kind === 'minutes')
			return $_('validationCampaigns.results.minutesValue', {
				values: { value: minutes(value, $locale) }
			});
		return percent(value, $locale);
	}

	function targetText(criterion: Criterion, target: number): string {
		const kind = criterionKind(criterion);
		if (kind === 'bias')
			return $_('validationCampaigns.results.within', {
				values: { value: percent(target, $locale) }
			});
		if (kind === 'minutes')
			return $_('validationCampaigns.results.atMost', {
				values: { value: minutes(target, $locale) }
			});
		return $_('validationCampaigns.results.atLeast', {
			values: { value: percent(target, $locale) }
		});
	}

	function comparedText(row: CriterionVerdict): string {
		// Availability counts operating minutes: there is no count of judged items to reach.
		return row.criterion === 'Availability'
			? whole(row.judged, $locale)
			: $_('validationCampaigns.results.comparedValue', {
					values: { judged: whole(row.judged, $locale), required: whole(row.required, $locale) }
				});
	}

	function excludedText(row: CriterionVerdict): string {
		return row.excludedShare === null
			? whole(row.excluded, $locale)
			: $_('validationCampaigns.results.excludedValue', {
					values: {
						excluded: whole(row.excluded, $locale),
						share: percent(row.excludedShare, $locale)
					}
				});
	}

	onMount(() => {
		void load();
	});

	onDestroy(() => {
		destroyed = true;
	});
</script>

{#if loading}
	<p class="rounded-xl border bg-card p-5 text-sm text-muted-foreground" role="status">
		{$_('validationCampaigns.results.loading')}
	</p>
{:else if problem}
	<div
		class="flex flex-wrap items-center justify-between gap-3 rounded-xl border bg-card p-5"
		data-testid="results-problem"
	>
		<p role="alert" class="text-sm text-status-danger-foreground">{problem}</p>
		{#if busy}
			<button
				type="button"
				data-testid="results-retry"
				onclick={() => void load()}
				class="inline-flex h-10 items-center gap-2 rounded-lg border bg-card px-4 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
			>
				<RefreshCw class="size-4" aria-hidden="true" />
				{$_('validationCampaigns.results.retry')}
			</button>
		{/if}
	</div>
{:else if results}
	<div class="flex flex-col gap-4" data-testid="campaign-results">
		{#if results.revision}
			{@const frozen = results.revision}
			<section
				aria-labelledby="results-frozen-title"
				class="rounded-xl border bg-card p-5"
				data-testid="results-frozen"
			>
				<div class="flex flex-wrap items-start justify-between gap-3">
					<div class="flex min-w-0 items-center gap-3">
						<div
							class="flex size-9 shrink-0 items-center justify-center rounded-md bg-primary/10 text-primary"
						>
							<Fingerprint class="size-5" aria-hidden="true" />
						</div>
						<div class="min-w-0">
							<h3 id="results-frozen-title" class="text-xl font-semibold">
								{$_('validationCampaigns.results.frozenTitle')}
							</h3>
							<p
								class="text-sm text-secondary-foreground tabular-nums"
								data-testid="results-revision"
							>
								{$_('validationCampaigns.results.revision', {
									values: { number: frozen.number, revisions: frozen.revisions }
								})}
								·
								<time datetime={frozen.frozenUtc}
									>{$_('validationCampaigns.results.frozenAt', {
										values: { time: siteDateTime(frozen.frozenUtc, zone, $locale) }
									})}</time
								>
							</p>
						</div>
					</div>
					{#if frozen.revisions > 1}
						<div class="flex flex-col gap-1">
							<label for="results-revision" class="text-xs font-medium"
								>{$_('validationCampaigns.results.revisionPick')}</label
							>
							<select
								id="results-revision"
								data-testid="results-revision-pick"
								value={String(frozen.number)}
								onchange={pickRevision}
								class="h-10 rounded-lg border border-input bg-background px-3 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
							>
								{#each Array.from({ length: frozen.revisions }, (_unused, i) => frozen.revisions - i) as number (number)}
									<option value={String(number)}>{number}</option>
								{/each}
							</select>
						</div>
					{/if}
				</div>
				<div class="mt-4">
					<p class="text-xs font-semibold tracking-wide text-tertiary uppercase">
						{$_('validationCampaigns.results.hash')}
					</p>
					<p
						class="mt-1 rounded-lg border bg-surface-2 px-3 py-2 font-mono text-sm break-all select-all"
						data-testid="results-hash"
						dir="ltr"
					>
						{frozen.contentSha256}
					</p>
					<p class="mt-1 text-xs text-muted-foreground">
						{$_('validationCampaigns.results.hashHint')}
					</p>
					{#if frozen.reason}
						<p class="mt-2 text-sm break-all" data-testid="results-reason">
							{$_('validationCampaigns.results.recomputed', { values: { reason: frozen.reason } })}
						</p>
						<p class="mt-1 text-xs text-muted-foreground" data-testid="results-reason-hint">
							{$_('validationCampaigns.results.reasonHint')}
						</p>
					{/if}
				</div>
			</section>
		{:else}
			<section
				aria-labelledby="results-live-title"
				class="rounded-xl border border-status-info-border bg-status-info p-5 text-status-info-foreground"
				data-testid="results-live"
			>
				<h3 id="results-live-title" class="text-base font-semibold">
					{$_('validationCampaigns.results.liveTitle')}
				</h3>
				<p class="mt-1 text-sm">{$_('validationCampaigns.results.live')}</p>
				<p class="mt-1 text-sm tabular-nums">
					<time datetime={results.computedUtc}
						>{$_('validationCampaigns.results.computed', {
							values: { time: siteDateTime(results.computedUtc, zone, $locale) }
						})}</time
					>
				</p>
			</section>
		{/if}

		{#if results.problem}
			<p
				role="alert"
				class="rounded-xl border border-status-danger-border bg-status-danger p-4 text-sm text-status-danger-foreground"
			>
				{$_(
					`validationCampaigns.results.problem.${results.problem === 'InputTooLarge' ? 'InputTooLarge' : 'InvalidScope'}`
				)}
			</p>
		{/if}

		{#if results.review.length > 0}
			<section
				aria-labelledby="results-review-title"
				class="rounded-xl border border-status-warning-border bg-status-warning p-5 text-status-warning-foreground"
				data-testid="results-review"
			>
				<h3 id="results-review-title" class="flex items-center gap-2 text-base font-semibold">
					<TriangleAlert class="size-4 shrink-0" aria-hidden="true" />
					{$_('validationCampaigns.results.reviewTitle')}
				</h3>
				<ul class="mt-2 list-disc ps-5 text-sm">
					{#each results.review as flag, index (index)}
						<li data-testid="results-review-flag" data-flag={flag}>
							{$_(`validationCampaigns.results.review.${flag}`, { default: flag })}
						</li>
					{/each}
				</ul>
			</section>
		{/if}

		<section aria-labelledby="results-criteria-title" class="rounded-xl border bg-card p-5">
			<h3 id="results-criteria-title" class="text-xl font-semibold">
				{$_('validationCampaigns.results.criteriaTitle')}
			</h3>
			<div class="mt-3 overflow-x-auto rounded-lg border">
				<table class="w-full text-sm" data-testid="results-criteria">
					<caption class="sr-only">{$_('validationCampaigns.results.criteriaCaption')}</caption>
					<thead class="bg-surface-2">
						<tr>
							{#each ['criterion', 'value', 'target', 'verdict', 'compared', 'excluded'] as column (column)}
								<th
									scope="col"
									class="p-4 text-start text-xs font-semibold tracking-wide text-tertiary uppercase"
									>{$_(`validationCampaigns.results.columns.${column}`)}</th
								>
							{/each}
						</tr>
					</thead>
					<tbody>
						{#each rows as row, index (index)}
							<tr
								data-testid="criterion-row"
								data-criterion={row.criterion}
								data-verdict={row.verdict}
								class="odd:bg-surface even:bg-surface-2/50 hover:bg-muted"
							>
								<th scope="row" class="px-4 py-3 text-start font-medium">
									{$_(`validationCampaigns.results.criteria.${row.criterion}`)}
								</th>
								<td class="px-4 py-3 whitespace-nowrap tabular-nums" data-testid="criterion-value"
									>{valueText(row.criterion, row.value)}</td
								>
								<td class="px-4 py-3 whitespace-nowrap tabular-nums" data-testid="criterion-target"
									>{targetText(row.criterion, row.target)}</td
								>
								<td class="px-4 py-3" data-testid="criterion-verdict">
									<StatusBadge
										tone={verdictTone(row.verdict)}
										label={$_(`validationCampaigns.results.verdicts.${row.verdict}`)}
									/>
									<span
										class="mt-1 block text-xs text-muted-foreground"
										data-testid="criterion-reason"
										>{$_(`validationCampaigns.results.reasons.${row.reason}`, {
											default: row.reason
										})}</span
									>
								</td>
								<td class="px-4 py-3 whitespace-nowrap tabular-nums">{comparedText(row)}</td>
								<td class="px-4 py-3 whitespace-nowrap tabular-nums">{excludedText(row)}</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		</section>

		<ResultsTables {results} />
	</div>
{/if}
