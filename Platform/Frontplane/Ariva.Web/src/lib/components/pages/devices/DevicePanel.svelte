<script lang="ts">
	import { X } from '@lucide/svelte';
	import { onMount, untrack } from 'svelte';
	import { _ } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import ConfirmButton from '$lib/components/shared/ConfirmButton.svelte';
	import FormField from '$lib/components/shared/FormField.svelte';
	import SelectField from '$lib/components/shared/SelectField.svelte';
	import { auth } from '$lib/core/auth.svelte';
	import * as devices from '$lib/core/devices';
	import type { Calibration, Device, DeviceHealth, Issued, Mapping } from '$lib/core/devices';
	import type { Level } from '$lib/core/topology';
	import PlacementFields from './PlacementFields.svelte';

	interface Props {
		device: Device;
		health: DeviceHealth | null;
		levels: Level[];
		queueZones: Record<string, string[]>;
		mappings: Mapping[];
		onChanged: (device: Device | null) => void | Promise<void>;
		onIssued: (issued: Issued) => void;
		onClose: () => void;
		onFailed: (reason: string) => void;
	}

	let {
		device,
		health,
		levels,
		queueZones,
		mappings,
		onChanged,
		onIssued,
		onClose,
		onFailed
	}: Props = $props();

	// The panel is keyed by the device: the forms start from it and are edited locally until saved.
	const start = untrack(() => device);
	let model = $state(start.model);
	let transport = $state(start.transport);
	let dialect = $state(start.dialect);
	let clockSource = $state(start.clockSource);
	let mappingName = $state(start.mappingName ?? '');
	let levelId = $state(start.levelId);
	let x = $state(start.x);
	let y = $state(start.y);
	let height = $state(start.mountingHeightMetres);
	let orientation = $state(start.orientationDegrees);
	let queueZoneName = $state(start.queueZoneName);
	let length = $state<number | string>(
		start.footprint.source === 'Vendor' ? (start.footprint.lengthMetres ?? '') : ''
	);
	let width = $state<number | string>(
		start.footprint.source === 'Vendor' ? (start.footprint.widthMetres ?? '') : ''
	);
	let radius = $state<number | string>(
		start.footprint.source === 'Vendor' ? (start.footprint.radiusMetres ?? '') : ''
	);
	let sources = $state((start.allowedSources ?? []).join('\n'));
	let certificate = $state(start.clientCertificateSha256 ?? '');
	let calibrations = $state<Calibration[]>([]);
	let method = $state<string>('ManualCountTally');
	let sampleSize = $state(100);
	let accuracy = $state(97);
	let waitError = $state(1);
	let notes = $state('');
	let busy = $state(false);

	const retired = $derived(device.state === 'Retired');
	const canEdit = $derived(auth.can('Device.Edit') && !retired);
	const canCreate = $derived(auth.can('Device.Create') && !retired);
	const canDelete = $derived(auth.can('Device.Delete') && !retired);

	onMount(async () => {
		const result = await devices.calibrations(device.id);
		calibrations = result.data ?? [];
	});

	function metres(value: number | string): number | null {
		return value === '' || value === null ? null : Number(value);
	}

	function date(value: string | null): string {
		return value ? new Date(value).toLocaleString() : $_('devices.never');
	}

	async function run<T>(
		call: () => Promise<{ hasErrors: boolean; errorMessages: string[]; data: T | null }>,
		done: (data: T) => void | Promise<void>
	): Promise<void> {
		if (busy) return;
		busy = true;
		try {
			const result = await call();
			if (result.hasErrors || result.data === null) return onFailed(result.errorMessages[0] ?? '');
			await done(result.data);
		} finally {
			busy = false;
		}
	}

	const save = (event: SubmitEvent) => {
		event.preventDefault();
		return run(
			() =>
				devices.update(device.id, {
					model: model.trim(),
					transport,
					dialect,
					clockSource,
					mappingName: dialect === 'Declarative' ? mappingName || null : null
				}),
			async (d) => {
				toast.success($_('devices.messages.saved', { values: { code: d.code } }));
				await onChanged(d);
			}
		);
	};

	const move = (event: SubmitEvent) => {
		event.preventDefault();
		return run(
			() =>
				devices.move(device.id, {
					levelId,
					x: Number(x),
					y: Number(y),
					mountingHeightMetres: Number(height),
					orientationDegrees: Number(orientation),
					queueZoneName,
					footprintLengthMetres: metres(length),
					footprintWidthMetres: metres(width),
					footprintRadiusMetres: metres(radius)
				}),
			async (d) => {
				toast.success($_('devices.messages.moved', { values: { code: d.code } }));
				await onChanged(d);
			}
		);
	};

	const saveAccess = (event: SubmitEvent) => {
		event.preventDefault();
		const list = sources
			.split(/\r?\n/)
			.map((s) => s.trim())
			.filter(Boolean);
		return run(
			() => devices.setAccess(device.id, list, certificate.trim()),
			async (d) => {
				toast.success($_('devices.messages.accessSaved'));
				await onChanged(d);
			}
		);
	};

	const record = (event: SubmitEvent) => {
		event.preventDefault();
		return run(
			() =>
				devices.recordCalibration(device.id, {
					method,
					sampleSize: Number(sampleSize),
					countingAccuracyPercent: Number(accuracy),
					waitTimeErrorMinutes: Number(waitError),
					notes: notes.trim() || null
				}),
			async (c) => {
				calibrations = [c, ...calibrations];
				toast.success(
					$_('devices.messages.recorded', {
						values: { result: $_(c.passed ? 'devices.result.pass' : 'devices.result.fail') }
					})
				);
				await onChanged(null);
			}
		);
	};

	const rotate = () =>
		run(
			() => devices.rotateCredential(device.id),
			async (issued) => {
				toast.success($_('devices.messages.rotated', { values: { code: issued.device.code } }));
				onIssued(issued);
				await onChanged(issued.device);
			}
		);

	const retire = () =>
		run(
			() => devices.retire(device.id),
			async (d) => {
				toast.success($_('devices.messages.retired', { values: { code: d.code } }));
				await onChanged(d);
			}
		);

	async function removeDevice(): Promise<void> {
		if (busy) return;
		busy = true;
		try {
			const result = await devices.remove(device.id);
			if (result.hasErrors) return onFailed(result.errorMessages[0] ?? '');
			toast.success($_('devices.messages.removed', { values: { code: device.code } }));
			await onChanged(null);
			onClose();
		} finally {
			busy = false;
		}
	}

	const buttonClass =
		'inline-flex h-9 items-center self-start rounded-md bg-button-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60 dark:text-white';
</script>

<section
	data-testid="device-panel"
	aria-labelledby="device-title"
	class="flex flex-col gap-5 rounded-xl border bg-card p-5"
>
	<header class="flex items-start justify-between gap-3">
		<div class="min-w-0">
			<p class="text-xs font-semibold tracking-wide text-tertiary uppercase">
				{$_(`devices.families.${device.family}`, { default: device.family })}
			</p>
			<h2 id="device-title" class="truncate text-xl font-semibold">
				<span class="font-mono">{device.code}</span>
				{device.model}
			</h2>
			<p class="text-xs text-muted-foreground">
				<span data-testid="device-state">{$_(`devices.states.${device.state}`)}</span>
				· {device.queueZoneName}
				· {health?.secondsSinceSeen != null
					? $_('devices.secondsAgo', { values: { seconds: Math.round(health.secondsSinceSeen) } })
					: $_('devices.never')}
			</p>
			<p class="text-xs text-muted-foreground" data-testid="credential-prefix">
				{device.credentialPrefix
					? $_('devices.credential.prefix', {
							values: { prefix: device.credentialPrefix, date: date(device.credentialIssuedOn) }
						})
					: $_('devices.credential.none')}
			</p>
		</div>
		<button
			type="button"
			onclick={onClose}
			aria-label={$_('devices.actions.close')}
			class="inline-flex size-8 items-center justify-center rounded-md text-muted-foreground hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			<X class="size-4" aria-hidden="true" />
		</button>
	</header>

	<div class="grid gap-6 lg:grid-cols-2">
		<form class="flex flex-col gap-2.5" onsubmit={save} data-testid="device-details">
			<h3 class="text-sm font-semibold">{$_('devices.sections.details')}</h3>
			<fieldset disabled={!canEdit} class="grid grid-cols-2 gap-2">
				<FormField
					id="device-model"
					label={$_('devices.fields.model')}
					bind:value={model}
					required
					maxlength={100}
					readonly={!canEdit}
				/>
				<SelectField
					id="device-transport"
					label={$_('devices.fields.transport')}
					bind:value={transport}
					options={devices.transports.map((t) => ({
						value: t,
						label: $_(`devices.transports.${t}`)
					}))}
				/>
				<SelectField
					id="device-dialect"
					label={$_('devices.fields.dialect')}
					bind:value={dialect}
					options={devices.dialects.map((d) => ({ value: d, label: $_(`devices.dialects.${d}`) }))}
				/>
				<SelectField
					id="device-clock"
					label={$_('devices.fields.clockSource')}
					bind:value={clockSource}
					options={devices.clockSources.map((c) => ({ value: c, label: c.toUpperCase() }))}
				/>
				{#if dialect === 'Declarative'}
					<SelectField
						id="device-mapping"
						label={$_('devices.fields.mapping')}
						bind:value={mappingName}
						options={mappings.map((m) => ({ value: m.name, label: m.title }))}
					/>
				{/if}
			</fieldset>
			{#if canEdit}<button type="submit" disabled={busy} class={buttonClass}
					>{$_('devices.actions.save')}</button
				>{/if}
		</form>

		<form class="flex flex-col gap-2.5" onsubmit={move} data-testid="device-placement">
			<h3 class="text-sm font-semibold">{$_('devices.sections.placement')}</h3>
			<p class="text-xs text-muted-foreground">
				{device.footprint.text}{#if device.footprint.source === 'AssumedFromBoq'}
					({$_('devices.assumed')}){/if}
			</p>
			<fieldset disabled={!canEdit}>
				<PlacementFields
					prefix="device"
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
			</fieldset>
			{#if canEdit}
				<p class="text-xs text-muted-foreground">{$_('devices.moveHint')}</p>
				<button type="submit" disabled={busy} class={buttonClass}
					>{$_('devices.actions.move')}</button
				>
			{/if}
		</form>

		<section
			class="flex flex-col gap-2.5"
			aria-labelledby="calibration-title"
			data-testid="device-calibration"
		>
			<h3 id="calibration-title" class="text-sm font-semibold">
				{$_('devices.sections.calibration')}
			</h3>
			{#if calibrations.length === 0}
				<p class="text-sm text-muted-foreground">{$_('devices.noCalibrations')}</p>
			{:else}
				<ul class="flex flex-col gap-1 text-sm" data-testid="calibrations">
					{#each calibrations as c (c.id)}
						<li>
							{$_('devices.calibrationRow', {
								values: {
									date: date(c.performedOn),
									accuracy: c.countingAccuracyPercent,
									sample: c.sampleSize,
									result: $_(c.passed ? 'devices.result.pass' : 'devices.result.fail')
								}
							})}
						</li>
					{/each}
				</ul>
			{/if}
			{#if canEdit}
				<form class="flex flex-col gap-2.5" onsubmit={record} data-testid="record-calibration">
					<div class="grid grid-cols-2 gap-2">
						<SelectField
							id="calibration-method"
							label={$_('devices.fields.method')}
							bind:value={method}
							options={devices.calibrationMethods.map((m) => ({ value: m, label: m }))}
						/>
						<FormField
							id="calibration-sample"
							label={$_('devices.fields.sampleSize')}
							type="number"
							bind:value={sampleSize}
							min={50}
							max={5000}
							required
						/>
						<FormField
							id="calibration-accuracy"
							label={$_('devices.fields.accuracy')}
							type="number"
							bind:value={accuracy}
							min={0}
							max={100}
							step="any"
							required
						/>
						<FormField
							id="calibration-wait"
							label={$_('devices.fields.waitError')}
							type="number"
							bind:value={waitError}
							min={0}
							max={30}
							step="any"
							required
						/>
					</div>
					<FormField
						id="calibration-notes"
						label={$_('devices.fields.notes')}
						bind:value={notes}
						maxlength={1000}
					/>
					<p class="text-xs text-muted-foreground">{$_('devices.calibrationHint')}</p>
					<button type="submit" disabled={busy} class={buttonClass}
						>{$_('devices.actions.record')}</button
					>
				</form>
			{/if}
		</section>

		<div class="flex flex-col gap-5">
			{#if canEdit}
				<form class="flex flex-col gap-2.5" onsubmit={saveAccess} data-testid="device-access">
					<h3 class="text-sm font-semibold">{$_('devices.sections.access')}</h3>
					<div class="flex flex-col gap-1">
						<label for="device-sources" class="text-xs font-medium"
							>{$_('devices.fields.allowedSources')}</label
						>
						<textarea
							id="device-sources"
							bind:value={sources}
							rows="3"
							spellcheck="false"
							class="rounded-md border border-input bg-background px-2.5 py-1.5 font-mono text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
						></textarea>
					</div>
					<FormField
						id="device-certificate"
						label={$_('devices.fields.certificate')}
						bind:value={certificate}
						maxlength={95}
					/>
					<button type="submit" disabled={busy} class={buttonClass}
						>{$_('devices.actions.setAccess')}</button
					>
				</form>
			{/if}
			{#if canCreate || canDelete}
				<div class="flex flex-col gap-2.5">
					<h3 class="text-sm font-semibold">{$_('devices.sections.danger')}</h3>
					<div class="flex flex-wrap gap-2">
						{#if canCreate}
							<ConfirmButton
								label={$_('devices.credential.rotate')}
								confirmLabel={$_('devices.credential.confirmRotate')}
								testId="rotate"
								onConfirm={rotate}
							/>
						{/if}
						{#if canDelete}
							<ConfirmButton
								label={$_('devices.actions.retire')}
								confirmLabel={$_('devices.actions.confirmRetire', {
									values: { code: device.code }
								})}
								testId="retire"
								onConfirm={retire}
							/>
							{#if !device.lastCalibratedOn}
								<ConfirmButton
									label={$_('devices.actions.remove')}
									confirmLabel={$_('devices.actions.confirmRemove', {
										values: { code: device.code }
									})}
									testId="remove"
									onConfirm={removeDevice}
								/>
							{/if}
						{/if}
					</div>
				</div>
			{/if}
		</div>
	</div>
</section>
