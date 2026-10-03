<script lang="ts">
	import { untrack } from 'svelte';
	import { _ } from 'svelte-i18n';
	import FormField from '$lib/components/shared/FormField.svelte';
	import SelectField from '$lib/components/shared/SelectField.svelte';
	import * as devices from '$lib/core/devices';
	import type { Issued, Mapping } from '$lib/core/devices';
	import type { Level } from '$lib/core/topology';
	import PlacementFields from './PlacementFields.svelte';

	interface Props {
		levels: Level[];
		queueZones: Record<string, string[]>;
		mappings: Mapping[];
		onRegistered: (issued: Issued) => void | Promise<void>;
		onCancel: () => void;
		onFailed: (reason: string) => void;
	}

	let { levels, queueZones, mappings, onRegistered, onCancel, onFailed }: Props = $props();

	let code = $state('');
	let family = $state<string>('StereoVision');
	let model = $state('');
	let transport = $state<string>('HttpsPush');
	let dialect = $state<string>('Canonical');
	let clockSource = $state<string>('Ntp');
	let mappingName = $state('');
	// Starts on the first level that has a queue zone to assign the device to.
	let levelId = $state(
		untrack(() => levels.find((l) => (queueZones[l.id] ?? []).length)?.id ?? levels[0]?.id ?? '')
	);
	let x = $state(10);
	let y = $state(10);
	let height = $state(3);
	let orientation = $state(0);
	let queueZoneName = $state('');
	let length = $state<number | string>('');
	let width = $state<number | string>('');
	let radius = $state<number | string>('');
	let busy = $state(false);

	function metres(value: number | string): number | null {
		return value === '' || value === null ? null : Number(value);
	}

	async function submit(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		if (busy) return;
		busy = true;
		const result = await devices.register({
			code: code.trim(),
			family,
			model: model.trim(),
			transport,
			dialect,
			clockSource,
			mappingName: dialect === 'Declarative' ? mappingName || null : null,
			placement: {
				levelId,
				x: Number(x),
				y: Number(y),
				mountingHeightMetres: Number(height),
				orientationDegrees: Number(orientation),
				queueZoneName,
				footprintLengthMetres: metres(length),
				footprintWidthMetres: metres(width),
				footprintRadiusMetres: metres(radius)
			}
		});
		busy = false;
		if (result.hasErrors || !result.data) return onFailed(result.errorMessages[0] ?? '');
		await onRegistered(result.data);
	}
</script>

<form class="flex flex-col gap-3" onsubmit={submit} data-testid="register-form">
	<h2 class="text-base font-semibold">{$_('devices.register')}</h2>
	<div class="grid grid-cols-2 gap-2">
		<FormField
			id="register-code"
			label={$_('devices.fields.code')}
			bind:value={code}
			required
			maxlength={16}
			uppercase
		/>
		<FormField
			id="register-model"
			label={$_('devices.fields.model')}
			bind:value={model}
			required
			maxlength={100}
		/>
		<SelectField
			id="register-family"
			label={$_('devices.fields.family')}
			bind:value={family}
			options={devices.families.map((f) => ({ value: f, label: $_(`devices.families.${f}`) }))}
		/>
		<SelectField
			id="register-transport"
			label={$_('devices.fields.transport')}
			bind:value={transport}
			options={devices.transports.map((t) => ({ value: t, label: $_(`devices.transports.${t}`) }))}
		/>
		<SelectField
			id="register-dialect"
			label={$_('devices.fields.dialect')}
			bind:value={dialect}
			options={devices.dialects.map((d) => ({ value: d, label: $_(`devices.dialects.${d}`) }))}
		/>
		<SelectField
			id="register-clock"
			label={$_('devices.fields.clockSource')}
			bind:value={clockSource}
			options={devices.clockSources.map((c) => ({ value: c, label: c.toUpperCase() }))}
		/>
	</div>
	{#if dialect === 'Declarative'}
		<SelectField
			id="register-mapping"
			label={$_('devices.fields.mapping')}
			bind:value={mappingName}
			options={mappings.map((m) => ({ value: m.name, label: m.title }))}
		/>
	{/if}
	<PlacementFields
		prefix="register"
		{levels}
		{queueZones}
		bind:levelId
		bind:x
		bind:y
		bind:height
		bind:orientation
		bind:queueZoneName
		bind:length
		bind:width
		bind:radius
	/>
	<div class="flex gap-2">
		<button
			type="submit"
			disabled={busy}
			class="inline-flex h-9 items-center rounded-md bg-button-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60 dark:text-white"
		>
			{$_('devices.register')}
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
