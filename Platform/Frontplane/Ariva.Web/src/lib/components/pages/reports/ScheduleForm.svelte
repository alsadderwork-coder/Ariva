<script lang="ts">
	import { untrack } from 'svelte';
	import { _ } from 'svelte-i18n';
	import FormField from '$lib/components/shared/FormField.svelte';
	import {
		limits,
		type ReportRecipient,
		type ReportSchedule,
		type ReportScheduleRequest
	} from '$lib/core/reports';

	interface Props {
		siteCode: string;
		editing: ReportSchedule | null;
		/** The accounts the site's report may go to (the server's list; it checks again on save). */
		eligible: ReportRecipient[];
		onSubmit: (request: ReportScheduleRequest) => Promise<string[]>;
		onCancel: () => void;
	}

	let { siteCode, editing, eligible, onSubmit, onCancel }: Props = $props();

	// The form starts from the schedule being edited; it is remounted (keyed) for another one.
	const start = untrack(() => editing);
	let name = $state(start?.name ?? '');
	let sendAt = $state(start?.sendAt ?? '06:00');
	let enabled = $state(start?.enabled ?? true);
	let chosen = $state<string[]>(start?.recipients.map((r) => r.id) ?? []);
	let errors = $state<string[]>([]);
	let saving = $state(false);
	let filter = $state('');

	/**
	 * Accounts already on the schedule that the site may no longer send to (a lost role or site) are kept visible so
	 * removing them is possible; the server refuses to save them, and skips them at delivery meanwhile.
	 */
	const listed = $derived.by(() => {
		const known = new Map(eligible.map((r) => [r.id, r]));
		for (const r of editing?.recipients ?? []) if (!known.has(r.id)) known.set(r.id, r);
		return [...known.values()].sort((a, b) => a.userName.localeCompare(b.userName));
	});
	const eligibleIds = $derived(new Set(eligible.map((r) => r.id)));
	const shown = $derived(
		filter.trim()
			? listed.filter((r) =>
					`${r.userName} ${r.displayName ?? ''}`.toLowerCase().includes(filter.trim().toLowerCase())
				)
			: listed
	);

	function toggle(id: string, on: boolean): void {
		chosen = on ? [...new Set([...chosen, id])] : chosen.filter((c) => c !== id);
	}

	function check(): string[] {
		const problems: string[] = [];
		const trimmed = name.trim();
		if (!trimmed || trimmed.length > limits.name)
			problems.push($_('reports.schedules.errors.name'));
		if (!/^([01]\d|2[0-3]):[0-5]\d$/.test(sendAt))
			problems.push($_('reports.schedules.errors.sendAt'));
		if (chosen.length === 0 || chosen.length > limits.recipients)
			problems.push(
				$_('reports.schedules.errors.recipients', { values: { max: limits.recipients } })
			);
		return problems;
	}

	async function submit(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		errors = check();
		if (errors.length) return;
		saving = true;
		errors = await onSubmit({
			siteCode,
			name: name.trim(),
			template: 'DailyPeaks',
			sendAt,
			recipientIds: chosen,
			enabled
		});
		saving = false;
	}
</script>

<form
	class="flex flex-col gap-4 rounded-xl border bg-card p-4"
	data-testid="schedule-form"
	aria-labelledby="schedule-form-title"
	onsubmit={submit}
	novalidate
>
	<h2 id="schedule-form-title" class="text-base font-semibold">
		{editing ? $_('reports.schedules.editTitle') : $_('reports.schedules.addTitle')}
	</h2>

	<div class="grid gap-3 sm:grid-cols-[minmax(0,2fr)_minmax(0,1fr)_minmax(0,1fr)]">
		<FormField
			id="schedule-name"
			label={$_('reports.schedules.fields.name')}
			bind:value={name}
			required
			maxlength={limits.name}
		/>
		<div class="flex min-w-0 flex-col gap-1">
			<label for="schedule-send-at" class="text-xs font-medium"
				>{$_('reports.schedules.fields.sendAt')}</label
			>
			<input
				id="schedule-send-at"
				type="time"
				step="60"
				required
				bind:value={sendAt}
				aria-describedby="schedule-send-at-hint"
				class="h-9 rounded-md border border-input bg-background px-2.5 text-sm tabular-nums focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
			/>
			<p id="schedule-send-at-hint" class="text-xs text-muted-foreground">
				{$_('reports.schedules.sendAtHint')}
			</p>
		</div>
		<div class="flex min-w-0 flex-col gap-1">
			<span class="text-xs font-medium">{$_('reports.schedules.fields.template')}</span>
			<span class="flex h-9 items-center rounded-md border bg-muted px-2.5 text-sm"
				>{$_('reports.schedules.templates.DailyPeaks')}</span
			>
		</div>
	</div>

	<fieldset class="flex flex-col gap-2">
		<legend class="text-xs font-medium">
			{$_('reports.schedules.fields.recipients', {
				values: { count: chosen.length, max: limits.recipients }
			})}
		</legend>
		<p class="text-xs text-muted-foreground">{$_('reports.schedules.recipientsHint')}</p>
		{#if listed.length > 8}
			<label class="sr-only" for="recipient-filter">{$_('reports.schedules.filter')}</label>
			<input
				id="recipient-filter"
				type="search"
				bind:value={filter}
				placeholder={$_('reports.schedules.filter')}
				class="h-9 max-w-sm rounded-md border border-input bg-background px-2.5 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
			/>
		{/if}
		{#if listed.length === 0}
			<p class="text-sm text-muted-foreground" data-testid="no-recipients">
				{$_('reports.schedules.noRecipients')}
			</p>
		{:else}
			<ul
				class="grid max-h-64 gap-1 overflow-auto rounded-md border p-2 sm:grid-cols-2"
				data-testid="recipient-list"
			>
				{#each shown as recipient (recipient.id)}
					<li>
						<label class="flex items-start gap-2 rounded px-1.5 py-1 text-sm hover:bg-accent">
							<input
								type="checkbox"
								class="mt-0.5"
								data-testid="recipient"
								data-user={recipient.userName}
								checked={chosen.includes(recipient.id)}
								disabled={!chosen.includes(recipient.id) &&
									(chosen.length >= limits.recipients || !eligibleIds.has(recipient.id))}
								onchange={(e) => toggle(recipient.id, e.currentTarget.checked)}
							/>
							<span class="min-w-0">
								<span class="block truncate">{recipient.displayName || recipient.userName}</span>
								{#if recipient.displayName && recipient.displayName !== recipient.userName}
									<span class="block truncate font-mono text-xs text-muted-foreground"
										>{recipient.userName}</span
									>
								{/if}
							</span></label
						>
					</li>
				{/each}
			</ul>
		{/if}
	</fieldset>

	<label class="flex items-center gap-2 text-sm">
		<input type="checkbox" bind:checked={enabled} data-testid="schedule-enabled" />
		{$_('reports.schedules.fields.enabled')}
	</label>

	{#if errors.length}
		<ul
			class="rounded-md border border-status-danger-border bg-status-danger px-3 py-2 text-sm text-status-danger-foreground"
			role="alert"
			data-testid="schedule-errors"
		>
			{#each errors as error, index (index)}<li>{error}</li>{/each}
		</ul>
	{/if}

	<div class="flex flex-wrap gap-2">
		<button
			type="submit"
			data-testid="save-schedule"
			disabled={saving}
			class="inline-flex h-10 items-center rounded-lg bg-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
		>
			{$_('reports.schedules.save')}
		</button>
		<button
			type="button"
			onclick={onCancel}
			class="inline-flex h-10 items-center rounded-lg border bg-card px-4 text-sm hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			{$_('reports.schedules.cancel')}
		</button>
	</div>
</form>
