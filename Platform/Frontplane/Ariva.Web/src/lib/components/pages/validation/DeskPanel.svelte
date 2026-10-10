<script lang="ts">
	import { Check, Square } from '@lucide/svelte';
	import { _, locale } from 'svelte-i18n';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { cn } from '$lib/utils';
	import {
		binMs,
		deskStates,
		maxDesksPerBatch,
		minuteMs,
		minutesPerBin,
		type CaptureCampaign,
		type CaptureDesk,
		type DeskObservation,
		type DeskState
	} from '$lib/core/validation';
	import DeskHistory from './DeskHistory.svelte';
	import type { ArivaClock } from './arivaClock.svelte';
	import { deskLabel, type DeskLog } from './desklog.svelte';
	import { deskStateIcon, deskStateTone } from './deskStates';
	import { duration, siteClock, siteMinute } from './format';
	import { primaryButton, secondaryButton } from './ui';

	interface Props {
		campaign: CaptureCampaign;
		log: DeskLog;
		clock: ArivaClock;
		/** Ariva's time now (the tablet's clock corrected by the measured offset), updated every second. */
		arivaNow: number;
		/** The observer's own recorded desk states, latest minute first. */
		records: DeskObservation[];
		/** Why the recorded states could not be read (plain text), or empty. */
		recordsProblem: string;
		onStart: (desks: CaptureDesk[]) => void;
		onCorrected: (observation: DeskObservation) => void;
		onReload: () => void;
	}

	let {
		campaign,
		log,
		clock,
		arivaNow,
		records,
		recordsProblem,
		onStart,
		onCorrected,
		onReload
	}: Props = $props();

	let chosen = $state<string[]>([]);
	let stopping = $state(false);

	const zone = $derived(campaign.timeZoneId);
	const clockText = (ms: number) => siteClock(ms, zone, $locale);
	const stateName = (state: DeskState) => $_(`validation.desks.states.${state}`);
	const minutes = Array.from({ length: minutesPerBin }, (_, minute) => minute);
	/** Ariva's clock is being read again (the tablet's clock jumped) or its reading is implausible: nothing is placed. */
	const held = $derived(clock.held);
	const chosenDesks = $derived(campaign.desks.filter((desk) => chosen.includes(desk.id)));
	const targetStart = $derived(log.binStart + log.target * minuteMs);

	function toggle(id: string): void {
		if (chosen.includes(id)) chosen = chosen.filter((d) => d !== id);
		else if (chosen.length < maxDesksPerBatch) chosen = [...chosen, id];
	}

	function chooseAll(): void {
		chosen = campaign.desks.slice(0, maxDesksPerBatch).map((desk) => desk.id);
	}

	function tap(deskId: string, state: DeskState): void {
		// Ariva's time, checked for a jump of the tablet's clock first; null while the log waits: nothing is placed.
		const at = clock.now();
		if (at === null) return;
		log.set(deskId, state, at);
	}

	function minuteLabel(minute: number): string {
		const time = clockText(log.binStart + minute * minuteMs);
		if (minute < log.firstOpen) return $_('validation.desks.minuteSent', { values: { time } });
		if (minute === log.current) return $_('validation.desks.minuteNow', { values: { time } });
		if (minute > log.current) return $_('validation.desks.minuteLater', { values: { time } });
		return $_('validation.desks.minuteEnded', { values: { time } });
	}
</script>

<div class="flex flex-col gap-4">
	{#if !log.logging}
		<section aria-labelledby="choose-desks" class="rounded-xl border bg-card p-5">
			<h2 id="choose-desks" class="text-xl font-semibold">{$_('validation.desks.chooseTitle')}</h2>
			<p class="mt-1 max-w-prose text-sm text-secondary-foreground">
				{$_('validation.desks.chooseHint')}
			</p>
			<ul class="mt-4 grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-4">
				{#each campaign.desks as desk (desk.id)}
					{@const on = chosen.includes(desk.id)}
					<li>
						<button
							type="button"
							data-testid="desk-option"
							aria-pressed={on}
							disabled={!on && chosen.length >= maxDesksPerBatch}
							onclick={() => toggle(desk.id)}
							class={cn(
								'flex min-h-16 w-full items-center gap-3 rounded-xl border p-3 text-start focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60',
								on ? 'border-primary bg-primary/10' : 'bg-surface-2 hover:bg-muted'
							)}
						>
							<span
								class={cn(
									'flex size-6 shrink-0 items-center justify-center rounded-md border',
									on ? 'border-primary bg-button-primary text-primary-foreground' : 'bg-card'
								)}
								aria-hidden="true"
							>
								{#if on}<Check class="size-4" />{/if}
							</span>
							<span class="flex min-w-0 flex-col">
								<span class="text-xs font-medium break-all text-secondary-foreground"
									>{desk.checkpoint}</span
								>
								<span class="text-lg font-semibold break-all" data-testid="desk-option-code"
									>{desk.code}</span
								>
							</span>
						</button>
					</li>
				{/each}
			</ul>
			<p class="mt-3 text-sm text-secondary-foreground" role="status" data-testid="desks-chosen">
				{$_('validation.desks.chosen', {
					values: { count: chosen.length, max: maxDesksPerBatch }
				})}
			</p>
			<div class="mt-3 flex flex-wrap gap-2">
				<button
					type="button"
					data-testid="start-desks"
					disabled={chosenDesks.length === 0 || held}
					onclick={() => onStart(chosenDesks)}
					class={primaryButton}
				>
					{$_('validation.desks.start')}
				</button>
				{#if campaign.desks.length <= maxDesksPerBatch}
					<button
						type="button"
						data-testid="choose-all-desks"
						onclick={chooseAll}
						class={secondaryButton}
					>
						{$_('validation.desks.chooseAll')}
					</button>
				{/if}
				<button
					type="button"
					disabled={chosen.length === 0}
					onclick={() => (chosen = [])}
					class={secondaryButton}
				>
					{$_('validation.desks.clear')}
				</button>
			</div>
			{#if log.stopped}
				<p data-testid="desks-stopped" role="status" class="mt-3 text-sm text-secondary-foreground">
					{$_('validation.desks.stopped', { values: { time: clockText(log.stopped.sentUntilMs) } })}
				</p>
			{/if}
		</section>
	{:else}
		<section aria-labelledby="desk-log-title" class="@container rounded-xl border bg-card p-5">
			<div class="flex flex-wrap items-start justify-between gap-3">
				<div class="min-w-0">
					<h2 id="desk-log-title" class="text-xl font-semibold" data-testid="desk-log-title">
						{$_('validation.desks.logging', { values: { count: log.desks.length } })}
					</h2>
					<p class="text-sm text-secondary-foreground tabular-nums" data-testid="desk-bin">
						{$_(held ? 'validation.desks.binHeld' : 'validation.desks.bin', {
							values: {
								start: clockText(log.binStart),
								end: clockText(log.binStart + binMs),
								left: duration(log.binStart + binMs - arivaNow)
							}
						})}
					</p>
				</div>
				{#if stopping}
					<div class="flex flex-wrap gap-2">
						<button
							type="button"
							data-testid="confirm-stop-desks"
							onclick={() => {
								log.stop(clock.now());
								stopping = false;
							}}
							class="inline-flex min-h-12 items-center gap-2 rounded-lg border border-status-danger-border bg-status-danger px-4 text-base font-medium text-status-danger-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
						>
							<Square class="size-4" aria-hidden="true" />
							{$_('validation.desks.stopConfirm')}
						</button>
						<button type="button" onclick={() => (stopping = false)} class={secondaryButton}>
							{$_('validation.correction.cancel')}
						</button>
					</div>
				{:else}
					<button
						type="button"
						data-testid="stop-desks"
						onclick={() => (stopping = true)}
						class={secondaryButton}
					>
						<Square class="size-4" aria-hidden="true" />
						{$_('validation.desks.stop')}
					</button>
				{/if}
			</div>

			<div
				role="group"
				aria-label={$_('validation.desks.minutes')}
				data-testid="desk-minutes"
				class="mt-4 grid grid-cols-8 gap-1 @xl:grid-cols-15"
			>
				{#each minutes as minute (minute)}
					{@const isNow = minute === log.current}
					{@const isTarget = minute === log.target}
					<button
						type="button"
						data-testid="desk-minute"
						data-minute={minute}
						aria-label={minuteLabel(minute)}
						aria-current={isNow ? 'time' : undefined}
						aria-pressed={isTarget}
						disabled={held || minute > log.current || minute < log.firstOpen}
						onclick={() => log.select(minute)}
						class={cn(
							'flex h-12 min-w-0 items-center justify-center rounded-md border text-sm font-semibold tabular-nums focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:cursor-not-allowed',
							isNow
								? 'border-primary bg-button-primary text-primary-foreground dark:text-white'
								: 'bg-surface-2 text-foreground hover:bg-muted disabled:bg-card disabled:text-muted-foreground',
							isTarget &&
								!isNow &&
								'border-status-warning-border bg-status-warning text-status-warning-foreground',
							isTarget && 'ring-2 ring-ring ring-offset-2 ring-offset-card'
						)}
					>
						{siteMinute(log.binStart + minute * minuteMs, zone)}
					</button>
				{/each}
			</div>
			{#if held}
				<p
					role="status"
					data-testid="desk-held"
					class="mt-3 rounded-lg border border-status-warning-border bg-status-warning p-3 text-base font-medium text-status-warning-foreground"
				>
					{$_('validation.desks.held')}
				</p>
			{:else}
				<p class="mt-3 text-base font-medium tabular-nums" data-testid="desk-target">
					{$_('validation.desks.target', {
						values: { start: clockText(targetStart), end: clockText(targetStart + minuteMs) }
					})}
				</p>
			{/if}
			{#if log.selected !== null}
				<div
					role="status"
					data-testid="desk-earlier"
					class="mt-2 flex flex-wrap items-center justify-between gap-2 rounded-lg border border-status-warning-border bg-status-warning p-3 text-sm text-status-warning-foreground"
				>
					<span>{$_('validation.desks.earlier', { values: { time: clockText(targetStart) } })}</span
					>
					<button
						type="button"
						data-testid="desk-back-to-now"
						onclick={() => log.select(null)}
						class={secondaryButton}
					>
						{$_('validation.desks.backToNow')}
					</button>
				</div>
			{/if}

			<ul class="mt-4 grid gap-3 @3xl:grid-cols-2">
				{#each log.desks as desk (desk.id)}
					{@const row = log.states[desk.id] ?? []}
					{@const label = deskLabel(desk)}
					{@const shown = row[log.target] ?? null}
					<li data-testid="desk-card" data-desk={label} class="rounded-xl border bg-surface-2 p-4">
						<div class="flex flex-wrap items-baseline justify-between gap-2">
							<h3 class="min-w-0 text-lg font-semibold break-all">
								<span class="text-sm font-medium text-secondary-foreground">{desk.checkpoint}</span>
								<span data-testid="desk-card-code">{desk.code}</span>
							</h3>
							<span data-testid="desk-card-state">
								{#if shown}
									<StatusBadge
										tone={shown === 'Serving'
											? 'success'
											: shown === 'Idle'
												? 'info'
												: shown === 'Paused'
													? 'warning'
													: 'neutral'}
										label={stateName(shown)}
									/>
								{:else}
									<span class="text-sm text-muted-foreground"
										>{$_('validation.desks.notLogged')}</span
									>
								{/if}
							</span>
						</div>
						<div
							role="group"
							aria-label={$_('validation.desks.stateOf', {
								values: { desk: label, time: clockText(targetStart) }
							})}
							class="mt-3 grid grid-cols-4 gap-2"
						>
							{#each deskStates as state (state)}
								{@const Icon = deskStateIcon[state]}
								{@const pressed = shown === state}
								<button
									type="button"
									data-testid="desk-state"
									data-state={state}
									aria-pressed={pressed}
									disabled={held}
									onclick={() => tap(desk.id, state)}
									class={cn(
										'flex min-h-16 touch-manipulation flex-col items-center justify-center gap-1 rounded-lg border px-1 text-center text-sm leading-tight font-medium select-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none active:scale-[0.98] disabled:cursor-not-allowed disabled:opacity-60',
										pressed
											? cn(deskStateTone[state], 'border-2')
											: 'bg-card text-foreground hover:bg-muted'
									)}
								>
									<Icon class="size-5" aria-hidden="true" />
									{stateName(state)}
								</button>
							{/each}
						</div>
						<ol
							aria-label={$_('validation.desks.strip', { values: { desk: label } })}
							data-testid="desk-strip"
							class="mt-3 grid grid-cols-15 gap-0.5"
						>
							{#each row as state, minute (minute)}
								{@const Icon = state ? deskStateIcon[state] : null}
								<li
									data-testid="desk-strip-minute"
									data-state={state ?? ''}
									class={cn(
										'flex h-7 items-center justify-center rounded-sm border',
										state ? deskStateTone[state] : 'border-dashed bg-card text-muted-foreground',
										minute === log.target && 'ring-2 ring-ring'
									)}
								>
									{#if Icon}<Icon class="size-3.5" aria-hidden="true" />{/if}
									<span class="sr-only"
										>{clockText(log.binStart + minute * minuteMs)}: {state
											? stateName(state)
											: $_('validation.desks.notObserved')}</span
									>
								</li>
							{/each}
						</ol>
					</li>
				{/each}
			</ul>

			<ul class="mt-4 flex flex-wrap gap-2 text-sm" aria-label={$_('validation.desks.legend')}>
				{#each deskStates as state (state)}
					{@const Icon = deskStateIcon[state]}
					<li
						class={cn(
							'inline-flex items-center gap-1.5 rounded-md border px-2 py-1',
							deskStateTone[state]
						)}
					>
						<Icon class="size-4" aria-hidden="true" />
						{stateName(state)}
					</li>
				{/each}
			</ul>
		</section>
	{/if}

	<DeskHistory {campaign} {records} problem={recordsProblem} {onCorrected} {onReload} />
</div>
