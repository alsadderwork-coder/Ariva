<script lang="ts">
	import { _ } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import FormField from '$lib/components/shared/FormField.svelte';
	import SelectField from '$lib/components/shared/SelectField.svelte';
	import * as topology from '$lib/core/topology';
	import {
		allowedDeskKinds,
		checkpointKinds,
		type CheckpointKind,
		type EntityName
	} from '$lib/core/topology';
	import LaneChecks from './LaneChecks.svelte';

	interface Props {
		entity: EntityName | 'range';
		parentId: string | null;
		siteCode: string;
		checkpointKind: CheckpointKind;
		canRange?: boolean;
		onRange?: () => void;
		onCancel: () => void;
		onCreated: () => void | Promise<void>;
		onFailed: (reason: string) => void;
	}

	let {
		entity,
		parentId,
		siteCode,
		checkpointKind,
		canRange = false,
		onRange,
		onCancel,
		onCreated,
		onFailed
	}: Props = $props();

	const browserZone = Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
	const deskKinds = $derived(allowedDeskKinds(checkpointKind));

	let code = $state('');
	let name = $state('');
	let iataCode = $state('');
	let icaoCode = $state('');
	let timeZoneId = $state(browserZone);
	let floorNumber = $state(0);
	let widthMetres = $state(100);
	let depthMetres = $state(50);
	let kind = $state<string>('Immigration');
	let deskKind = $state<string>('');
	let lanes = $state<string[]>([]);
	let prefix = $state('D');
	let from = $state(1);
	let to = $state(10);
	let width = $state(2);
	let busy = $state(false);

	$effect(() => {
		if (!deskKinds.includes(deskKind as never)) deskKind = deskKinds[0];
	});

	function body(): unknown {
		switch (entity) {
			case 'airport':
				return {
					iataCode: iataCode.trim().toUpperCase(),
					icaoCode: icaoCode.trim().toUpperCase() || null,
					name: name.trim(),
					timeZoneId: timeZoneId.trim()
				};
			case 'terminal':
				return { airportId: parentId, code: code.trim(), name: name.trim(), siteCode };
			case 'level':
				return {
					terminalId: parentId,
					code: code.trim(),
					name: name.trim(),
					floorNumber,
					widthMetres,
					depthMetres
				};
			case 'checkpoint':
				return { levelId: parentId, code: code.trim(), name: name.trim(), kind };
			case 'desk':
				return {
					checkpointId: parentId,
					code: code.trim(),
					name: name.trim() || null,
					kind: deskKind,
					laneCategories: lanes
				};
			default:
				return null;
		}
	}

	async function submit(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		if (busy) return;
		busy = true;
		try {
			if (entity === 'range') {
				const result = await topology.createDeskRange({
					checkpointId: parentId!,
					prefix: prefix.trim(),
					from,
					to,
					width,
					kind: deskKind as topology.DeskKind,
					laneCategories: lanes
				});
				if (result.hasErrors) return onFailed(result.errorMessages[0] ?? '');
				toast.success(
					$_('topology.messages.createdRange', { values: { count: result.data?.length ?? 0 } })
				);
			} else {
				const result = await topology.create<{ code?: string; iataCode?: string }>(entity, body());
				if (result.hasErrors) return onFailed(result.errorMessages[0] ?? '');
				toast.success(
					$_('topology.messages.created', {
						values: { code: result.data?.code ?? result.data?.iataCode ?? '' }
					})
				);
			}
			await onCreated();
		} finally {
			busy = false;
		}
	}
</script>

<form class="flex flex-col gap-2.5" onsubmit={submit} data-testid="create-{entity}">
	{#if entity === 'airport'}
		<div class="grid grid-cols-2 gap-2">
			<FormField
				id="new-airport-iata"
				label={$_('topology.fields.iataCode')}
				bind:value={iataCode}
				required
				maxlength={3}
				pattern="[A-Za-z]{'{'}3{'}'}"
				uppercase
			/>
			<FormField
				id="new-airport-icao"
				label={$_('topology.fields.icaoCode')}
				bind:value={icaoCode}
				maxlength={4}
				uppercase
			/>
		</div>
		<FormField
			id="new-airport-name"
			label={$_('topology.fields.name')}
			bind:value={name}
			required
			maxlength={200}
		/>
		<FormField
			id="new-airport-zone"
			label={$_('topology.fields.timeZoneId')}
			bind:value={timeZoneId}
			required
			maxlength={64}
		/>
	{:else if entity === 'range'}
		<div class="grid grid-cols-2 gap-2">
			<FormField
				id="new-range-prefix"
				label={$_('topology.fields.prefix')}
				bind:value={prefix}
				maxlength={12}
				uppercase
			/>
			<FormField
				id="new-range-width"
				label={$_('topology.fields.width')}
				type="number"
				bind:value={width}
				min={1}
				max={4}
				required
			/>
			<FormField
				id="new-range-from"
				label={$_('topology.fields.from')}
				type="number"
				bind:value={from}
				min={0}
				max={9999}
				required
			/>
			<FormField
				id="new-range-to"
				label={$_('topology.fields.to')}
				type="number"
				bind:value={to}
				min={0}
				max={9999}
				required
			/>
		</div>
		<p class="text-xs text-muted-foreground">{$_('topology.rangeHint')}</p>
	{:else}
		<div class="grid grid-cols-[6rem_1fr] gap-2">
			<FormField
				id="new-{entity}-code"
				label={$_('topology.fields.code')}
				bind:value={code}
				required
				maxlength={16}
				uppercase
			/>
			<FormField
				id="new-{entity}-name"
				label={$_('topology.fields.name')}
				bind:value={name}
				required={entity !== 'desk'}
				maxlength={200}
			/>
		</div>
	{/if}
	{#if entity === 'level'}
		<div class="grid grid-cols-3 gap-2">
			<FormField
				id="new-level-floor"
				label={$_('topology.fields.floorNumber')}
				type="number"
				bind:value={floorNumber}
				min={-10}
				max={50}
				required
			/>
			<FormField
				id="new-level-width"
				label={$_('topology.fields.widthMetres')}
				type="number"
				bind:value={widthMetres}
				min={0.1}
				max={2000}
				step="any"
				required
			/>
			<FormField
				id="new-level-depth"
				label={$_('topology.fields.depthMetres')}
				type="number"
				bind:value={depthMetres}
				min={0.1}
				max={2000}
				step="any"
				required
			/>
		</div>
	{:else if entity === 'checkpoint'}
		<SelectField
			id="new-checkpoint-kind"
			label={$_('topology.fields.kind')}
			bind:value={kind}
			options={checkpointKinds.map((k) => ({ value: k, label: $_(`topology.kinds.${k}`) }))}
		/>
	{/if}
	{#if entity === 'desk' || entity === 'range'}
		<SelectField
			id="new-{entity}-kind"
			label={$_('topology.fields.kind')}
			bind:value={deskKind}
			options={deskKinds.map((k) => ({ value: k, label: $_(`topology.kinds.${k}`) }))}
		/>
		<LaneChecks id="new-{entity}-lanes" bind:value={lanes} />
	{/if}
	<div class="flex flex-wrap items-center gap-2">
		<button
			type="submit"
			disabled={busy}
			class="inline-flex h-8 items-center rounded-md bg-button-primary px-3 text-xs font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60 dark:text-white"
		>
			{$_('topology.actions.create')}
		</button>
		<button
			type="button"
			onclick={onCancel}
			class="inline-flex h-8 items-center rounded-md border bg-card px-3 text-xs font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			{$_('topology.actions.cancel')}
		</button>
		{#if canRange}
			<button
				type="button"
				onclick={() => onRange?.()}
				class="ms-auto text-xs font-medium text-primary underline-offset-4 hover:underline focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none dark:text-foreground"
			>
				{entity === 'range' ? $_('topology.actions.add') : $_('topology.actions.addRange')}
			</button>
		{/if}
	</div>
</form>
