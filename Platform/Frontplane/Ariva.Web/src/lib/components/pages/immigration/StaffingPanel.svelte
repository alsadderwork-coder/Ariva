<script lang="ts">
	import { ChevronDown, UserRoundCheck } from '@lucide/svelte';
	import { onDestroy, untrack } from 'svelte';
	import { _, locale } from 'svelte-i18n';
	import { SvelteSet } from 'svelte/reactivity';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import * as operations from '$lib/core/operations';
	import type {
		LaneStaffing,
		StaffingInterval,
		StaffingRecommendation
	} from '$lib/core/operations';
	import { cn } from '$lib/utils';

	/**
	 * The indicative staffing recommendation of the arrivals hall (ARV-181b1, formulas F16 pilot version). Border data: the
	 * page renders this panel only for holders of ArrivalWaveLanes.View, so no other role sends its request (the server
	 * refuses them too). Names the server writes (reasons, bounds, throughput sources, lane categories) become words only
	 * through the allowlists below; any other value is shown as it is, as text, and never used as a message key.
	 */
	interface Props {
		siteCode: string;
		/** Lane category codes in the order the page shows them; other categories follow in the server's order. */
		laneOrder: readonly string[];
	}

	let { siteCode, laneOrder }: Props = $props();

	const refreshMs = 60_000;
	const id = $props.id();
	const reasonsKnown = [
		'NoQueueLength',
		'NoThroughput',
		'FlightWithoutPassengers',
		'BacklogUnknown',
		'ArrivalsTruncated',
		'NoDesks',
		'NoLaneDemand',
		'ArrivalsOutOfRange',
		'Stale'
	];
	const boundsKnown = ['Wait', 'Busy', 'Both'];
	const sourcesKnown = ['Measured', 'Fallback'];
	/** The longest predicted wait the server gives: 120 means 120 minutes or more. */
	const waitCap = 120;

	let answer = $state<StaffingRecommendation | null>(null);
	let status = $state<'loading' | 'ready' | 'refused' | 'failed'>('loading');
	/** The lanes whose next 3 hours are shown, by zone key. */
	const expanded = new SvelteSet<string>();
	/** The first answer of the panel opens the first lane short of desks now; later answers keep the user's choice. */
	let opened = false;
	let generation = 0;
	let destroyed = false;

	const isArabic = $derived($locale?.startsWith('ar') ?? false);
	const numberLocale = $derived(isArabic ? 'ar-u-nu-latn' : 'en');
	const decimal = $derived(new Intl.NumberFormat(numberLocale, { maximumFractionDigits: 1 }));
	const whole = $derived(new Intl.NumberFormat(numberLocale, { maximumFractionDigits: 0 }));
	const percent = $derived(
		new Intl.NumberFormat(numberLocale, { style: 'percent', maximumFractionDigits: 0 })
	);

	/** The answer's lanes in the page's lane order, then any other category, each category in the server's order. */
	const lanes = $derived.by(() => {
		const rank = (code: string) => {
			const index = laneOrder.indexOf(code);
			return index < 0 ? laneOrder.length : index;
		};
		const all = Array.isArray(answer?.lanes) ? answer.lanes : [];
		return all
			.filter((lane) => lane && typeof lane.zoneKey === 'string')
			.map((lane, index) => ({ lane, index }))
			.sort((a, b) => rank(a.lane.lane) - rank(b.lane.lane) || a.index - b.index)
			.map((entry) => entry.lane);
	});

	/** The server's time of the answer: the interval holding it is the current quarter hour. */
	const asOf = $derived(answer ? Date.parse(answer.asOfUtc) : Number.NaN);

	/** The target wait and busy cap, when every lane with numbers has the same (otherwise each lane's caption says). */
	const settings = $derived.by(() => {
		const withNumbers = lanes.filter((l) => l.targetMinutes != null && l.busyCap != null);
		const targets = new Set(withNumbers.map((l) => l.targetMinutes));
		const caps = new Set(withNumbers.map((l) => l.busyCap));
		if (withNumbers.length === 0 || targets.size !== 1 || caps.size !== 1) return null;
		return { target: withNumbers[0].targetMinutes!, cap: withNumbers[0].busyCap! };
	});

	/** The latest minute any lane was computed at. */
	const computedAt = $derived.by(() => {
		const minutes = lanes
			.map((l) => l.minuteUtc)
			.filter((m): m is string => !!m && Number.isFinite(Date.parse(m)))
			.sort((a, b) => Date.parse(a) - Date.parse(b));
		return minutes.at(-1) ?? null;
	});

	/**
	 * Reads the site's recommendation; an answer that arrives after a later read began (another site, or the next minute's
	 * read) is dropped, so one site's lanes never show under another.
	 */
	async function load(site: string): Promise<void> {
		const mine = ++generation;
		const result = await operations.staffingRecommendation(site);
		if (destroyed || mine !== generation) return;
		if (operations.staffingRefused(result)) {
			answer = null;
			status = 'refused';
			return;
		}
		if (result.hasErrors || !result.data) {
			answer = null;
			status = 'failed';
			return;
		}
		answer = result.data;
		status = 'ready';
		if (!opened) {
			opened = true;
			const short = lanes.find((lane) => difference(lane, current(lane))?.kind === 'short');
			if (short) expanded.add(short.zoneKey);
		}
	}

	$effect(() => {
		const site = siteCode;
		untrack(() => {
			answer = null;
			status = 'loading';
			expanded.clear();
			opened = false;
			void load(site);
		});
		const timer = setInterval(() => void load(site), refreshMs);
		return () => clearInterval(timer);
	});

	onDestroy(() => {
		destroyed = true;
	});

	function toggle(lane: LaneStaffing): void {
		if (expanded.has(lane.zoneKey)) expanded.delete(lane.zoneKey);
		else expanded.add(lane.zoneKey);
	}

	function laneName(code: string): string {
		return laneOrder.includes(code) ? $_(`immigration.lanes.${code}`) : code;
	}

	/** A reason the screen has words for, or the name as it came (text only). */
	function reason(gap: string): string {
		return reasonsKnown.includes(gap) ? $_(`immigration.staffing.reasons.${gap}`) : gap;
	}

	/** What set the number (for the wait, the busy cap or both); nothing when no desk is needed. */
	function bound(value: string): string {
		if (value === 'None') return '';
		return boundsKnown.includes(value) ? $_(`immigration.staffing.bounds.${value}`) : value;
	}

	function source(value: string | null): string {
		if (!value) return '';
		return sourcesKnown.includes(value) ? $_(`immigration.staffing.sources.${value}`) : value;
	}

	function time(value: string): string {
		return new Date(value).toLocaleTimeString(isArabic ? 'ar-u-nu-latn' : undefined, {
			hour: '2-digit',
			minute: '2-digit'
		});
	}

	/** The end of an interval, as an ISO time. */
	function endOf(interval: StaffingInterval): string {
		return new Date(Date.parse(interval.startUtc) + interval.minutes * 60_000).toISOString();
	}

	/** The index of the interval holding the answer's time (the current quarter hour); the first when none does. */
	function currentIndex(lane: LaneStaffing): number {
		const index = (lane.intervals ?? []).findIndex((interval) => {
			const start = Date.parse(interval.startUtc);
			return start <= asOf && asOf < start + interval.minutes * 60_000;
		});
		return index < 0 ? 0 : index;
	}

	function current(lane: LaneStaffing): StaffingInterval | undefined {
		return (lane.intervals ?? [])[currentIndex(lane)];
	}

	/** Recommended desks now against the desks open now (desk states): short, spare or matching; null when either is not known. */
	function difference(
		lane: LaneStaffing,
		interval: StaffingInterval | undefined
	): { kind: 'short' | 'spare' | 'matches'; count: number } | null {
		if (interval?.recommended == null || lane.openNow == null) return null;
		const gap = interval.recommended - lane.openNow;
		return gap > 0
			? { kind: 'short', count: gap }
			: gap < 0
				? { kind: 'spare', count: -gap }
				: { kind: 'matches', count: 0 };
	}

	function recommended(interval: StaffingInterval): string {
		if (interval.recommended == null) return '';
		const desks = whole.format(interval.recommended);
		if (!interval.atLimit) return desks;
		return interval.shortfallDesks != null
			? $_('immigration.staffing.atLimit', {
					values: { desks, short: whole.format(interval.shortfallDesks) }
				})
			: $_('immigration.staffing.outOfReach', { values: { desks } });
	}

	function wait(minutes: number | null, noService = false): string {
		if (noService) return $_('immigration.staffing.noService');
		if (minutes == null) return $_('immigration.staffing.unknown');
		if (minutes >= waitCap) return $_('immigration.staffing.over120');
		return $_('immigration.minutes', { values: { value: decimal.format(minutes) } });
	}

	function arrivals(value: number | null): string {
		return value == null ? $_('immigration.staffing.unknown') : whole.format(Math.round(value));
	}
</script>

{#snippet openNow(lane: LaneStaffing)}
	{#if lane.openNow == null}
		<span class="text-xs text-muted-foreground">{$_('immigration.staffing.unknown')}</span>
	{:else if lane.openNowDegraded}
		<!-- The approximate sign is shown; a screen reader hears the count, then why it is approximate. -->
		<span
			data-testid="staffing-degraded"
			class="me-1 text-muted-foreground"
			title={$_('immigration.staffing.degraded')}
			aria-hidden="true">≈</span
		>{whole.format(lane.openNow)}
		<span class="sr-only">({$_('immigration.staffing.degraded')})</span>
	{:else}
		{whole.format(lane.openNow)}
	{/if}
{/snippet}

<!-- Zone names come from the zone profile and are text; every status is a word as well as a colour. -->
<section
	class="rounded-xl border bg-card"
	aria-labelledby="{id}-title"
	data-testid="staffing"
	data-state={status}
>
	<div class="flex flex-col gap-1 border-b px-4 py-3">
		<div class="flex flex-wrap items-center gap-2">
			<h2 id="{id}-title" class="flex items-center gap-2 text-base font-semibold">
				<UserRoundCheck class="size-4" aria-hidden="true" />
				{$_('immigration.staffing.title')}
			</h2>
			<span data-testid="staffing-indicative">
				<StatusBadge tone="info" label={$_('immigration.staffing.indicative')} />
			</span>
		</div>
		<p class="text-xs text-muted-foreground">{$_('immigration.staffing.description')}</p>
		{#if status === 'ready' && (settings || computedAt)}
			<p class="text-xs text-muted-foreground" data-testid="staffing-settings">
				{#if settings}
					{$_('immigration.staffing.settings', {
						values: {
							target: decimal.format(settings.target),
							cap: percent.format(settings.cap)
						}
					})}
				{/if}
				{#if computedAt}
					{$_('immigration.staffing.computedAt', { values: { time: time(computedAt) } })}
				{/if}
			</p>
		{/if}
	</div>

	{#if status === 'loading'}
		<p class="px-4 py-6 text-sm text-muted-foreground" aria-busy="true">
			{$_('immigration.staffing.loading')}
		</p>
	{:else if status === 'refused'}
		<p class="px-4 py-6 text-sm text-muted-foreground" data-testid="staffing-refused">
			{$_('immigration.staffing.refused')}
		</p>
	{:else if status === 'failed'}
		<p class="px-4 py-6 text-sm text-status-danger-foreground" data-testid="staffing-failed">
			{$_('immigration.staffing.failed')}
		</p>
	{:else if lanes.length === 0}
		<p class="px-4 py-6 text-sm text-muted-foreground" data-testid="staffing-empty">
			{$_('immigration.staffing.noLanes')}
		</p>
	{:else}
		<div class="overflow-x-auto">
			<table class="w-full text-sm" data-testid="staffing-summary">
				<caption class="sr-only">{$_('immigration.staffing.summaryCaption')}</caption>
				<thead class="bg-muted/50 text-xs text-muted-foreground">
					<tr>
						<th scope="col" class="px-4 py-2 text-start font-medium"
							>{$_('immigration.staffing.columns.lane')}</th
						>
						<th scope="col" class="px-4 py-2 text-end font-medium"
							>{$_('immigration.staffing.columns.openNow')}</th
						>
						<th scope="col" class="px-4 py-2 text-end font-medium"
							>{$_('immigration.staffing.columns.recommendedNow')}</th
						>
						<th scope="col" class="px-4 py-2 text-start font-medium"
							>{$_('immigration.staffing.columns.difference')}</th
						>
						<th scope="col" class="px-4 py-2 text-end font-medium"
							>{$_('immigration.staffing.columns.waitOpenNow')}</th
						>
						<th scope="col" class="px-4 py-2 text-end font-medium"
							>{$_('immigration.staffing.columns.waitRecommended')}</th
						>
						<th scope="col" class="px-4 py-2 text-end font-medium"
							>{$_('immigration.staffing.columns.details')}</th
						>
					</tr>
				</thead>
				<tbody>
					{#each lanes as lane, index (lane.zoneKey)}
						{@const now = current(lane)}
						{@const diff = difference(lane, now)}
						{@const open = expanded.has(lane.zoneKey)}
						{@const detailId = `${id}-lane-${index}`}
						{@const intervals = lane.intervals ?? []}
						<tr
							class="border-t"
							data-testid="staffing-summary-row"
							data-lane={lane.lane}
							data-zone={lane.zone}
						>
							<th scope="row" class="px-4 py-2 text-start font-medium">
								{laneName(lane.lane)}
								<!-- Isolated and kept on one line: a code such as A-CRW must not wrap at its hyphen or reorder in Arabic. -->
								<span
									dir="auto"
									class="ms-1 font-mono text-xs whitespace-nowrap text-muted-foreground"
									data-testid="staffing-zone">{lane.zone}</span
								>
							</th>
							{#if lane.gap === 'Stale' || !now}
								<td
									colspan="5"
									class="px-4 py-2 text-start text-xs text-muted-foreground"
									data-testid="staffing-reason"
									>{reason(lane.gap === 'None' ? 'Stale' : lane.gap)}</td
								>
							{:else}
								<td class="px-4 py-2 text-end tabular-nums" data-testid="staffing-open-now"
									>{@render openNow(lane)}</td
								>
								{#if now.gap !== 'None'}
									<td
										colspan="4"
										class="px-4 py-2 text-start text-xs text-muted-foreground"
										data-testid="staffing-reason">{reason(now.gap)}</td
									>
								{:else}
									<td class="px-4 py-2 text-end tabular-nums" data-testid="staffing-recommended-now"
										>{recommended(now)}</td
									>
									<td class="px-4 py-2 text-start" data-testid="staffing-difference">
										{#if diff?.kind === 'short'}
											<StatusBadge
												tone="warning"
												label={$_('immigration.staffing.short', {
													values: { count: whole.format(diff.count) }
												})}
											/>
										{:else if diff?.kind === 'spare'}
											<span class="text-xs text-muted-foreground"
												>{$_('immigration.staffing.spare', {
													values: { count: whole.format(diff.count) }
												})}</span
											>
										{:else if diff?.kind === 'matches'}
											<span class="text-xs text-muted-foreground"
												>{$_('immigration.staffing.matches')}</span
											>
										{:else}
											<span class="text-xs text-muted-foreground"
												>{$_('immigration.staffing.openUnknown')}</span
											>
										{/if}
									</td>
									<td class="px-4 py-2 text-end tabular-nums" data-testid="staffing-wait-open"
										>{wait(now.waitOpenNowMinutes, now.noServiceOpenNow)}</td
									>
									<td
										class="px-4 py-2 text-end tabular-nums"
										data-testid="staffing-wait-recommended">{wait(now.waitRecommendedMinutes)}</td
									>
								{/if}
							{/if}
							<td class="px-4 py-2 text-end">
								{#if intervals.length > 0}
									<button
										type="button"
										data-testid="staffing-toggle"
										data-lane={lane.lane}
										data-zone={lane.zone}
										aria-expanded={open}
										aria-controls={detailId}
										aria-label={$_('immigration.staffing.nextHoursFor', {
											values: { lane: laneName(lane.lane), zone: lane.zone }
										})}
										onclick={() => toggle(lane)}
										class="inline-flex items-center gap-1 rounded-md px-2 py-1 text-xs font-medium whitespace-nowrap text-primary hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
									>
										<ChevronDown
											class={cn('size-4 transition-transform', open && 'rotate-180')}
											aria-hidden="true"
										/>
										<!-- The name starts with this visible text, then names the lane (WCAG 2.5.3). -->
										{$_('immigration.staffing.nextHours')}
									</button>
								{/if}
							</td>
						</tr>
						{#if intervals.length > 0}
							{@const nowIndex = currentIndex(lane)}
							<tr id={detailId} hidden={!open} data-testid="staffing-detail" data-zone={lane.zone}>
								<td colspan="7" class="bg-surface-2/50 px-4 py-3">
									<table
										class="w-full text-sm"
										data-testid="staffing-lane"
										data-lane={lane.lane}
										data-zone={lane.zone}
									>
										<caption class="pb-2 text-start text-xs text-muted-foreground">
											<span class="font-medium text-foreground"
												>{$_('immigration.staffing.caption', {
													values: {
														lane: laneName(lane.lane),
														zone: lane.zone,
														time: lane.minuteUtc ? time(lane.minuteUtc) : ''
													}
												})}</span
											>
											<!-- Each sentence isolated, so the numbers of two sentences never merge into one run in Arabic. -->
											{#if lane.targetMinutes != null && lane.busyCap != null}
												<span dir="auto" data-testid="staffing-lane-settings"
													>{$_('immigration.staffing.captionSettings', {
														values: {
															target: decimal.format(lane.targetMinutes),
															cap: percent.format(lane.busyCap)
														}
													})}</span
												>
											{/if}
											{#if lane.cycleMinutes != null}
												<span dir="auto" data-testid="staffing-cycle"
													>{$_('immigration.staffing.captionCycle', {
														values: {
															cycle: decimal.format(lane.cycleMinutes),
															source: source(lane.throughputSource)
														}
													})}</span
												>
											{/if}
										</caption>
										<thead class="text-xs text-muted-foreground">
											<tr>
												<th scope="col" class="py-1 pe-3 text-start font-medium"
													>{$_('immigration.staffing.columns.from')}</th
												>
												<th scope="col" class="py-1 pe-3 text-end font-medium"
													>{$_('immigration.staffing.columns.arrivals')}</th
												>
												<th scope="col" class="py-1 pe-3 text-end font-medium"
													>{$_('immigration.staffing.columns.recommended')}</th
												>
												<th scope="col" class="py-1 pe-3 text-end font-medium"
													>{$_('immigration.staffing.columns.waitRecommended')}</th
												>
												<th scope="col" class="py-1 text-end font-medium"
													>{lane.openNow == null
														? $_('immigration.staffing.columns.waitOpenNow')
														: $_('immigration.staffing.columns.waitOpenNowCount', {
																values: { count: whole.format(lane.openNow) }
															})}</th
												>
											</tr>
										</thead>
										<tbody>
											{#each intervals as interval, k (k)}
												<tr
													class={cn('border-t', k === nowIndex && 'font-medium')}
													data-testid="staffing-row"
													data-index={k}
													data-start={interval.startUtc}
												>
													<th
														scope="row"
														class="py-1 pe-3 text-start font-normal tabular-nums"
														data-testid="staffing-from"
													>
														{#if k === nowIndex}
															{$_('immigration.staffing.nowTo', {
																values: { time: time(endOf(interval)) }
															})}
														{:else}
															<time datetime={interval.startUtc}>{time(interval.startUtc)}</time>
														{/if}
													</th>
													<td
														class="py-1 pe-3 text-end tabular-nums"
														data-testid="staffing-arrivals">{arrivals(interval.arrivals)}</td
													>
													{#if interval.gap !== 'None'}
														<td
															colspan="3"
															class="py-1 text-start text-xs text-muted-foreground"
															data-testid="staffing-why"
															><span class="sr-only"
																>{$_('immigration.staffing.columns.reason')}:</span
															>
															{reason(interval.gap)}</td
														>
													{:else}
														<td
															class="py-1 pe-3 text-end tabular-nums"
															data-testid="staffing-recommended"
														>
															{recommended(interval)}
															{#if bound(interval.bound)}
																<span
																	class="ms-1 text-xs text-muted-foreground"
																	data-testid="staffing-bound">{bound(interval.bound)}</span
																>
															{/if}
														</td>
														<td
															class="py-1 pe-3 text-end tabular-nums"
															data-testid="staffing-wait-recommended"
															>{wait(interval.waitRecommendedMinutes)}</td
														>
														<td class="py-1 text-end tabular-nums" data-testid="staffing-wait-open"
															>{wait(interval.waitOpenNowMinutes, interval.noServiceOpenNow)}</td
														>
													{/if}
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
		<p class="border-t px-4 py-2 text-xs text-muted-foreground" data-testid="staffing-open-hint">
			{$_('immigration.staffing.openNowHint')}
		</p>
	{/if}
</section>
