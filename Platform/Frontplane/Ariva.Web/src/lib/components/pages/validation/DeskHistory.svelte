<script lang="ts">
	import { _, locale } from 'svelte-i18n';
	import { cn } from '$lib/utils';
	import {
		binMs,
		binOf,
		deskStates,
		minuteMs,
		minutesPerBin,
		type CaptureCampaign,
		type DeskObservation
	} from '$lib/core/validation';
	import DeskCorrectionForm from './DeskCorrectionForm.svelte';
	import { deskLabel } from './desklog.svelte';
	import { deskStateIcon, deskStateTone } from './deskStates';
	import { siteClock, siteDayClock, siteMinute } from './format';
	import { field } from './ui';

	interface Props {
		campaign: CaptureCampaign;
		/** The observer's own current desk states, latest minute first. */
		records: DeskObservation[];
		/** Why they could not be read (plain text), or empty. */
		problem: string;
		onCorrected: (observation: DeskObservation) => void;
		onReload: () => void;
	}

	let { campaign, records, problem, onCorrected, onReload }: Props = $props();

	let chosenBin = $state<number | null>(null);
	let correcting = $state<string | null>(null);
	/** Why the last correction was not saved, once its form has closed (shown as text). */
	let notice = $state('');

	const zone = $derived(campaign.timeZoneId);
	const clockText = (ms: number) => siteClock(ms, zone, $locale);
	const minutes = Array.from({ length: minutesPerBin }, (_, minute) => minute);

	/** Rows the grid can show: a time and one of the four states (anything else from the server is left out). */
	const usable = $derived(
		records.filter((r) => Number.isFinite(Date.parse(r.minuteUtc)) && deskStates.includes(r.state))
	);
	/** The bins with recorded states, latest first. */
	const bins = $derived(
		[...new Set(usable.map((r) => binOf(Date.parse(r.minuteUtc))))].sort((a, b) => b - a)
	);
	const bin = $derived(
		chosenBin !== null && bins.includes(chosenBin) ? chosenBin : (bins[0] ?? null)
	);
	const inBin = $derived(
		bin === null ? [] : usable.filter((r) => binOf(Date.parse(r.minuteUtc)) === bin)
	);
	/** One row per desk of the bin, its 15 minutes, in checkpoint and desk code order. */
	const rows = $derived(
		[...new Set(inBin.map((r) => r.deskId))]
			.map((deskId) => {
				const own = inBin.filter((r) => r.deskId === deskId);
				const at = (minute: number) =>
					own.find((r) => Date.parse(r.minuteUtc) === (bin ?? 0) + minute * minuteMs) ?? null;
				return {
					deskId,
					label: deskLabel({ checkpoint: own[0].checkpoint, code: own[0].deskCode }),
					minutes: minutes.map(at)
				};
			})
			.sort((a, b) => (a.label < b.label ? -1 : a.label > b.label ? 1 : 0))
	);
	const corrections = $derived(
		inBin
			.filter((r) => r.revision > 1)
			.sort((a, b) => Date.parse(a.minuteUtc) - Date.parse(b.minuteUtc))
	);
	const editing = $derived(inBin.find((r) => r.id === correcting) ?? null);

	const stateName = (state: string) => $_(`validation.desks.states.${state}`);
	const binText = (start: number) =>
		$_('validation.desks.history.binOption', {
			values: { start: siteDayClock(start, zone, $locale), end: clockText(start + binMs) }
		});
</script>

<section aria-labelledby="desk-history-title" class="rounded-xl border bg-card p-5">
	<div class="flex flex-wrap items-end justify-between gap-3">
		<h2 id="desk-history-title" class="text-xl font-semibold">
			{$_('validation.desks.history.title')}
		</h2>
		{#if bins.length > 1}
			<div class="flex flex-col gap-1">
				<label for="desk-history-bin" class="text-sm font-medium"
					>{$_('validation.desks.history.bin')}</label
				>
				<select
					id="desk-history-bin"
					data-testid="desk-history-bin"
					value={bin}
					onchange={(event) => {
						chosenBin = Number(event.currentTarget.value);
						correcting = null;
					}}
					class={field}
				>
					{#each bins as start (start)}
						<option value={start}>{binText(start)}</option>
					{/each}
				</select>
			</div>
		{/if}
	</div>
	{#if notice}
		<p
			role="alert"
			data-testid="desk-correction-notice"
			class="mt-2 rounded-lg border border-status-warning-border bg-status-warning p-3 text-sm text-status-warning-foreground"
		>
			{notice}
		</p>
	{/if}
	{#if problem}
		<p
			role="alert"
			data-testid="desk-history-problem"
			class="mt-2 text-sm text-status-danger-foreground"
		>
			{problem}
		</p>
	{:else if bin === null}
		<p class="mt-2 text-sm text-muted-foreground" data-testid="desk-history-empty">
			{$_('validation.desks.history.empty')}
		</p>
	{:else}
		<p class="mt-1 text-sm text-secondary-foreground">{$_('validation.desks.history.hint')}</p>
		<div class="mt-3 overflow-x-auto rounded-lg border">
			<table class="w-full text-sm" data-testid="desk-history">
				<caption class="sr-only"
					>{$_('validation.desks.history.caption', {
						values: { start: clockText(bin), end: clockText(bin + binMs) }
					})}</caption
				>
				<thead class="bg-surface-2">
					<tr>
						<th
							scope="col"
							class="p-4 text-start text-xs font-semibold tracking-wide text-tertiary uppercase"
							>{$_('validation.desks.history.desk')}</th
						>
						{#each minutes as minute (minute)}
							<th
								scope="col"
								class="px-1 py-4 text-center text-xs font-semibold text-tertiary tabular-nums"
							>
								<span aria-hidden="true">{siteMinute(bin + minute * minuteMs, zone)}</span>
								<span class="sr-only">{clockText(bin + minute * minuteMs)}</span>
							</th>
						{/each}
					</tr>
				</thead>
				<tbody>
					{#each rows as row (row.deskId)}
						<tr data-testid="desk-history-row" class="odd:bg-surface even:bg-surface-2/50">
							<th
								scope="row"
								class="px-4 py-2 text-start font-medium whitespace-nowrap"
								data-testid="desk-history-desk">{row.label}</th
							>
							{#each row.minutes as record, minute (minute)}
								<td class="p-0.5 text-center">
									{#if record}
										{@const Icon = deskStateIcon[record.state]}
										<button
											type="button"
											data-testid="desk-history-cell"
											data-minute={minute}
											data-state={record.state}
											aria-pressed={correcting === record.id}
											aria-label={$_('validation.desks.history.cellLabel', {
												values: {
													desk: row.label,
													time: clockText(Date.parse(record.minuteUtc)),
													state: stateName(record.state),
													revision: record.revision
												}
											})}
											onclick={() => {
												correcting = correcting === record.id ? null : record.id;
												notice = '';
											}}
											class={cn(
												'relative inline-flex size-10 items-center justify-center rounded-md border focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none',
												deskStateTone[record.state],
												correcting === record.id && 'ring-2 ring-ring ring-offset-1'
											)}
										>
											<Icon class="size-4" aria-hidden="true" />
											{#if record.revision > 1}
												<span
													aria-hidden="true"
													class="absolute end-0.5 top-0 text-[10px] leading-tight font-semibold"
													>{record.revision}</span
												>
											{/if}
										</button>
									{:else}
										<span aria-hidden="true" class="text-muted-foreground">·</span>
										<span class="sr-only">{$_('validation.desks.notObserved')}</span>
									{/if}
								</td>
							{/each}
						</tr>
					{/each}
				</tbody>
			</table>
		</div>

		{#if editing}
			{#key editing.id}
				<div class="mt-3">
					<DeskCorrectionForm
						siteCode={campaign.siteCode}
						observation={editing}
						deskText={deskLabel({ checkpoint: editing.checkpoint, code: editing.deskCode })}
						minuteText={clockText(Date.parse(editing.minuteUtc))}
						onSaved={(saved) => {
							correcting = null;
							notice = '';
							onCorrected(saved);
						}}
						onCancel={() => {
							// The outcome of a send may be unknown: the grid shows what Ariva holds.
							correcting = null;
							onReload();
						}}
						onStale={(message) => {
							correcting = null;
							notice = message;
							onReload();
						}}
					/>
				</div>
			{/key}
		{/if}

		{#if corrections.length > 0}
			<h3 class="mt-4 text-base font-semibold">{$_('validation.desks.history.corrections')}</h3>
			<div class="mt-2 overflow-x-auto rounded-lg border">
				<table class="w-full text-sm" data-testid="desk-corrections">
					<thead class="bg-surface-2">
						<tr>
							{#each ['desk', 'minute', 'state', 'revision', 'reason'] as column (column)}
								<th
									scope="col"
									class="p-4 text-start text-xs font-semibold tracking-wide text-tertiary uppercase"
									>{$_(`validation.desks.history.columns.${column}`)}</th
								>
							{/each}
						</tr>
					</thead>
					<tbody>
						{#each corrections as record (record.id)}
							<tr
								data-testid="desk-correction-row"
								class="odd:bg-surface even:bg-surface-2/50 hover:bg-muted"
							>
								<td class="px-4 py-3 whitespace-nowrap"
									>{deskLabel({ checkpoint: record.checkpoint, code: record.deskCode })}</td
								>
								<td class="px-4 py-3 whitespace-nowrap tabular-nums"
									>{clockText(Date.parse(record.minuteUtc))}</td
								>
								<td class="px-4 py-3">{stateName(record.state)}</td>
								<td class="px-4 py-3 tabular-nums">{record.revision}</td>
								<td class="px-4 py-3 break-all" data-testid="desk-correction-row-reason"
									>{record.reason ?? ''}</td
								>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		{/if}
	{/if}
</section>
