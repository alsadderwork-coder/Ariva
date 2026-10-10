<script lang="ts">
	import { Plus, Undo2, X } from '@lucide/svelte';
	import { _ } from 'svelte-i18n';
	import FormField from '$lib/components/shared/FormField.svelte';
	import type { Point, Zone } from '$lib/core/zones';
	import { laneCategories, maxPhysicalCapacity, round } from '$lib/core/zones';
	import ConfirmButton from '$lib/components/shared/ConfirmButton.svelte';

	interface Props {
		zone: Zone;
		points: Point[];
		queueZoneName: string | null;
		deskCode: string | null;
		editable: boolean;
		dirty: boolean;
		width: number;
		depth: number;
		name: string;
		/** The lane whose queue this is (queue zones only, ARV-057); empty for none. */
		lane: string;
		/** The physical capacity in people (queue zones and overflow bands, ARV-114a); empty for none. */
		capacity: string | number;
		onPoints: (points: Point[]) => void;
		onSave: () => void | Promise<void>;
		onRevert: () => void;
		onDelete: () => void | Promise<void>;
	}

	let {
		zone,
		points,
		queueZoneName,
		deskCode,
		editable,
		dirty,
		width,
		depth,
		name = $bindable(),
		lane = $bindable(),
		capacity = $bindable(),
		onPoints,
		onSave,
		onRevert,
		onDelete
	}: Props = $props();

	function set(index: number, axis: 'x' | 'y', value: number): void {
		if (!Number.isFinite(value)) return;
		const limit = axis === 'x' ? width : depth;
		onPoints(
			points.map((p, i) =>
				i === index ? { ...p, [axis]: round(Math.min(Math.max(value, 0), limit)) } : p
			)
		);
	}

	/** A new vertex halfway along the edge to the next one. */
	function insertAfter(index: number): void {
		const a = points[index];
		const b = points[(index + 1) % points.length];
		const next = [...points];
		next.splice(index + 1, 0, { x: round((a.x + b.x) / 2), y: round((a.y + b.y) / 2) });
		onPoints(next);
	}

	function removeAt(index: number): void {
		if (points.length > 3) onPoints(points.filter((_, i) => i !== index));
	}
</script>

<section
	data-testid="zone-details"
	aria-labelledby="zone-details-title"
	class="flex flex-col gap-3"
>
	<div>
		<p class="text-xs font-semibold tracking-wide text-tertiary uppercase">
			{$_(`zones.kinds.${zone.kind}`)}
		</p>
		<h2 id="zone-details-title" class="text-lg font-semibold">{zone.name}</h2>
		<p class="text-xs text-muted-foreground tabular-nums">
			{$_('zones.area', { values: { area: zone.areaSquareMetres.toFixed(1) } })}
			{#if queueZoneName}· {$_('zones.queueOf', { values: { name: queueZoneName } })}{/if}
			{#if deskCode}· {$_('zones.deskOf', { values: { code: deskCode } })}{/if}
		</p>
	</div>
	<FormField
		id="zone-name"
		label={$_('topology.fields.name')}
		bind:value={name}
		required
		maxlength={200}
		readonly={!editable}
	/>
	{#if zone.kind === 'Queue'}
		<div class="flex min-w-0 flex-col gap-1">
			<label for="zone-lane" class="text-xs font-medium">{$_('zones.lane')}</label>
			<select
				id="zone-lane"
				bind:value={lane}
				disabled={!editable}
				aria-describedby="zone-lane-hint"
				class="h-9 min-w-0 rounded-md border border-input bg-background px-2 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:bg-muted"
			>
				<option value="">{$_('zones.noLane')}</option>
				{#each [...new Set([...laneCategories, ...(lane ? [lane] : [])])] as code (code)}
					<option value={code}>{code}</option>
				{/each}
			</select>
			<p id="zone-lane-hint" class="text-xs text-muted-foreground">{$_('zones.laneHint')}</p>
		</div>
	{/if}
	{#if zone.kind === 'Queue' || zone.kind === 'Overflow'}
		<FormField
			id="zone-capacity"
			label={$_('zones.capacity')}
			type="number"
			bind:value={capacity}
			min={1}
			max={maxPhysicalCapacity}
			step={1}
			hint={$_('zones.capacityHint')}
			readonly={!editable}
		/>
	{/if}
	<div class="overflow-x-auto">
		<table class="w-full text-sm" data-testid="vertex-table">
			<caption class="mb-1 text-start text-xs font-semibold"
				>{$_('zones.vertices', { values: { count: points.length } })}</caption
			>
			<thead class="bg-surface-2 text-xs font-semibold tracking-wide text-tertiary uppercase">
				<tr>
					<th scope="col" class="px-2 py-1.5 text-start">#</th>
					<th scope="col" class="px-2 py-1.5 text-start">x (m)</th>
					<th scope="col" class="px-2 py-1.5 text-start">y (m)</th>
					{#if editable}<th scope="col" class="px-2 py-1.5"
							><span class="sr-only">{$_('zones.vertexActions')}</span></th
						>{/if}
				</tr>
			</thead>
			<tbody>
				{#each points as point, index (index)}
					<tr class="odd:bg-surface even:bg-surface-2/50">
						<th scope="row" class="px-2 py-1 text-start font-normal tabular-nums">{index + 1}</th>
						<td class="px-2 py-1">
							<input
								type="number"
								step="0.1"
								min="0"
								max={width}
								value={point.x}
								readonly={!editable}
								aria-label={$_('zones.vertexX', { values: { n: index + 1 } })}
								onchange={(event) => set(index, 'x', event.currentTarget.valueAsNumber)}
								class="h-8 w-24 rounded-md border border-input bg-background px-2 text-sm tabular-nums read-only:bg-muted focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
							/>
						</td>
						<td class="px-2 py-1">
							<input
								type="number"
								step="0.1"
								min="0"
								max={depth}
								value={point.y}
								readonly={!editable}
								aria-label={$_('zones.vertexY', { values: { n: index + 1 } })}
								onchange={(event) => set(index, 'y', event.currentTarget.valueAsNumber)}
								class="h-8 w-24 rounded-md border border-input bg-background px-2 text-sm tabular-nums read-only:bg-muted focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
							/>
						</td>
						{#if editable}
							<td class="px-2 py-1 whitespace-nowrap">
								<button
									type="button"
									onclick={() => insertAfter(index)}
									aria-label={$_('zones.insertAfter', { values: { n: index + 1 } })}
									class="inline-flex size-7 items-center justify-center rounded-md text-muted-foreground hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
								>
									<Plus class="size-4" aria-hidden="true" />
								</button>
								<button
									type="button"
									disabled={points.length <= 3}
									onclick={() => removeAt(index)}
									aria-label={$_('zones.removeVertex', { values: { n: index + 1 } })}
									class="inline-flex size-7 items-center justify-center rounded-md text-muted-foreground hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-40"
								>
									<X class="size-4" aria-hidden="true" />
								</button>
							</td>
						{/if}
					</tr>
				{/each}
			</tbody>
		</table>
	</div>
	{#if editable}
		<div class="flex flex-wrap items-center gap-2">
			<button
				type="button"
				data-testid="save-zone"
				onclick={onSave}
				class="inline-flex h-9 items-center rounded-md bg-button-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none dark:text-white"
			>
				{$_('zones.saveShape')}
			</button>
			{#if dirty}
				<button
					type="button"
					onclick={onRevert}
					class="inline-flex h-9 items-center gap-1 rounded-md border bg-card px-3 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
				>
					<Undo2 class="size-4" aria-hidden="true" />
					{$_('zones.revert')}
				</button>
			{/if}
			<ConfirmButton
				label={$_('topology.actions.delete')}
				confirmLabel={$_('topology.actions.confirmDelete', { values: { code: zone.name } })}
				testId="delete-zone"
				onConfirm={onDelete}
			/>
		</div>
	{/if}
</section>
