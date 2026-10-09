<script lang="ts">
	import { CircleAlert, CloudOff, RotateCw } from '@lucide/svelte';
	import { _, locale } from 'svelte-i18n';
	import { isClockRefusal } from '$lib/core/validation';
	import type { CountPayload, RunPayload } from './capture.svelte';
	import { siteClock } from './format';
	import type { OutboxItem, OutboxView } from './outbox.svelte';
	import { primaryButton } from './ui';

	interface Props {
		counts: OutboxView<CountPayload>;
		runs: OutboxView<RunPayload>;
		timeZone: string;
		onRetry: () => void;
	}

	let { counts, runs, timeZone, onRetry }: Props = $props();

	const unsent = $derived(counts.unsent + runs.unsent);
	const sending = $derived(
		counts.items.some((i) => i.state === 'sending') || runs.items.some((i) => i.state === 'sending')
	);

	function countLabel(item: OutboxItem<CountPayload>): string {
		return $_('validation.outbox.binLabel', {
			values: {
				line: item.payload.lineName,
				bin: siteClock(item.payload.binStartMs, timeZone, $locale)
			}
		});
	}

	function runLabel(item: OutboxItem<RunPayload>): string {
		return $_('validation.outbox.runLabel', {
			values: { code: item.payload.run.tracerCode, zone: item.payload.zoneName }
		});
	}

	const refusedEntries = $derived([
		...counts.refused.map((item) => ({
			item: item as OutboxItem<unknown>,
			kind: 'count' as const,
			label: countLabel(item),
			discard: () => counts.discard(item.key)
		})),
		...runs.refused.map((item) => ({
			item: item as OutboxItem<unknown>,
			kind: 'run' as const,
			label: runLabel(item),
			discard: () => runs.discard(item.key)
		}))
	]);

	/** Where an unsent item stands: being sent, or why the last attempt did not reach Ariva. */
	function progress(item: OutboxItem<unknown>): string {
		if (item.state === 'sending') return $_('validation.outbox.sending');
		if (!item.status) return $_('validation.outbox.offline');
		return $_('validation.outbox.unavailable', { values: { status: item.status } });
	}

	function refusal(item: OutboxItem<unknown>, kind: 'count' | 'run'): string {
		if (item.status === 403) return $_('validation.outbox.ownCampaign');
		if (isClockRefusal(item.status, item.message)) return $_('validation.outbox.clock');
		return $_('validation.outbox.refused', { values: { kind, reason: item.message } });
	}
</script>

{#if unsent > 0}
	<section
		data-testid="unsent-banner"
		aria-labelledby="unsent-title"
		class="mb-4 rounded-xl border border-status-warning-border bg-status-warning p-4 text-status-warning-foreground"
	>
		<div class="flex flex-wrap items-start justify-between gap-3">
			<div class="flex min-w-0 items-start gap-3">
				<CloudOff class="mt-0.5 size-5 shrink-0" aria-hidden="true" />
				<div class="min-w-0">
					<h2 id="unsent-title" class="text-base font-semibold">{$_('validation.outbox.title')}</h2>
					<p class="text-sm" role="status" data-testid="unsent-count">
						{$_('validation.outbox.waiting', { values: { count: unsent } })}
					</p>
					<ul class="mt-2 flex flex-col gap-1 text-sm">
						{#each counts.items.filter((i) => i.state !== 'refused') as item (item.key)}
							<li data-testid="unsent-item">{countLabel(item)}: {progress(item)}</li>
						{/each}
						{#each runs.items.filter((i) => i.state !== 'refused') as item (item.key)}
							<li data-testid="unsent-item">{runLabel(item)}: {progress(item)}</li>
						{/each}
					</ul>
				</div>
			</div>
			<button
				type="button"
				data-testid="retry-unsent"
				disabled={sending}
				onclick={onRetry}
				class={primaryButton}
			>
				<RotateCw class="size-5" aria-hidden="true" />
				{$_('validation.outbox.retry')}
			</button>
		</div>
	</section>
{/if}

{#each refusedEntries as entry (entry.item.key)}
	<div
		role="alert"
		data-testid="refused-item"
		class="mb-4 flex flex-wrap items-start justify-between gap-3 rounded-xl border border-status-danger-border bg-status-danger p-4 text-status-danger-foreground"
	>
		<div class="flex min-w-0 items-start gap-3">
			<CircleAlert class="mt-0.5 size-5 shrink-0" aria-hidden="true" />
			<div class="min-w-0 text-sm">
				<p class="font-medium">{entry.label}</p>
				<p>{refusal(entry.item, entry.kind)}</p>
			</div>
		</div>
		<button
			type="button"
			data-testid="discard-refused"
			onclick={entry.discard}
			class="inline-flex min-h-11 items-center rounded-lg border bg-card px-4 text-sm font-medium text-foreground hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			{$_('validation.outbox.discard')}
		</button>
	</div>
{/each}
