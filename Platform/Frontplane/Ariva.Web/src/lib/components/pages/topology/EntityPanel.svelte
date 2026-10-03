<script lang="ts">
	import { Trash2, X } from '@lucide/svelte';
	import { untrack } from 'svelte';
	import { _ } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import FormField from '$lib/components/shared/FormField.svelte';
	import * as topology from '$lib/core/topology';
	import type { EntityName } from '$lib/core/topology';
	import DeskMappings from './DeskMappings.svelte';
	import LaneChecks from './LaneChecks.svelte';

	/** Any topology entity's fields, as the API returns them. */
	interface Item {
		id: string;
		code?: string;
		iataCode?: string;
		icaoCode?: string | null;
		name?: string | null;
		timeZoneId?: string;
		floorNumber?: number;
		widthMetres?: number;
		depthMetres?: number;
		kind?: string;
		laneCategories?: string[];
		inService?: boolean;
		siteCode?: string;
	}

	interface Props {
		entity: EntityName;
		item: Item;
		canEdit: boolean;
		canDelete: boolean;
		onClose: () => void;
		onSaved: () => void | Promise<void>;
		onDeleted: () => void | Promise<void>;
		onFailed: (reason: string) => void;
	}

	let { entity, item, canEdit, canDelete, onClose, onSaved, onDeleted, onFailed }: Props = $props();

	// The panel is keyed by the item, so the form starts from the item shown and is edited locally until saved.
	const start = untrack(() => item);
	const code = start.code ?? start.iataCode ?? '';

	let name = $state(start.name ?? '');
	let icaoCode = $state(start.icaoCode ?? '');
	let timeZoneId = $state(start.timeZoneId ?? '');
	let floorNumber = $state(start.floorNumber ?? 0);
	let widthMetres = $state(start.widthMetres ?? 0);
	let depthMetres = $state(start.depthMetres ?? 0);
	let lanes = $state<string[]>([...(start.laneCategories ?? [])]);
	let inService = $state(start.inService ?? true);
	let confirming = $state(false);
	let busy = $state(false);

	function body(): unknown {
		switch (entity) {
			case 'airport':
				return {
					icaoCode: icaoCode.trim().toUpperCase() || null,
					name: name.trim(),
					timeZoneId: timeZoneId.trim()
				};
			case 'level':
				return { name: name.trim(), floorNumber, widthMetres, depthMetres };
			case 'desk':
				return { name: name.trim() || null, laneCategories: lanes, inService };
			default:
				return { name: name.trim() };
		}
	}

	async function save(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		if (busy) return;
		busy = true;
		const result = await topology.update(entity, item.id, body());
		busy = false;
		if (result.hasErrors) return onFailed(result.errorMessages[0] ?? '');
		toast.success($_('topology.messages.saved'));
		await onSaved();
	}

	async function remove(): Promise<void> {
		if (busy) return;
		busy = true;
		const result = await topology.remove(entity, item.id);
		busy = false;
		confirming = false;
		if (result.hasErrors) return onFailed(result.errorMessages[0] ?? '');
		toast.success($_('topology.messages.deleted', { values: { code } }));
		await onDeleted();
	}

	const facts = $derived.by(() => {
		const rows: [string, string][] = [];
		if (item.kind)
			rows.push([
				$_('topology.fields.kind'),
				$_(`topology.kinds.${item.kind}`, { default: item.kind })
			]);
		if (item.siteCode) rows.push([$_('topology.fields.siteCode'), item.siteCode]);
		return rows;
	});
</script>

<section
	data-testid="entity-panel"
	aria-labelledby="entity-panel-title"
	class="mt-4 rounded-xl border bg-card p-5"
>
	<header class="mb-4 flex items-start justify-between gap-3">
		<div class="min-w-0">
			<p class="text-xs font-semibold tracking-wide text-tertiary uppercase">
				{$_(`topology.columns.${entity}`)}
			</p>
			<h2 id="entity-panel-title" class="truncate text-xl font-semibold">
				<span class="font-mono">{code}</span>
				{item.name ?? ''}
			</h2>
		</div>
		<button
			type="button"
			onclick={onClose}
			aria-label={$_('topology.actions.close')}
			class="inline-flex size-8 items-center justify-center rounded-md text-muted-foreground hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			<X class="size-4" aria-hidden="true" />
		</button>
	</header>

	{#if facts.length}
		<dl class="mb-4 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
			{#each facts as [label, value] (label)}
				<dt class="text-muted-foreground">{label}</dt>
				<dd>{value}</dd>
			{/each}
		</dl>
	{/if}

	<div class="grid gap-6 lg:grid-cols-2">
		<form class="flex max-w-lg flex-col gap-3" onsubmit={save} data-testid="edit-{entity}">
			<fieldset disabled={!canEdit} class="flex flex-col gap-3">
				<legend class="sr-only">{$_('topology.details')}</legend>
				<FormField
					id="edit-name"
					label={$_('topology.fields.name')}
					bind:value={name}
					required={entity !== 'desk'}
					maxlength={200}
					readonly={!canEdit}
				/>
				{#if entity === 'airport'}
					<div class="grid grid-cols-2 gap-2">
						<FormField
							id="edit-icao"
							label={$_('topology.fields.icaoCode')}
							bind:value={icaoCode}
							maxlength={4}
							uppercase
							readonly={!canEdit}
						/>
						<FormField
							id="edit-zone"
							label={$_('topology.fields.timeZoneId')}
							bind:value={timeZoneId}
							required
							maxlength={64}
							readonly={!canEdit}
						/>
					</div>
				{:else if entity === 'level'}
					<div class="grid grid-cols-3 gap-2">
						<FormField
							id="edit-floor"
							label={$_('topology.fields.floorNumber')}
							type="number"
							bind:value={floorNumber}
							min={-10}
							max={50}
							readonly={!canEdit}
						/>
						<FormField
							id="edit-width"
							label={$_('topology.fields.widthMetres')}
							type="number"
							bind:value={widthMetres}
							min={0.1}
							max={2000}
							step="any"
							readonly={!canEdit}
						/>
						<FormField
							id="edit-depth"
							label={$_('topology.fields.depthMetres')}
							type="number"
							bind:value={depthMetres}
							min={0.1}
							max={2000}
							step="any"
							readonly={!canEdit}
						/>
					</div>
				{:else if entity === 'desk'}
					<LaneChecks id="edit-lanes" bind:value={lanes} />
					<label class="inline-flex items-center gap-2 text-sm">
						<input type="checkbox" bind:checked={inService} class="size-4 rounded border-input" />
						{$_('topology.fields.inService')}
					</label>
				{/if}
			</fieldset>
			{#if canEdit || canDelete}
				<div class="flex flex-wrap items-center gap-2">
					{#if canEdit}
						<button
							type="submit"
							disabled={busy}
							class="inline-flex h-9 items-center rounded-md bg-button-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60 dark:text-white"
						>
							{$_('topology.actions.save')}
						</button>
					{/if}
					{#if canDelete}
						{#if confirming}
							<!-- The confirmation is part of the page (no browser dialog). -->
							<button
								type="button"
								data-testid="confirm-delete"
								disabled={busy}
								onclick={remove}
								class="inline-flex h-9 items-center gap-2 rounded-md border border-status-danger-border bg-status-danger px-3 text-sm font-medium text-status-danger-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
							>
								<Trash2 class="size-4" aria-hidden="true" />
								{$_('topology.actions.confirmDelete', { values: { code } })}
							</button>
							<button
								type="button"
								onclick={() => (confirming = false)}
								class="inline-flex h-9 items-center rounded-md border bg-card px-3 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
							>
								{$_('topology.actions.cancel')}
							</button>
						{:else}
							<button
								type="button"
								data-testid="delete"
								onclick={() => (confirming = true)}
								class="inline-flex h-9 items-center gap-2 rounded-md border bg-card px-3 text-sm font-medium text-status-danger-foreground hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
							>
								<Trash2 class="size-4" aria-hidden="true" />
								{$_('topology.actions.delete')}
							</button>
						{/if}
					{/if}
				</div>
			{/if}
		</form>
		{#if entity === 'desk'}
			<DeskMappings deskId={item.id} />
		{/if}
	</div>
</section>
