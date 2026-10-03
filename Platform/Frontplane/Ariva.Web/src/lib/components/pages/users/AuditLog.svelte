<script lang="ts">
	import { ChevronLeft, ChevronRight, X } from '@lucide/svelte';
	import { untrack } from 'svelte';
	import { _ } from 'svelte-i18n';
	import * as users from '$lib/core/users';
	import type { AuditEntry } from '$lib/core/users';

	interface Props {
		/** Entries about one account (targetId) or by one account (actorId), with the words for the chip. */
		filter: { targetId?: string; actorId?: string; label: string } | null;
		onClearFilter: () => void;
	}

	let { filter, onClearFilter }: Props = $props();

	const pageSize = 25;
	let action = $state('');
	let from = $state('');
	let to = $state('');
	let pageIndex = $state(1);
	let entries = $state<AuditEntry[]>([]);
	let total = $state(0);
	let problems = $state<string[]>([]);
	let loading = $state(true);
	let request = 0;

	const pages = $derived(Math.max(1, Math.ceil(total / pageSize)));

	async function load(): Promise<void> {
		const mine = ++request;
		loading = true;
		problems = [];
		const fromDate = from ? users.utcDay(from) : null;
		const toDate = to ? users.utcDay(to, true) : null;
		const result = await users.auditEntries({
			action,
			actorId: filter?.actorId ?? null,
			targetId: filter?.targetId ?? null,
			fromDate,
			toDate,
			pageIndex,
			pageSize
		});
		// A late answer for criteria no longer on the screen is dropped.
		if (mine !== request) return;
		if (result.hasErrors) problems = result.errorMessages;
		entries = result.data?.data ?? [];
		total = result.data?.totalCount ?? 0;
		loading = false;
	}

	function search(event: SubmitEvent): void {
		event.preventDefault();
		pageIndex = 1;
		void load();
	}

	function go(next: number): void {
		pageIndex = Math.min(Math.max(1, next), pages);
		void load();
	}

	// The first load, and again from the first page whenever the account filter changes.
	$effect(() => {
		void filter;
		untrack(() => {
			pageIndex = 1;
			void load();
		});
	});
</script>

<section class="flex flex-col gap-3" aria-labelledby="audit-title" data-testid="audit-log">
	<h2 id="audit-title" class="sr-only">{$_('users.audit.title')}</h2>
	<form
		class="flex flex-wrap items-end gap-2"
		onsubmit={search}
		aria-label={$_('users.audit.filters')}
	>
		<label class="flex flex-col gap-1 text-xs font-medium">
			{$_('users.audit.action')}
			<input
				bind:value={action}
				list="audit-actions"
				maxlength={64}
				dir="ltr"
				class="h-9 w-56 rounded-md border border-input bg-background px-2 font-mono text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
			/>
			<datalist id="audit-actions">
				{#each users.userActions as known (known)}<option value={known}></option>{/each}
			</datalist>
		</label>
		<label class="flex flex-col gap-1 text-xs font-medium">
			{$_('users.audit.from')}
			<input
				type="date"
				bind:value={from}
				class="h-9 rounded-md border border-input bg-background px-2 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
			/>
		</label>
		<label class="flex flex-col gap-1 text-xs font-medium">
			{$_('users.audit.to')}
			<input
				type="date"
				bind:value={to}
				class="h-9 rounded-md border border-input bg-background px-2 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
			/>
		</label>
		<button
			type="submit"
			data-testid="audit-search"
			class="inline-flex h-9 items-center rounded-md bg-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			{$_('users.actions.search')}
		</button>
		{#if filter}
			<span
				class="inline-flex h-9 items-center gap-1.5 rounded-full border bg-muted px-3 text-sm"
				data-testid="audit-filter"
			>
				{filter.label}
				<button
					type="button"
					onclick={onClearFilter}
					aria-label={$_('users.audit.clearFilter')}
					class="inline-flex size-6 items-center justify-center rounded-full hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
				>
					<X class="size-3.5" aria-hidden="true" />
				</button>
			</span>
		{/if}
	</form>
	<p class="text-xs text-muted-foreground">{$_('users.audit.utcNote')}</p>

	{#if problems.length}
		<ul class="list-disc ps-5 text-sm text-status-danger-foreground" role="alert">
			{#each problems as problem, index (index)}<li>{problem}</li>{/each}
		</ul>
	{/if}

	<div class="overflow-hidden rounded-xl border bg-card">
		{#if !loading && entries.length === 0}
			<p class="px-4 py-6 text-sm text-muted-foreground" data-testid="audit-none">
				{$_('users.audit.none')}
			</p>
		{:else}
			<div class="overflow-x-auto">
				<table class="w-full text-sm" data-testid="audit-table" aria-busy={loading}>
					<thead class="bg-muted/50 text-xs text-muted-foreground">
						<tr>
							<th class="px-4 py-2 text-start font-medium">{$_('users.audit.when')}</th>
							<th class="px-4 py-2 text-start font-medium">{$_('users.audit.actor')}</th>
							<th class="px-4 py-2 text-start font-medium">{$_('users.audit.action')}</th>
							<th class="px-4 py-2 text-start font-medium">{$_('users.audit.target')}</th>
							<th class="px-4 py-2 text-start font-medium">{$_('users.audit.change')}</th>
							<th class="px-4 py-2 text-start font-medium">{$_('users.audit.source')}</th>
						</tr>
					</thead>
					<tbody>
						{#each entries as entry (entry.id)}
							<tr class="border-t align-top" data-testid="audit-row" data-action={entry.action}>
								<td class="px-4 py-2 text-xs whitespace-nowrap tabular-nums" dir="ltr"
									>{users.utcText(entry.occurredOn)}</td
								>
								<td class="px-4 py-2 font-mono text-xs break-all"
									>{entry.actorName ?? $_('users.audit.system')}</td
								>
								<td class="px-4 py-2 font-mono text-xs" dir="ltr">{entry.action}</td>
								<td class="px-4 py-2 text-xs">
									<div class="break-all">{entry.targetName ?? ''}</div>
									{#if entry.targetType}<div class="text-muted-foreground">
											{entry.targetType}
										</div>{/if}
								</td>
								<td class="max-w-md px-4 py-2 text-xs">
									{#if entry.beforeSummary || entry.afterSummary}
										<!-- Summaries are text the server wrote from the record; shown as text, never markup. -->
										<details>
											<summary class="cursor-pointer text-muted-foreground"
												>{$_('users.audit.show')}</summary
											>
											<dl class="mt-1 flex flex-col gap-1">
												{#if entry.beforeSummary}
													<dt class="font-medium">{$_('users.audit.before')}</dt>
													<dd
														class="font-mono break-all whitespace-pre-wrap"
														dir="ltr"
														data-testid="audit-before"
													>
														{entry.beforeSummary}
													</dd>
												{/if}
												{#if entry.afterSummary}
													<dt class="font-medium">{$_('users.audit.after')}</dt>
													<dd
														class="font-mono break-all whitespace-pre-wrap"
														dir="ltr"
														data-testid="audit-after"
													>
														{entry.afterSummary}
													</dd>
												{/if}
											</dl>
										</details>
									{/if}
								</td>
								<td class="px-4 py-2 font-mono text-[11px] text-muted-foreground" dir="ltr">
									<div>{entry.ipAddress ?? ''}</div>
									{#if entry.traceId}<div class="break-all" title={$_('users.audit.trace')}>
											{entry.traceId}
										</div>{/if}
								</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		{/if}
	</div>

	<div class="flex items-center justify-between gap-2 text-sm">
		<span class="text-muted-foreground tabular-nums" data-testid="audit-count">
			{$_('users.audit.count', { values: { total, page: pageIndex, pages } })}
		</span>
		<span class="flex gap-1.5">
			<button
				type="button"
				disabled={pageIndex <= 1 || loading}
				onclick={() => go(pageIndex - 1)}
				aria-label={$_('users.actions.previous')}
				class="inline-flex size-9 items-center justify-center rounded-md border bg-card hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-50"
			>
				<ChevronLeft class="size-4 rtl:rotate-180" aria-hidden="true" />
			</button>
			<button
				type="button"
				disabled={pageIndex >= pages || loading}
				onclick={() => go(pageIndex + 1)}
				aria-label={$_('users.actions.next')}
				class="inline-flex size-9 items-center justify-center rounded-md border bg-card hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-50"
			>
				<ChevronRight class="size-4 rtl:rotate-180" aria-hidden="true" />
			</button>
		</span>
	</div>
</section>
