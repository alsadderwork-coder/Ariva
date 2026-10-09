<script lang="ts">
	import { Clock, LogIn, LogOut, UserX } from '@lucide/svelte';
	import { _, locale } from 'svelte-i18n';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import type { CaptureCampaign, TracerRun } from '$lib/core/validation';
	import { Tracers, type JoinProblem, type RunPayload } from './capture.svelte';
	import { duration, offsetSeconds, siteClockSeconds, siteDayClock } from './format';
	import { field, primaryButton, secondaryButton } from './ui';

	interface Props {
		campaign: CaptureCampaign;
		tracers: Tracers;
		now: number;
		/** The observer's own recorded runs, latest join first. */
		recorded: TracerRun[];
		/** The tablet's clock against Ariva's from the last batch Ariva recorded (device minus server), and when. */
		offset: { ms: number; at: number } | null;
		onFinished: (payload: RunPayload) => void;
	}

	let { campaign, tracers, now, recorded, offset, onFinished }: Props = $props();

	let code = $state('');
	let zoneId = $state('');
	let problem = $state<JoinProblem | null>(null);
	let cancelling = $state<number | null>(null);

	const zone = $derived(campaign.timeZoneId);
	const chosenZone = $derived(
		campaign.zones.length === 1 ? campaign.zones[0] : campaign.zones.find((z) => z.id === zoneId)
	);
	const measured = $derived(offset ? offsetSeconds(offset.ms) : null);
	const offsetLarge = $derived(offset !== null && Math.abs(offset.ms) > 60_000);

	function join(event: SubmitEvent): void {
		event.preventDefault();
		problem = tracers.join(code, chosenZone, Date.now());
		if (!problem) code = '';
	}

	function finish(id: number, abandoned: boolean): void {
		const payload = tracers.finish(id, abandoned, Date.now());
		if (!payload) return;
		problem = null;
		onFinished(payload);
	}
</script>

<div class="flex flex-col gap-4">
	<section
		aria-labelledby="clock-title"
		data-testid="clock-offset"
		class="flex items-start gap-3 rounded-xl border bg-card p-5"
	>
		<Clock class="mt-0.5 size-5 shrink-0 text-primary" aria-hidden="true" />
		<div class="min-w-0">
			<h2 id="clock-title" class="text-base font-semibold">
				{$_('validation.tracers.clockTitle')}
			</h2>
			{#if offset && measured}
				<p class="text-sm tabular-nums" data-testid="clock-offset-value">
					{$_('validation.tracers.offset', {
						values: {
							sign: measured.sign,
							seconds: measured.seconds,
							time: siteClockSeconds(offset.at, zone, $locale)
						}
					})}
				</p>
				{#if offsetLarge}
					<p class="mt-1 text-sm text-status-warning-foreground" data-testid="clock-offset-large">
						{$_('validation.tracers.offsetLarge')}
					</p>
				{/if}
			{:else}
				<p class="text-sm text-secondary-foreground" data-testid="clock-offset-pending">
					{$_('validation.tracers.notMeasured')}
				</p>
			{/if}
		</div>
	</section>

	<section aria-labelledby="join-title" class="rounded-xl border bg-card p-5">
		<h2 id="join-title" class="text-xl font-semibold">{$_('validation.tracers.joinTitle')}</h2>
		<form
			onsubmit={join}
			class="mt-3 grid items-end gap-3 sm:grid-cols-[9rem_minmax(0,1fr)_auto]"
			novalidate
		>
			<div class="flex min-w-0 flex-col gap-1">
				<label for="tracer-code" class="text-sm font-medium">{$_('validation.tracers.code')}</label>
				<input
					id="tracer-code"
					data-testid="tracer-code"
					bind:value={code}
					maxlength="5"
					autocomplete="off"
					autocapitalize="characters"
					spellcheck="false"
					placeholder="T-07"
					aria-describedby="tracer-code-hint"
					aria-invalid={problem === 'code' || problem === 'active'}
					class="{field} uppercase"
				/>
			</div>
			{#if campaign.zones.length > 1}
				<div class="flex min-w-0 flex-col gap-1">
					<label for="tracer-zone" class="text-sm font-medium"
						>{$_('validation.tracers.zone')}</label
					>
					<select
						id="tracer-zone"
						data-testid="tracer-zone"
						bind:value={zoneId}
						aria-invalid={problem === 'zone'}
						class={field}
					>
						<option value="" disabled></option>
						{#each campaign.zones as option (option.id)}
							<option value={option.id}>{option.name}</option>
						{/each}
					</select>
				</div>
			{:else if campaign.zones.length === 1}
				<p class="min-w-0 text-sm sm:pb-3">
					<span class="font-medium">{$_('validation.tracers.zone')}:</span>
					<span data-testid="tracer-zone-name">{campaign.zones[0].name}</span>
				</p>
			{/if}
			<button type="submit" data-testid="tracer-join" class="{primaryButton} sm:min-w-32">
				<LogIn class="size-5 rtl:-scale-x-100" aria-hidden="true" />
				{$_('validation.tracers.join')}
			</button>
		</form>
		<p id="tracer-code-hint" class="mt-2 text-sm text-muted-foreground">
			{$_('validation.tracers.codeHint')}
		</p>
		{#if problem}
			<p
				role="alert"
				data-testid="tracer-problem"
				class="mt-2 text-sm text-status-danger-foreground"
			>
				{$_(`validation.tracers.problems.${problem}`)}
			</p>
		{/if}
	</section>

	<section aria-labelledby="active-title" class="rounded-xl border bg-card p-5">
		<h2 id="active-title" class="text-xl font-semibold">{$_('validation.tracers.activeTitle')}</h2>
		{#if tracers.active.length === 0}
			<p class="mt-2 text-sm text-muted-foreground">{$_('validation.tracers.activeEmpty')}</p>
		{:else}
			<ul class="mt-3 grid gap-3 lg:grid-cols-2">
				{#each tracers.active as run (run.id)}
					{@const tooLong = Tracers.tooLong(run, now)}
					<li
						data-testid="active-run"
						class="flex flex-col gap-3 rounded-xl border bg-surface-2 p-4"
					>
						<div class="flex flex-wrap items-baseline justify-between gap-2">
							<p class="text-2xl font-bold" data-testid="active-code">{run.code}</p>
							<p class="text-xl font-semibold tabular-nums" data-testid="active-elapsed">
								{$_('validation.tracers.elapsed', {
									values: { time: duration(now - run.joinedMs) }
								})}
							</p>
						</div>
						<p class="text-sm text-secondary-foreground">
							{$_('validation.tracers.joinedAt', {
								values: { time: siteClockSeconds(run.joinedMs, zone, $locale), zone: run.zone.name }
							})}
						</p>
						{#if tooLong}
							<p class="text-sm text-status-danger-foreground">
								{$_('validation.tracers.tooLong')}
							</p>
						{/if}
						<div class="grid grid-cols-2 gap-2">
							<button
								type="button"
								data-testid="tracer-exit"
								disabled={tooLong || now - run.joinedMs < 1000}
								aria-label={$_('validation.tracers.exitLabel', { values: { code: run.code } })}
								onclick={() => finish(run.id, false)}
								class="{primaryButton} min-h-16 text-lg"
							>
								<LogOut class="size-5 rtl:-scale-x-100" aria-hidden="true" />
								{$_('validation.tracers.exit')}
							</button>
							<button
								type="button"
								data-testid="tracer-abandon"
								disabled={tooLong || now - run.joinedMs < 1000}
								aria-label={$_('validation.tracers.abandonLabel', { values: { code: run.code } })}
								onclick={() => finish(run.id, true)}
								class="{secondaryButton} min-h-16"
							>
								<UserX class="size-5" aria-hidden="true" />
								{$_('validation.tracers.abandon')}
							</button>
						</div>
						{#if cancelling === run.id}
							<div class="flex flex-wrap gap-2">
								<button
									type="button"
									data-testid="confirm-cancel-run"
									onclick={() => {
										tracers.cancel(run.id);
										cancelling = null;
									}}
									class="inline-flex min-h-12 items-center rounded-lg border border-status-danger-border bg-status-danger px-4 text-base font-medium text-status-danger-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
								>
									{$_('validation.tracers.cancelConfirm')}
								</button>
								<button type="button" onclick={() => (cancelling = null)} class={secondaryButton}>
									{$_('validation.correction.cancel')}
								</button>
							</div>
						{:else}
							<button
								type="button"
								data-testid="cancel-run"
								onclick={() => (cancelling = run.id)}
								class="self-start text-sm font-medium text-secondary-foreground underline-offset-4 hover:underline focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
							>
								{$_('validation.tracers.cancel')}
							</button>
						{/if}
					</li>
				{/each}
			</ul>
		{/if}
	</section>

	<section aria-labelledby="recorded-title" class="rounded-xl border bg-card p-5">
		<h2 id="recorded-title" class="text-xl font-semibold">
			{$_('validation.tracers.recordedTitle')}
		</h2>
		{#if recorded.length === 0}
			<p class="mt-2 text-sm text-muted-foreground">{$_('validation.tracers.recordedEmpty')}</p>
		{:else}
			<div class="mt-3 overflow-x-auto rounded-lg border">
				<table class="w-full text-sm" data-testid="recorded-runs">
					<caption class="sr-only">{$_('validation.tracers.recordedCaption')}</caption>
					<thead class="bg-surface-2">
						<tr>
							{#each ['code', 'zone', 'joined', 'wait', 'outcome', 'offset'] as column (column)}
								<th
									scope="col"
									class="p-4 text-start text-xs font-semibold tracking-wide text-tertiary uppercase"
									>{$_(`validation.tracers.columns.${column}`)}</th
								>
							{/each}
						</tr>
					</thead>
					<tbody>
						{#each recorded as run (run.id)}
							{@const runOffset = offsetSeconds(run.clockOffsetMs)}
							<tr
								data-testid="recorded-run"
								class="odd:bg-surface even:bg-surface-2/50 hover:bg-muted"
							>
								<td class="px-4 py-3 font-medium" data-testid="recorded-code">{run.tracerCode}</td>
								<td class="px-4 py-3 break-all">{run.zoneName}</td>
								<td class="px-4 py-3 whitespace-nowrap tabular-nums"
									>{siteDayClock(Date.parse(run.joinedUtc), zone, $locale)}</td
								>
								<td class="px-4 py-3 tabular-nums" data-testid="recorded-wait"
									>{duration(run.waitSeconds * 1000)}</td
								>
								<td class="px-4 py-3">
									<StatusBadge
										tone={run.abandoned ? 'warning' : 'success'}
										label={$_(
											run.abandoned ? 'validation.tracers.abandoned' : 'validation.tracers.served'
										)}
									/>
								</td>
								<td class="px-4 py-3 whitespace-nowrap tabular-nums"
									>{runOffset.sign}{runOffset.seconds} s</td
								>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		{/if}
	</section>
</div>
