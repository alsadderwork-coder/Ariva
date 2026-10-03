<script lang="ts">
	import { _ } from 'svelte-i18n';
	import FormField from '$lib/components/shared/FormField.svelte';
	import SelectField from '$lib/components/shared/SelectField.svelte';
	import type { Level } from '$lib/core/topology';

	interface Props {
		prefix: string;
		levels: Level[];
		/** The queue zones of each level (published version, else the draft). */
		queueZones: Record<string, string[]>;
		levelId: string;
		x: number;
		y: number;
		height: number;
		orientation: number;
		queueZoneName: string;
		length: number | string;
		width: number | string;
		radius: number | string;
	}

	let {
		prefix,
		levels,
		queueZones,
		levelId = $bindable(),
		x = $bindable(),
		y = $bindable(),
		height = $bindable(),
		orientation = $bindable(),
		queueZoneName = $bindable(),
		length = $bindable(),
		width = $bindable(),
		radius = $bindable()
	}: Props = $props();

	const zoneOptions = $derived(
		(queueZones[levelId] ?? []).map((name) => ({ value: name, label: name }))
	);
	$effect(() => {
		const names = queueZones[levelId] ?? [];
		if (names.length && !names.includes(queueZoneName)) queueZoneName = names[0];
	});
</script>

<div class="flex flex-col gap-2.5">
	<div class="grid grid-cols-2 gap-2">
		<SelectField
			id="{prefix}-level"
			label={$_('devices.fields.level')}
			bind:value={levelId}
			options={levels.map((l) => ({ value: l.id, label: `${l.code}, ${l.name}` }))}
		/>
		<SelectField
			id="{prefix}-zone"
			label={$_('devices.fields.queueZone')}
			bind:value={queueZoneName}
			options={zoneOptions}
		/>
	</div>
	<div class="grid grid-cols-2 gap-2 sm:grid-cols-4">
		<FormField
			id="{prefix}-x"
			label={$_('devices.fields.x')}
			type="number"
			bind:value={x}
			min={0}
			max={2000}
			step="any"
			required
		/>
		<FormField
			id="{prefix}-y"
			label={$_('devices.fields.y')}
			type="number"
			bind:value={y}
			min={0}
			max={2000}
			step="any"
			required
		/>
		<FormField
			id="{prefix}-height"
			label={$_('devices.fields.height')}
			type="number"
			bind:value={height}
			min={0}
			max={100}
			step="any"
			required
		/>
		<FormField
			id="{prefix}-orientation"
			label={$_('devices.fields.orientation')}
			type="number"
			bind:value={orientation}
			min={0}
			max={360}
			step="any"
			required
		/>
	</div>
	<div class="grid grid-cols-3 gap-2">
		<FormField
			id="{prefix}-length"
			label={$_('devices.fields.footprintLength')}
			type="number"
			bind:value={length}
			min={0}
			max={1000}
			step="any"
		/>
		<FormField
			id="{prefix}-width"
			label={$_('devices.fields.footprintWidth')}
			type="number"
			bind:value={width}
			min={0}
			max={1000}
			step="any"
		/>
		<FormField
			id="{prefix}-radius"
			label={$_('devices.fields.footprintRadius')}
			type="number"
			bind:value={radius}
			min={0}
			max={1000}
			step="any"
		/>
	</div>
	<p class="text-xs text-muted-foreground">{$_('devices.footprintHint')}</p>
</div>
