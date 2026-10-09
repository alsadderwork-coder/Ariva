<script lang="ts">
	import { ArrowDownToLine, ArrowUpFromLine, Minus, Square } from '@lucide/svelte';
	import { _, locale } from 'svelte-i18n';
	import {
		binMs,
		type CaptureCampaign,
		type CaptureLine,
		type ManualCount
	} from '$lib/core/validation';
	import CorrectionForm from './CorrectionForm.svelte';
	import type { Tally } from './capture.svelte';
	import { duration, siteClock, siteDayClock } from './format';
	import { secondaryButton } from './ui';

	interface Props {
		campaign: CaptureCampaign;
		tally: Tally;
		now: number;
		/** The observer's own counts of the line being counted, newest bin first. */
		history: ManualCount[];
		onStart: (line: CaptureLine) => void;
		onCorrected: (count: ManualCount) => void;
		/** Reads the observer's counts again (a correction found its count changed on the server). */
		onReload: () => void;
	}

	let { campaign, tally, now, history, onStart, onCorrected, onReload }: Props = $props();

	let stopping = $state(false);
	let correcting = $state<string | null>(null);
	/** Why the last correction was not saved, once its form has closed (shown as text). */
	let notice = $state('');

	const zone = $derived(campaign.timeZoneId);
	const clock = (ms: number) => siteClock(ms, zone, $locale);
	const binText = (ms: number) =>
		$_('validation.tally.bin', { values: { start: clock(ms), end: clock(ms + binMs) } });

	function roleName(role: string): string {
		const key = `validation.tally.roles.${role}`;
		const text = $_(key);
		return text === key ? role : text;
	}

	function tap(direction: 'in' | 'out', step: 1 | -1): void {
		tally.tap(direction, step, Date.now());
	}
</script>

{#if !tally.line}
	<section aria-labelledby="choose-line" class="rounded-xl border bg-card p-5">
		<h2 id="choose-line" class="text-xl font-semibold">{$_('validation.tally.chooseLine')}</h2>
		<p class="mt-1 max-w-prose text-sm text-secondary-foreground">
			{$_('validation.tally.chooseLineHint')}
		</p>
		<ul class="mt-4 grid gap-3 sm:grid-cols-2">
			{#each campaign.lines as line (line.id)}
				<li>
					<button
						type="button"
						data-testid="line-option"
						onclick={() => onStart(line)}
						class="flex min-h-20 w-full flex-col items-start justify-center gap-1 rounded-xl border bg-surface-2 p-4 text-start hover:bg-muted focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
					>
						<span class="text-lg font-semibold break-all">{line.name}</span>
						<span class="text-sm text-secondary-foreground">
							{$_('validation.tally.lineRole', {
								values: { role: roleName(line.role), zone: line.queueZone }
							})}
						</span>
						<span class="text-sm font-medium text-primary">{$_('validation.tally.start')}</span>
					</button>
				</li>
			{/each}
		</ul>
	</section>
{:else}
	{@const line = tally.line}
	<section aria-labelledby="counting-title" class="rounded-xl border bg-card p-5">
		<div class="flex flex-wrap items-start justify-between gap-3">
			<div class="min-w-0">
				<h2 id="counting-title" data-testid="counting-line" class="text-xl font-semibold break-all">
					{$_('validation.tally.counting', { values: { line: line.name } })}
				</h2>
				<p class="text-sm text-secondary-foreground">
					{$_('validation.tally.lineRole', {
						values: { role: roleName(line.role), zone: line.queueZone }
					})}
				</p>
			</div>
			{#if stopping}
				<div class="flex flex-wrap gap-2">
					<button
						type="button"
						data-testid="confirm-stop-counting"
						onclick={() => {
							tally.stop();
							stopping = false;
						}}
						class="inline-flex min-h-12 items-center gap-2 rounded-lg border border-status-danger-border bg-status-danger px-4 text-base font-medium text-status-danger-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
					>
						<Square class="size-4" aria-hidden="true" />
						{$_('validation.tally.stopConfirm')}
					</button>
					<button type="button" onclick={() => (stopping = false)} class={secondaryButton}>
						{$_('validation.correction.cancel')}
					</button>
				</div>
			{:else}
				<button
					type="button"
					data-testid="stop-counting"
					onclick={() => (stopping = true)}
					class={secondaryButton}
				>
					<Square class="size-4" aria-hidden="true" />
					{$_('validation.tally.stop')}
				</button>
			{/if}
		</div>

		<div class="mt-4 flex flex-wrap items-baseline justify-between gap-2">
			<p data-testid="tally-bin" class="text-2xl font-semibold tabular-nums">
				{binText(tally.binStart)}
			</p>
			<p class="text-sm text-secondary-foreground tabular-nums" data-testid="tally-left">
				{$_('validation.tally.binHint', {
					values: { left: duration(tally.binStart + binMs - now) }
				})}
			</p>
		</div>

		{#if tally.partial}
			<p
				data-testid="part-bin"
				class="mt-3 rounded-lg border border-status-info-border bg-status-info p-3 text-sm text-status-info-foreground"
			>
				{$_('validation.tally.partBin', {
					values: { start: clock(tally.binStart), next: clock(tally.binStart + binMs) }
				})}
			</p>
		{/if}

		<div class="mt-4 grid grid-cols-2 gap-4">
			{#each [{ dir: 'in' as const, count: tally.crossingsIn, Icon: ArrowDownToLine }, { dir: 'out' as const, count: tally.crossingsOut, Icon: ArrowUpFromLine }] as pad (pad.dir)}
				<div class="flex flex-col gap-2">
					<button
						type="button"
						data-testid="tally-{pad.dir}"
						aria-label={$_(
							pad.dir === 'in' ? 'validation.tally.inCount' : 'validation.tally.outCount',
							{
								values: { count: pad.count }
							}
						)}
						onclick={() => tap(pad.dir, 1)}
						class="flex h-48 touch-manipulation flex-col items-center justify-center gap-2 rounded-2xl select-none focus-visible:ring-4 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:outline-none active:scale-[0.98] md:h-60 {pad.dir ===
						'in'
							? 'bg-button-primary text-primary-foreground dark:text-white'
							: 'bg-sidebar-primary text-sidebar-primary-foreground'}"
					>
						<span class="flex items-center gap-2 text-2xl font-semibold">
							<pad.Icon class="size-7" aria-hidden="true" />
							{$_(pad.dir === 'in' ? 'validation.tally.in' : 'validation.tally.out')}
						</span>
						<span
							class="text-7xl leading-none font-bold tabular-nums"
							data-testid="tally-{pad.dir}-count">{pad.count}</span
						>
					</button>
					<button
						type="button"
						data-testid="undo-{pad.dir}"
						disabled={pad.count === 0}
						onclick={() => tap(pad.dir, -1)}
						class={secondaryButton}
					>
						<Minus class="size-4" aria-hidden="true" />
						{$_(pad.dir === 'in' ? 'validation.tally.undoIn' : 'validation.tally.undoOut')}
					</button>
				</div>
			{/each}
		</div>
	</section>
{/if}

{#if tally.skipped}
	<p data-testid="skipped-bin" role="status" class="mt-3 text-sm text-secondary-foreground">
		{$_(
			tally.skipped.reason === 'part'
				? 'validation.tally.skippedPart'
				: 'validation.tally.skippedStopped',
			{ values: { start: clock(tally.skipped.binStartMs) } }
		)}
	</p>
{/if}

{#if tally.line}
	<section aria-labelledby="history-title" class="mt-4 rounded-xl border bg-card p-5">
		<h2 id="history-title" class="text-xl font-semibold">{$_('validation.history.title')}</h2>
		{#if notice}
			<p
				role="alert"
				data-testid="correction-notice"
				class="mt-2 rounded-lg border border-status-warning-border bg-status-warning p-3 text-sm text-status-warning-foreground"
			>
				{notice}
			</p>
		{/if}
		{#if history.length === 0}
			<p class="mt-2 text-sm text-muted-foreground" data-testid="history-empty">
				{$_('validation.history.empty')}
			</p>
		{:else}
			<div class="mt-3 overflow-x-auto rounded-lg border">
				<table class="w-full text-sm" data-testid="count-history">
					<caption class="sr-only"
						>{$_('validation.history.caption', { values: { line: tally.line.name } })}</caption
					>
					<thead class="bg-surface-2">
						<tr>
							{#each ['bin', 'in', 'out', 'revision', 'reason', 'actions'] as column (column)}
								<th
									scope="col"
									class="p-4 text-start text-xs font-semibold tracking-wide text-tertiary uppercase"
									>{$_(`validation.history.columns.${column}`)}</th
								>
							{/each}
						</tr>
					</thead>
					<tbody>
						{#each history as count (count.id)}
							{@const bin = siteDayClock(Date.parse(count.binStartUtc), zone, $locale)}
							<tr
								data-testid="history-row"
								class="odd:bg-surface even:bg-surface-2/50 hover:bg-muted"
							>
								<td class="px-4 py-3 whitespace-nowrap tabular-nums">{bin}</td>
								<td class="px-4 py-3 tabular-nums" data-testid="history-in">{count.crossingsIn}</td>
								<td class="px-4 py-3 tabular-nums" data-testid="history-out"
									>{count.crossingsOut}</td
								>
								<td class="px-4 py-3 tabular-nums">{count.revision}</td>
								<td class="px-4 py-3 break-all" data-testid="history-reason"
									>{count.reason ?? ''}</td
								>
								<td class="px-4 py-2">
									<button
										type="button"
										data-testid="correct-count"
										aria-label={$_('validation.history.correctLabel', { values: { bin } })}
										onclick={() => {
											correcting = count.id;
											notice = '';
										}}
										class={secondaryButton}
									>
										{$_('validation.history.correct')}
									</button>
								</td>
							</tr>
							{#if correcting === count.id}
								<tr>
									<td colspan="6" class="p-3">
										{#key count.id}
											<CorrectionForm
												siteCode={campaign.siteCode}
												{count}
												binLabel={bin}
												onSaved={(saved) => {
													correcting = null;
													notice = '';
													onCorrected(saved);
												}}
												onCancel={() => {
													// The outcome of a send may be unknown: the list shows what Ariva holds.
													correcting = null;
													onReload();
												}}
												onStale={(message) => {
													correcting = null;
													notice = message;
													onReload();
												}}
											/>
										{/key}
									</td>
								</tr>
							{/if}
						{/each}
					</tbody>
				</table>
			</div>
		{/if}
	</section>
{/if}
