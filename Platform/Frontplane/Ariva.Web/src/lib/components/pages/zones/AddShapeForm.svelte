<script lang="ts">
	import { _ } from 'svelte-i18n';
	import FormField from '$lib/components/shared/FormField.svelte';
	import SelectField from '$lib/components/shared/SelectField.svelte';
	import type { Desk } from '$lib/core/topology';
	import * as zones from '$lib/core/zones';
	import {
		lineRoles,
		round,
		zoneKinds,
		type LineRole,
		type Zone,
		type ZoneKind
	} from '$lib/core/zones';

	interface Props {
		mode: 'zone' | 'line';
		profileId: string;
		levelId: string;
		width: number;
		depth: number;
		/** The queue and overflow zones on this level, which other zones and lines belong to. */
		owners: Zone[];
		desks: Desk[];
		onAdded: (selected: { kind: 'zone' | 'line'; id: string }) => void | Promise<void>;
		onCancel: () => void;
		onFailed: (reason: string) => void;
	}

	let {
		mode,
		profileId,
		levelId,
		width,
		depth,
		owners,
		desks,
		onAdded,
		onCancel,
		onFailed
	}: Props = $props();

	let name = $state('');
	let kind = $state<string>('Queue');
	let role = $state<string>('Entry');
	let owner = $state('');
	let desk = $state('');
	let busy = $state(false);

	const queues = $derived(owners.filter((z) => z.kind === 'Queue'));
	const needsOwner = $derived(mode === 'zone' ? kind !== 'Queue' : role !== 'Count');
	const ownerOptions = $derived([
		{ value: '', label: $_('zones.none') },
		...(mode === 'line' && role === 'OverflowEntry'
			? owners.filter((z) => z.kind === 'Overflow')
			: queues
		).map((z) => ({ value: z.id, label: z.name }))
	]);
	const deskOptions = $derived([
		{ value: '', label: $_('zones.none') },
		...desks.map((d) => ({ value: d.id, label: d.code }))
	]);

	/** A small square or a short line in the middle of the level, to be shaped on the plan or in the table. */
	function starter(): zones.Point[] {
		const cx = width / 2;
		const cy = depth / 2;
		const half = Math.max(1, Math.min(width, depth) / 20);
		return [
			{ x: round(cx - half), y: round(cy - half) },
			{ x: round(cx + half), y: round(cy - half) },
			{ x: round(cx + half), y: round(cy + half) },
			{ x: round(cx - half), y: round(cy + half) }
		];
	}

	/**
	 * A line of a zone lies on one of its edges: the middle half of the first edge for an entry, of the edge across
	 * the zone for an exit; a count line without a zone starts in the middle of the level.
	 */
	function starterLine(): [zones.Point, zones.Point] {
		const zone = owners.find((z) => z.id === owner);
		const ring = zone ? zones.parsePolygon(zone.polygon) : null;
		if (ring) {
			const index = role === 'Exit' ? Math.floor(ring.length / 2) % ring.length : 0;
			const a = ring[index];
			const b = ring[(index + 1) % ring.length];
			const at = (t: number): zones.Point => ({
				x: round(a.x + (b.x - a.x) * t),
				y: round(a.y + (b.y - a.y) * t)
			});
			return [at(0.25), at(0.75)];
		}
		const [a, b] = starter();
		return [
			{ x: a.x, y: round((a.y + b.y) / 2) },
			{ x: b.x, y: round((a.y + b.y) / 2) }
		];
	}

	async function submit(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		if (busy) return;
		busy = true;
		try {
			if (mode === 'zone') {
				const result = await zones.addZone(profileId, {
					name: name.trim(),
					kind: kind as ZoneKind,
					levelId,
					polygon: zones.formatPolygon(starter()),
					queueZoneId: owner || null,
					deskId: desk || null
				});
				if (result.hasErrors || !result.data) return onFailed(result.errorMessages[0] ?? '');
				await onAdded({ kind: 'zone', id: result.data.id });
			} else {
				const [a, b] = starterLine();
				const result = await zones.addLine(profileId, {
					name: name.trim(),
					role: role as LineRole,
					levelId,
					startX: a.x,
					startY: a.y,
					endX: b.x,
					endY: b.y,
					zoneId: owner || null
				});
				if (result.hasErrors || !result.data) return onFailed(result.errorMessages[0] ?? '');
				await onAdded({ kind: 'line', id: result.data.id });
			}
		} finally {
			busy = false;
		}
	}
</script>

<form class="flex flex-col gap-2.5" onsubmit={submit} data-testid="add-{mode}-form">
	<FormField
		id="add-{mode}-name"
		label={$_('topology.fields.name')}
		bind:value={name}
		required
		maxlength={200}
	/>
	{#if mode === 'zone'}
		<SelectField
			id="add-zone-kind"
			label={$_('topology.fields.kind')}
			bind:value={kind}
			options={zoneKinds.map((k) => ({ value: k, label: $_(`zones.kinds.${k}`) }))}
		/>
	{:else}
		<SelectField
			id="add-line-role"
			label={$_('zones.role')}
			bind:value={role}
			options={lineRoles.map((r) => ({ value: r, label: $_(`zones.roles.${r}`) }))}
		/>
	{/if}
	{#if needsOwner}
		<SelectField
			id="add-{mode}-owner"
			label={$_('zones.owner')}
			bind:value={owner}
			options={ownerOptions}
		/>
	{/if}
	{#if mode === 'zone' && (kind === 'Service' || kind === 'Staff')}
		<SelectField
			id="add-zone-desk"
			label={$_('zones.desk')}
			bind:value={desk}
			options={deskOptions}
		/>
	{/if}
	<p class="text-xs text-muted-foreground">
		{mode === 'zone' ? $_('zones.starterZone') : $_('zones.starterLine')}
	</p>
	<div class="flex gap-2">
		<button
			type="submit"
			disabled={busy || !name.trim()}
			class="inline-flex h-9 items-center rounded-md bg-button-primary px-3 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60 dark:text-white"
		>
			{$_('topology.actions.create')}
		</button>
		<button
			type="button"
			onclick={onCancel}
			class="inline-flex h-9 items-center rounded-md border bg-card px-3 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			{$_('topology.actions.cancel')}
		</button>
	</div>
</form>
