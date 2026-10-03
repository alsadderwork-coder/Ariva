<script lang="ts">
	import { FlaskConical } from '@lucide/svelte';
	import { _ } from 'svelte-i18n';
	import * as rules from '$lib/core/alertRules';
	import type { AlertRuleRequest, Backtest } from '$lib/core/alertRules';

	interface Props {
		/** The rule as the form holds it now (saved or not). */
		request: () => AlertRuleRequest;
	}

	let { request }: Props = $props();

	// The last whole minute and the 24 hours before it, in UTC: a range that has ended, as the backtest needs.
	const end = new Date(Math.floor(Date.now() / 60_000) * 60_000);
	let from = $state(
		rules.localOf(new Date(end.getTime() - rules.limits.backtestHours * 3_600_000))
	);
	let to = $state(rules.localOf(end));
	let result = $state<Backtest | null>(null);
	let problem = $state('');
	let busy = $state(false);

	async function preview(): Promise<void> {
		const fromUtc = rules.utcOf(from);
		const toUtc = rules.utcOf(to);
		result = null;
		problem = '';
		if (!fromUtc || !toUtc) {
			problem = $_('alertRules.preview.badRange');
			return;
		}
		busy = true;
		const answer = await rules.backtest(request(), fromUtc, toUtc);
		busy = false;
		if (answer.hasErrors || !answer.data) problem = answer.errorMessages.join(' ');
		else result = answer.data;
	}
</script>

<!-- The same evaluation the live alerts come from, on the stored minutes of the range. Server text is shown as text. -->
<section
	class="flex flex-col gap-3 rounded-lg border bg-muted/40 p-3"
	aria-labelledby="preview-title"
	data-testid="backtest"
>
	<div>
		<h3 id="preview-title" class="text-sm font-semibold">{$_('alertRules.preview.title')}</h3>
		<p class="text-xs text-muted-foreground">{$_('alertRules.preview.description')}</p>
	</div>
	<div class="flex flex-wrap items-end gap-2">
		<label class="flex min-w-0 flex-col gap-1 text-xs font-medium">
			{$_('alertRules.preview.from')}
			<input
				type="datetime-local"
				bind:value={from}
				class="h-9 rounded-md border border-input bg-background px-2 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
			/>
		</label>
		<label class="flex min-w-0 flex-col gap-1 text-xs font-medium">
			{$_('alertRules.preview.to')}
			<input
				type="datetime-local"
				bind:value={to}
				class="h-9 rounded-md border border-input bg-background px-2 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
			/>
		</label>
		<button
			type="button"
			data-testid="run-backtest"
			disabled={busy}
			onclick={preview}
			class="inline-flex h-9 items-center gap-2 rounded-md border bg-card px-3 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
		>
			<FlaskConical class="size-4" aria-hidden="true" />
			{$_('alertRules.preview.run')}
		</button>
	</div>
	<div aria-live="polite">
		{#if problem}
			<p class="text-sm text-status-danger-foreground" data-testid="backtest-problem">{problem}</p>
		{:else if result}
			<p
				class="text-sm font-medium"
				data-testid="backtest-summary"
				data-count={result.count}
				data-first={result.firstRaisedUtc ?? ''}
			>
				{#if result.count === 0}
					{$_('alertRules.preview.never')}
				{:else}
					{$_('alertRules.preview.fired', {
						values: { count: result.count, first: rules.utcText(result.firstRaisedUtc) }
					})}
				{/if}
			</p>
			<p class="text-xs text-muted-foreground">
				{$_('alertRules.preview.coverage', {
					values: { withData: result.targetsWithData, targets: result.targets }
				})}
				{#if result.truncated}{$_('alertRules.preview.truncated')}{/if}
			</p>
			{#if result.alerts.length}
				<div class="overflow-x-auto">
					<table class="w-full text-xs">
						<thead class="text-start text-muted-foreground">
							<tr>
								<th class="py-1 pe-3 text-start font-medium">{$_('alertRules.preview.zone')}</th>
								<th class="py-1 pe-3 text-start font-medium">{$_('alertRules.preview.raised')}</th>
								<th class="py-1 pe-3 text-start font-medium">{$_('alertRules.preview.cleared')}</th>
								<th class="py-1 text-end font-medium">{$_('alertRules.preview.value')}</th>
							</tr>
						</thead>
						<tbody>
							{#each result.alerts.slice(0, 20) as alert, index (index)}
								<tr class="border-t" data-testid="backtest-alert">
									<td class="py-1 pe-3">{alert.zoneName ?? alert.deviceCode ?? ''}</td>
									<td class="py-1 pe-3 tabular-nums">{rules.utcText(alert.raisedUtc)}</td>
									<td class="py-1 pe-3 tabular-nums"
										>{alert.clearedUtc
											? rules.utcText(alert.clearedUtc)
											: $_('alertRules.preview.open')}</td
									>
									<td class="py-1 text-end tabular-nums">{alert.value}</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
			{/if}
		{/if}
	</div>
</section>
