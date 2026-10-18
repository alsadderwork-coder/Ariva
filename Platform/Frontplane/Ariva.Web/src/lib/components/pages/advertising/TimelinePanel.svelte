<script lang="ts">
	import { ChartGantt } from '@lucide/svelte';
	import { onDestroy, untrack } from 'svelte';
	import { _ } from 'svelte-i18n';
	import { cn } from '$lib/utils';
	import * as plans from '$lib/core/advertising-plans';
	import type { Timeline } from '$lib/core/advertising-plans';
	import type { Display } from '$lib/core/displays';

	/**
	 * One display's week (ARV-143a): seven rows, one per local day of the site from midnight today, each a bar of 24 hours
	 * with what the display plays, positioned by local start and end: a playlist's name, or the instructions while no
	 * schedule applies. Widths are set through Svelte's style directives (the CSSOM), which the content security policy
	 * allows; the bar runs from the start side, so it mirrors in Arabic. It is read again after every change of a playlist
	 * or a schedule. Every name is shown as text.
	 */
	interface Props {
		siteCode: string;
		/** The site's displays (empty when they cannot be listed). */
		displays: Display[];
		/** False when the user may not list the site's displays. */
		displaysReadable: boolean;
		/** Displays the site's advertising policy keeps on instructions (from the policy, when it can be read). */
		fallingBack: string[];
		/** Grows with every change of the site's playlists or schedules. */
		version: number;
		onRefused: () => void;
	}

	let { siteCode, displays, displaysReadable, fallingBack, version, onRefused }: Props = $props();

	let chosen = $state(untrack(() => displays[0]?.code ?? ''));
	let timeline = $state<Timeline | null>(null);
	let loading = $state(false);
	let problem = $state<string | null>(null);
	let generation = 0;

	const display = $derived(displays.find((d) => d.code === chosen) ?? null);
	const days = $derived(timeline ? plans.timelineDays(timeline.segments) : []);
	/** The display's second region, as the timeline names it: one set to Instructions plays no advertising. */
	const instructionsOnly = $derived(
		(timeline?.secondRegion ?? display?.secondRegion) === 'Instructions'
	);
	/** A display set to advertising that still shows instructions: the full layout, or the policy keeps it off. */
	const heldBack = $derived(
		!!display &&
			!instructionsOnly &&
			(display.layout !== 'Split' || fallingBack.includes(display.code))
	);
	const quarters = [0, 6, 12, 18];

	async function load(code: string, displayCode: string): Promise<void> {
		const mine = ++generation;
		if (!displayCode) {
			timeline = null;
			return;
		}
		loading = true;
		problem = null;
		const result = await plans.timeline(code, displayCode);
		if (mine !== generation) return;
		loading = false;
		if (result.hasErrors || !result.data) {
			if (result.status === 403) return onRefused();
			timeline = null;
			problem = $_('advertisingPlans.timeline.failed', {
				values: { reason: result.errorMessages.join(' ') }
			});
			return;
		}
		problem = null;
		timeline = result.data;
	}

	$effect(() => {
		const code = siteCode;
		const displayCode = chosen;
		// Read again after every change of a playlist or a schedule.
		void version;
		untrack(() => void load(code, displayCode));
	});

	onDestroy(() => {
		generation++;
	});

	function weekday(day: plans.WeekDay): string {
		return $_(`advertisingPlans.days.${day}`);
	}
</script>

<section
	class="rounded-xl border bg-card"
	aria-labelledby="advertising-timeline-title"
	data-testid="timeline"
>
	<div class="border-b px-4 py-3">
		<h2 id="advertising-timeline-title" class="flex items-center gap-2 text-base font-semibold">
			<ChartGantt class="size-4 rtl:-scale-x-100" aria-hidden="true" />
			{$_('advertisingPlans.timeline.title')}
		</h2>
		<p class="text-xs text-muted-foreground">{$_('advertisingPlans.timeline.description')}</p>
	</div>
	<div class="flex flex-col gap-3 px-4 py-3 text-sm">
		{#if !displaysReadable}
			<p class="text-muted-foreground" data-testid="timeline-no-permission">
				{$_('advertisingPlans.timeline.displaysHidden')}
			</p>
		{:else if displays.length === 0}
			<p class="text-muted-foreground" data-testid="timeline-no-displays">
				{$_('advertisingPlans.timeline.noDisplays')}
			</p>
		{:else}
			<div class="flex flex-wrap items-end gap-x-4 gap-y-2">
				<div class="flex min-w-0 flex-col gap-1">
					<label for="advertising-timeline-display" class="text-xs font-medium"
						>{$_('advertisingPlans.timeline.display')}</label
					>
					<select
						id="advertising-timeline-display"
						data-testid="timeline-display"
						bind:value={chosen}
						class="h-9 min-w-0 rounded-md border border-input bg-background px-2 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
					>
						{#each displays as option (option.id)}
							<option value={option.code}
								>{$_('advertisingPlans.timeline.displayOption', {
									values: { code: option.code, name: option.name }
								})}</option
							>
						{/each}
					</select>
				</div>
				{#if timeline}
					<p class="text-xs text-muted-foreground" data-testid="timeline-zone">
						{$_('advertisingPlans.timeline.timeZone', { values: { zone: timeline.timeZone } })}
					</p>
				{/if}
			</div>

			{#if instructionsOnly}
				<p
					class="rounded-md border border-status-info-border bg-status-info px-3 py-2 text-xs text-status-info-foreground"
					role="note"
					data-testid="advertising-timeline-instructions-note"
				>
					{$_('advertisingPlans.timeline.instructionsOnly')}
				</p>
			{:else if heldBack}
				<p
					class="rounded-md border border-status-info-border bg-status-info px-3 py-2 text-xs text-status-info-foreground"
					role="note"
					data-testid="timeline-not-advertising"
				>
					{$_('advertisingPlans.timeline.notAdvertising')}
				</p>
			{/if}

			{#if problem}
				<p class="text-xs text-destructive" role="alert" data-testid="timeline-problem">
					{problem}
				</p>
			{:else if loading && !timeline}
				<p class="text-muted-foreground" data-testid="timeline-loading">
					{$_('advertisingPlans.timeline.loading')}
				</p>
			{:else if timeline}
				<div
					class="flex flex-col gap-1.5"
					data-testid="timeline-week"
					data-display={timeline.displayCode}
					aria-busy={loading}
				>
					<!-- The hours of the day, over the bars: 00:00 at the start side, 24:00 at the end side. -->
					<div class="grid grid-cols-1 gap-2 sm:grid-cols-[10rem_minmax(0,1fr)]" aria-hidden="true">
						<span class="hidden sm:block"></span>
						<span class="relative grid grid-cols-4 text-[11px] text-muted-foreground tabular-nums">
							{#each quarters as hour (hour)}
								<span class="border-s ps-1">{String(hour).padStart(2, '0')}:00</span>
							{/each}
							<span class="absolute end-0 top-0">24:00</span>
						</span>
					</div>
					<ol class="flex flex-col gap-1.5">
						{#each days as day, index (day.day)}
							<li
								class="grid grid-cols-1 items-center gap-1 sm:grid-cols-[10rem_minmax(0,1fr)] sm:gap-2"
								data-testid="timeline-day"
								data-day={day.day}
								data-weekday={day.weekDay}
							>
								<span class="text-xs">
									<span class="font-medium">{weekday(day.weekDay)}</span>
									<span class="text-muted-foreground tabular-nums">{day.day}</span>
									{#if index === 0}
										<span class="text-muted-foreground"
											>({$_('advertisingPlans.timeline.today')})</span
										>
									{/if}
								</span>
								<ol
									class="relative h-9 overflow-hidden rounded-md border bg-muted"
									aria-label={$_('advertisingPlans.timeline.dayBar', {
										values: { weekday: weekday(day.weekDay), day: day.day }
									})}
								>
									{#each day.segments as segment, position (position)}
										{@const name =
											segment.playlistName ?? $_('advertisingPlans.timeline.instructions')}
										{@const span = $_('advertisingPlans.timeline.segment', {
											values: { from: segment.from, to: segment.to, name }
										})}
										<li
											data-testid="timeline-segment"
											data-playlist={segment.playlistName ?? ''}
											data-from={segment.from}
											data-to={segment.to}
											title={span}
											class={cn(
												'absolute inset-y-0 flex min-w-0 items-center overflow-hidden border-e-2 border-card px-1.5 text-xs',
												segment.playlistName
													? 'bg-primary text-primary-foreground'
													: 'bg-status-neutral text-status-neutral-foreground'
											)}
											style:inset-inline-start="{segment.start}%"
											style:width="{segment.width}%"
										>
											<span class="truncate" aria-hidden="true">{name}</span>
											<span class="sr-only">{span}</span>
										</li>
									{/each}
								</ol>
							</li>
						{/each}
					</ol>
				</div>
			{/if}
		{/if}
	</div>
</section>
