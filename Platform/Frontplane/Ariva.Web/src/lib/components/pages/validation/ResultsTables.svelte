<script lang="ts">
	import { _, locale } from 'svelte-i18n';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import type {
		CampaignResults,
		CriterionUnit,
		DeskAgreementSummary,
		NowcastErrorStats,
		NowcastZoneErrors,
		TracerZoneSummary,
		Verdict
	} from '$lib/core/validation';
	import {
		minutes,
		percent,
		signedPercent,
		siteDateTime,
		verdictTone,
		whole
	} from './campaignFormat';

	// The tables behind the criteria (ARV-104h): count accuracy per line, tracer waits per queue zone, track completion per
	// zone, desk-state agreement per border desk (only when the server included the desk section: border data), the
	// ground-truth proof (the published nowcast beside the one without AMAN inputs, when the server included it) and the
	// devices' calibration records. Zone, line, desk and device names are text from the server, shown as text only.

	let { results }: { results: CampaignResults } = $props();

	const header = 'p-4 text-start text-xs font-semibold tracking-wide text-tertiary uppercase';
	const headerEnd = 'p-4 text-end text-xs font-semibold tracking-wide text-tertiary uppercase';
	const row = 'odd:bg-surface even:bg-surface-2/50 hover:bg-muted';
	const cell = 'px-4 py-3 whitespace-nowrap tabular-nums';
	const cellEnd = 'px-4 py-3 text-end whitespace-nowrap tabular-nums';

	const countUnits = $derived<CriterionUnit[]>(
		results.criteria.find((c) => c.criterion === 'CountAccuracy')?.units ?? []
	);
	const lines = $derived(
		[...(results.counts?.lines ?? [])].sort(
			(a, b) => a.queueZone.localeCompare(b.queueZone) || a.lineName.localeCompare(b.lineName)
		)
	);
	const tracerZones = $derived(
		[...(results.tracers?.zones ?? [])].sort((a, b) =>
			(a.queueZone ?? '').localeCompare(b.queueZone ?? '')
		)
	);
	const desks = $derived(
		[...(results.desks?.desks ?? [])].sort(
			(a, b) =>
				(a.checkpointCode ?? '').localeCompare(b.checkpointCode ?? '') ||
				(a.deskCode ?? '').localeCompare(b.deskCode ?? '')
		)
	);
	const proofZones = $derived(
		[...(results.nowcast?.zones ?? [])].sort((a, b) =>
			(a.queueZone ?? '').localeCompare(b.queueZone ?? '')
		)
	);
	const showProof = $derived(
		results.nowcast !== null && (results.audience?.proofIncluded ?? false)
	);

	const none = (): string => $_('validationCampaigns.noValue');
	const share = (value: number | null | undefined): string =>
		value == null ? none() : percent(value, $locale);
	const mins = (value: number | null | undefined): string =>
		value == null
			? none()
			: $_('validationCampaigns.results.minutesValue', {
					values: { value: minutes(value, $locale) }
				});
	const signedMins = (value: number | null | undefined): string =>
		value == null
			? none()
			: $_('validationCampaigns.results.minutesValue', {
					values: { value: `${value < 0 ? '-' : '+'}${minutes(Math.abs(value), $locale)}` }
				});
	const verdictLabel = (verdict: Verdict): string =>
		$_(`validationCampaigns.results.verdicts.${verdict}`);

	function unitOf(zone: string, line: string): CriterionUnit | undefined {
		return countUnits.find((u) => u.queueZone === zone && u.lineName === line);
	}

	function judgedMedian(stats: NowcastErrorStats | null | undefined): string {
		if (!stats || stats.minutes === 0) return none();
		return $_('validationCampaigns.results.minutesAndError', {
			values: {
				minutes: whole(stats.minutes, $locale),
				error: mins(stats.medianAbsoluteErrorMinutes)
			}
		});
	}

	function proofRow(zone: NowcastZoneErrors): string[] {
		return [
			judgedMedian(zone.published?.judged),
			judgedMedian(zone.shadow?.judged),
			zone.both ? whole(zone.both.minutes, $locale) : none(),
			zone.both && zone.both.minutes > 0
				? mins(zone.both.published.medianAbsoluteErrorMinutes)
				: none(),
			zone.both && zone.both.minutes > 0
				? mins(zone.both.shadow.medianAbsoluteErrorMinutes)
				: none()
		];
	}

	function tracerCells(zone: TracerZoneSummary): string[] {
		return [
			whole(zone.runs, $locale),
			whole(zone.compared, $locale),
			$_('validationCampaigns.results.comparedValue', {
				values: {
					judged: whole(zone.withinTolerance, $locale),
					required: whole(zone.compared, $locale)
				}
			}),
			zone.bias == null ? none() : signedPercent(zone.bias, $locale),
			signedMins(zone.meanErrorMinutes),
			mins(zone.largestAbsoluteErrorMinutes),
			whole(zone.abandoned, $locale)
		];
	}

	function deskCells(desk: DeskAgreementSummary): string[] {
		return [
			whole(desk.minutes, $locale),
			whole(desk.judged, $locale),
			whole(desk.agreeing, $locale),
			share(desk.check.value),
			share(desk.strictAgreement),
			whole(desk.excluded, $locale)
		];
	}
</script>

<section aria-labelledby="results-lines-title" class="rounded-xl border bg-card p-5">
	<h3 id="results-lines-title" class="text-xl font-semibold">
		{$_('validationCampaigns.results.linesTitle')}
	</h3>
	{#if lines.length === 0}
		<p class="mt-2 text-sm text-muted-foreground">{$_('validationCampaigns.results.noLines')}</p>
	{:else}
		<div class="mt-3 overflow-x-auto rounded-lg border">
			<table class="w-full text-sm" data-testid="results-lines">
				<caption class="sr-only">{$_('validationCampaigns.results.linesCaption')}</caption>
				<thead class="bg-surface-2">
					<tr>
						{#each ['zone', 'line', 'compared', 'excluded', 'lowest', 'pooled', 'verdict'] as column (column)}
							<th scope="col" class={header}
								>{$_(`validationCampaigns.results.linesColumns.${column}`)}</th
							>
						{/each}
					</tr>
				</thead>
				<tbody>
					{#each lines as line, index (index)}
						{@const unit = unitOf(line.queueZone, line.lineName)}
						{@const verdict = unit?.verdict ?? line.check.verdict}
						<tr data-testid="results-line" data-line={line.lineName} class={row}>
							<td class="px-4 py-3 break-all">{line.queueZone}</td>
							<th scope="row" class="px-4 py-3 text-start font-medium break-all">{line.lineName}</th
							>
							<td class={cell}
								>{unit
									? $_('validationCampaigns.results.comparedValue', {
											values: {
												judged: whole(unit.judged, $locale),
												required: whole(unit.required, $locale)
											}
										})
									: whole(line.judgedBins, $locale)}</td
							>
							<td class={cell}>{whole(line.excludedBins, $locale)}</td>
							<td class={cell}>{share(line.lowestAccuracy)}</td>
							<td class={cell}>{share(line.pooledAccuracy)}</td>
							<td class="px-4 py-3">
								<StatusBadge tone={verdictTone(verdict)} label={verdictLabel(verdict)} />
							</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	{/if}
</section>

<section aria-labelledby="results-tracers-title" class="rounded-xl border bg-card p-5">
	<h3 id="results-tracers-title" class="text-xl font-semibold">
		{$_('validationCampaigns.results.tracersTitle')}
	</h3>
	{#if tracerZones.length === 0}
		<p class="mt-2 text-sm text-muted-foreground">{$_('validationCampaigns.results.noTracers')}</p>
	{:else}
		<div class="mt-3 overflow-x-auto rounded-lg border">
			<table class="w-full text-sm" data-testid="results-tracers">
				<caption class="sr-only">{$_('validationCampaigns.results.tracersCaption')}</caption>
				<thead class="bg-surface-2">
					<tr>
						<th scope="col" class={header}
							>{$_('validationCampaigns.results.tracersColumns.zone')}</th
						>
						{#each ['runs', 'compared', 'within', 'bias', 'meanError', 'largestError', 'abandoned'] as column (column)}
							<th scope="col" class={headerEnd}
								>{$_(`validationCampaigns.results.tracersColumns.${column}`)}</th
							>
						{/each}
						<th scope="col" class={header}
							>{$_('validationCampaigns.results.tracersColumns.errorVerdict')}</th
						>
						<th scope="col" class={header}
							>{$_('validationCampaigns.results.tracersColumns.biasVerdict')}</th
						>
					</tr>
				</thead>
				<tbody>
					{#each tracerZones as zone, index (index)}
						<tr data-testid="results-tracer-zone" data-zone={zone.queueZone} class={row}>
							<th scope="row" class="px-4 py-3 text-start font-medium break-all"
								>{zone.queueZone}</th
							>
							{#each tracerCells(zone) as value, index (index)}
								<td class={cellEnd}>{value}</td>
							{/each}
							<td class="px-4 py-3">
								<StatusBadge
									tone={verdictTone(zone.errorCheck.verdict)}
									label={verdictLabel(zone.errorCheck.verdict)}
								/>
							</td>
							<td class="px-4 py-3">
								<StatusBadge
									tone={verdictTone(zone.biasCheck.verdict)}
									label={verdictLabel(zone.biasCheck.verdict)}
								/>
							</td>
						</tr>
					{/each}
				</tbody>
				{#if results.tracers?.overall}
					{@const overall = results.tracers.overall}
					<tfoot class="border-t bg-surface-2 font-semibold">
						<tr data-testid="results-tracer-overall">
							<th scope="row" class="px-4 py-3 text-start"
								>{$_('validationCampaigns.results.everyZone')}</th
							>
							{#each tracerCells(overall) as value, index (index)}
								<td class={cellEnd}>{value}</td>
							{/each}
							<td class="px-4 py-3">
								<StatusBadge
									tone={verdictTone(overall.errorCheck.verdict)}
									label={verdictLabel(overall.errorCheck.verdict)}
								/>
							</td>
							<td class="px-4 py-3">
								<StatusBadge
									tone={verdictTone(overall.biasCheck.verdict)}
									label={verdictLabel(overall.biasCheck.verdict)}
								/>
							</td>
						</tr>
					</tfoot>
				{/if}
			</table>
		</div>
	{/if}
</section>

{#if results.trackCompletion.length > 0}
	<section aria-labelledby="results-tracks-title" class="rounded-xl border bg-card p-5">
		<h3 id="results-tracks-title" class="text-xl font-semibold">
			{$_('validationCampaigns.results.tracksTitle')}
		</h3>
		<div class="mt-3 overflow-x-auto rounded-lg border">
			<table class="w-full text-sm" data-testid="results-tracks">
				<caption class="sr-only">{$_('validationCampaigns.results.tracksCaption')}</caption>
				<thead class="bg-surface-2">
					<tr>
						<th scope="col" class={header}
							>{$_('validationCampaigns.results.tracksColumns.zone')}</th
						>
						{#each ['goodBins', 'excludedBins', 'entered', 'exited', 'rate'] as column (column)}
							<th scope="col" class={headerEnd}
								>{$_(`validationCampaigns.results.tracksColumns.${column}`)}</th
							>
						{/each}
						<th scope="col" class={header}
							>{$_('validationCampaigns.results.tracksColumns.verdict')}</th
						>
					</tr>
				</thead>
				<tbody>
					{#each results.trackCompletion as track, index (index)}
						<tr data-testid="results-track-zone" data-zone={track.queueZone} class={row}>
							<th scope="row" class="px-4 py-3 text-start font-medium break-all"
								>{track.queueZone}</th
							>
							<td class={cellEnd}>{whole(track.good.bins, $locale)}</td>
							<td class={cellEnd}>{whole(track.excludedBins, $locale)}</td>
							<td class={cellEnd}>{whole(track.good.entered, $locale)}</td>
							<td class={cellEnd}>{whole(track.good.exited, $locale)}</td>
							<td class={cellEnd}>{share(track.good.rate)}</td>
							<td class="px-4 py-3">
								<StatusBadge
									tone={verdictTone(track.check.verdict)}
									label={verdictLabel(track.check.verdict)}
								/>
							</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	</section>
{/if}

{#if results.desks}
	<section
		aria-labelledby="results-desks-title"
		class="rounded-xl border bg-card p-5"
		data-testid="results-desks-section"
	>
		<h3 id="results-desks-title" class="text-xl font-semibold">
			{$_('validationCampaigns.results.desksTitle')}
		</h3>
		{#if desks.length === 0}
			<p class="mt-2 text-sm text-muted-foreground">{$_('validationCampaigns.results.noDesks')}</p>
		{:else}
			<div class="mt-3 overflow-x-auto rounded-lg border">
				<table class="w-full text-sm" data-testid="results-desks">
					<caption class="sr-only">{$_('validationCampaigns.results.desksCaption')}</caption>
					<thead class="bg-surface-2">
						<tr>
							<th scope="col" class={header}
								>{$_('validationCampaigns.results.desksColumns.desk')}</th
							>
							{#each ['minutes', 'judged', 'agreeing', 'agreement', 'strict', 'excluded'] as column (column)}
								<th scope="col" class={headerEnd}
									>{$_(`validationCampaigns.results.desksColumns.${column}`)}</th
								>
							{/each}
							<th scope="col" class={header}
								>{$_('validationCampaigns.results.desksColumns.verdict')}</th
							>
						</tr>
					</thead>
					<tbody>
						{#each desks as desk, index (index)}
							<tr
								data-testid="results-desk"
								data-desk="{desk.checkpointCode} {desk.deskCode}"
								class={row}
							>
								<th scope="row" class="px-4 py-3 text-start font-mono text-xs font-medium"
									>{desk.checkpointCode} {desk.deskCode}</th
								>
								{#each deskCells(desk) as value, index (index)}
									<td class={cellEnd}>{value}</td>
								{/each}
								<td class="px-4 py-3">
									<StatusBadge
										tone={verdictTone(desk.check.verdict)}
										label={verdictLabel(desk.check.verdict)}
									/>
								</td>
							</tr>
						{/each}
					</tbody>
					{#if results.desks.overall}
						{@const overall = results.desks.overall}
						<tfoot class="border-t bg-surface-2 font-semibold">
							<tr data-testid="results-desk-overall">
								<th scope="row" class="px-4 py-3 text-start"
									>{$_('validationCampaigns.results.everyDesk')}</th
								>
								{#each deskCells(overall) as value, index (index)}
									<td class={cellEnd}>{value}</td>
								{/each}
								<td class="px-4 py-3">
									<StatusBadge
										tone={verdictTone(overall.check.verdict)}
										label={verdictLabel(overall.check.verdict)}
									/>
								</td>
							</tr>
						</tfoot>
					{/if}
				</table>
			</div>
		{/if}
	</section>
{/if}

{#if showProof && results.nowcast}
	<section
		aria-labelledby="results-proof-title"
		class="rounded-xl border bg-card p-5"
		data-testid="results-proof-section"
	>
		<h3 id="results-proof-title" class="text-xl font-semibold">
			{$_('validationCampaigns.results.proofTitle')}
		</h3>
		<p class="mt-1 max-w-prose text-sm text-secondary-foreground">
			{$_('validationCampaigns.results.proofIntro')}
		</p>
		{#if !results.nowcast.shadowRead}
			<p
				class="mt-3 rounded-lg border border-status-warning-border bg-status-warning p-3 text-sm text-status-warning-foreground"
				data-testid="results-proof-not-read"
			>
				{$_('validationCampaigns.results.proofNotRead')}
			</p>
		{/if}
		<div class="mt-3 overflow-x-auto rounded-lg border">
			<table class="w-full text-sm" data-testid="results-proof">
				<caption class="sr-only">{$_('validationCampaigns.results.proofCaption')}</caption>
				<thead class="bg-surface-2">
					<tr>
						<th scope="col" class={header}>{$_('validationCampaigns.results.proofColumns.zone')}</th
						>
						{#each ['published', 'shadow', 'both', 'bothPublished', 'bothShadow'] as column (column)}
							<th scope="col" class={headerEnd}
								>{$_(`validationCampaigns.results.proofColumns.${column}`)}</th
							>
						{/each}
					</tr>
				</thead>
				<tbody>
					{#each proofZones as zone, index (index)}
						<tr data-testid="results-proof-zone" data-zone={zone.queueZone} class={row}>
							<th scope="row" class="px-4 py-3 text-start font-medium break-all"
								>{zone.queueZone}</th
							>
							{#each proofRow(zone) as value, index (index)}
								<td class={cellEnd}>{value}</td>
							{/each}
						</tr>
					{/each}
				</tbody>
				{#if results.nowcast.overall}
					<tfoot class="border-t bg-surface-2 font-semibold">
						<tr data-testid="results-proof-overall">
							<th scope="row" class="px-4 py-3 text-start"
								>{$_('validationCampaigns.results.everyZone')}</th
							>
							{#each proofRow(results.nowcast.overall) as value, index (index)}
								<td class={cellEnd}>{value}</td>
							{/each}
						</tr>
					</tfoot>
				{/if}
			</table>
		</div>
	</section>
{/if}

<section aria-labelledby="results-calibrations-title" class="rounded-xl border bg-card p-5">
	<h3 id="results-calibrations-title" class="text-xl font-semibold">
		{$_('validationCampaigns.results.calibrationsTitle')}
	</h3>
	{#if results.calibrations.length === 0}
		<p class="mt-2 text-sm text-muted-foreground" data-testid="results-no-calibrations">
			{$_('validationCampaigns.results.noCalibrations')}
		</p>
	{:else}
		<div class="mt-3 overflow-x-auto rounded-lg border">
			<table class="w-full text-sm" data-testid="results-calibrations">
				<caption class="sr-only">{$_('validationCampaigns.results.calibrationsCaption')}</caption>
				<thead class="bg-surface-2">
					<tr>
						{#each ['device', 'zone', 'method', 'sample', 'accuracy', 'waitError', 'threshold', 'result', 'performed'] as column (column)}
							<th scope="col" class={header}
								>{$_(`validationCampaigns.results.calibrationsColumns.${column}`)}</th
							>
						{/each}
					</tr>
				</thead>
				<tbody>
					{#each results.calibrations as record, index (index)}
						<tr data-testid="results-calibration" class={row}>
							<th scope="row" class="px-4 py-3 text-start font-mono text-xs font-medium break-all"
								>{record.deviceCode}</th
							>
							<td class="px-4 py-3 break-all">{record.queueZone}</td>
							<td class="px-4 py-3 break-all">{record.method}</td>
							<td class={cell}>{whole(record.sampleSize, $locale)}</td>
							<td class={cell}>{minutes(record.countingAccuracyPercent, $locale)}%</td>
							<td class={cell}>{mins(record.waitTimeErrorMinutes)}</td>
							<td class={cell}>{minutes(record.thresholdPercent, $locale)}%</td>
							<td class="px-4 py-3">
								<StatusBadge
									tone={record.passed ? 'success' : 'danger'}
									label={record.passed
										? $_('validationCampaigns.results.calibrationPassed')
										: $_('validationCampaigns.results.calibrationFailed')}
								/>
							</td>
							<td class={cell}>
								<time datetime={record.performedOn}
									>{siteDateTime(record.performedOn, results.timeZoneId, $locale)}</time
								>
							</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	{/if}
</section>
