<script lang="ts">
	import { BellRing } from '@lucide/svelte';
	import { _ } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import StatusBadge, { type StatusTone } from '$lib/components/shared/StatusBadge.svelte';
	import { auth } from '$lib/core/auth.svelte';
	import * as operations from '$lib/core/operations';
	import type { Alert } from '$lib/core/operations';
	import { knownLabels, labelOf } from './waits';

	interface Props {
		alerts: Alert[];
		onChanged: () => void | Promise<void>;
	}

	let { alerts, onChanged }: Props = $props();

	let notes = $state<Record<string, string>>({});
	let busy = $state<string | null>(null);

	const severityTone: Record<string, StatusTone> = {
		Critical: 'danger',
		Warning: 'warning',
		Info: 'info'
	};
	const canAct = $derived(auth.can('Alert.Edit'));

	function time(value: string | null): string {
		return value
			? new Date(value).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
			: '';
	}

	async function acknowledge(alert: Alert): Promise<void> {
		if (busy) return;
		busy = alert.id;
		const result = await operations.acknowledge(alert.id, (notes[alert.id] ?? '').trim());
		busy = null;
		if (result.hasErrors) {
			toast.error(
				$_('topology.messages.failed', { values: { reason: result.errorMessages[0] ?? '' } })
			);
			return;
		}
		toast.success($_('liveOperations.alerts.done'));
		await onChanged();
	}
</script>

<!-- Alerts the caller's role is responsible for (the API decides); names and notes are text. -->
<section
	class="flex flex-col gap-3 rounded-xl border bg-card p-4"
	aria-labelledby="alerts-title"
	data-testid="alerts-panel"
>
	<h2 id="alerts-title" class="flex items-center gap-2 text-base font-semibold">
		<BellRing class="size-4" aria-hidden="true" />
		{$_('liveOperations.alerts.title')}
	</h2>
	{#if alerts.length === 0}
		<p class="text-sm text-muted-foreground">{$_('liveOperations.alerts.none')}</p>
	{:else}
		<ul class="flex flex-col gap-2">
			{#each alerts as alert (alert.id)}
				<li
					data-testid="alert"
					data-rule={alert.ruleCode}
					data-state={alert.state}
					class="flex flex-col gap-2 rounded-lg border p-3"
				>
					<div class="flex flex-wrap items-center justify-between gap-2">
						<span class="text-sm font-medium"
							><span class="font-mono text-xs">{alert.ruleCode}</span> {alert.ruleName}</span
						>
						<span class="flex gap-1.5">
							<StatusBadge
								tone={severityTone[alert.severity] ?? 'neutral'}
								label={labelOf(
									$_,
									'liveOperations.alerts.severities',
									alert.severity,
									knownLabels.severities
								)}
							/>
							<StatusBadge
								tone="neutral"
								label={labelOf(
									$_,
									'liveOperations.alerts.states',
									alert.state,
									knownLabels.alertStates
								)}
							/>
						</span>
					</div>
					<p class="text-xs text-muted-foreground">
						{alert.zoneName ?? alert.deviceCode ?? ''} · {$_('liveOperations.alerts.raised', {
							values: { time: time(alert.raisedUtc) }
						})}
						{#if alert.acknowledgedUtc}· {$_('liveOperations.alerts.acknowledged', {
								values: { time: time(alert.acknowledgedUtc) }
							})}{/if}
					</p>
					{#if canAct && (alert.state === 'Raised' || alert.state === 'Escalated')}
						<form
							class="flex flex-wrap items-end gap-2"
							onsubmit={(event) => {
								event.preventDefault();
								void acknowledge(alert);
							}}
						>
							<label class="flex min-w-0 flex-1 flex-col gap-1 text-xs font-medium">
								{$_('liveOperations.alerts.note')}
								<input
									bind:value={notes[alert.id]}
									maxlength="500"
									class="h-8 rounded-md border border-input bg-background px-2 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
								/>
							</label>
							<button
								type="submit"
								disabled={busy === alert.id}
								class="inline-flex h-8 items-center rounded-md bg-button-primary px-3 text-xs font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60 dark:text-white"
							>
								{$_('liveOperations.alerts.acknowledge')}
							</button>
						</form>
					{/if}
				</li>
			{/each}
		</ul>
	{/if}
</section>
