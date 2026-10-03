<script lang="ts">
	import { untrack } from 'svelte';
	import { _ } from 'svelte-i18n';
	import FormField from '$lib/components/shared/FormField.svelte';
	import SelectField from '$lib/components/shared/SelectField.svelte';
	import * as rules from '$lib/core/alertRules';
	import type { AlertRule, AlertRuleRequest } from '$lib/core/alertRules';
	import BacktestPreview from './BacktestPreview.svelte';

	interface Props {
		/** The rule being changed, or null for a new one. */
		editing: AlertRule | null;
		/** Where the form starts: a saved rule's values (edit, duplicate) or the defaults. */
		initial: AlertRuleRequest;
		/** Queue and overflow zones of the site's published zone profile. */
		zoneNames: string[];
		/** Roles the caller may give a rule to (their own; every operational role for an administrator). */
		roles: string[];
		/** An administrator may leave a rule without an owner (every role of the site is responsible). */
		allowNoOwner: boolean;
		canPreview: boolean;
		onSaved: (rule: AlertRule) => void | Promise<void>;
		onCancel: () => void;
	}

	let { editing, initial, zoneNames, roles, allowNoOwner, canPreview, onSaved, onCancel }: Props =
		$props();

	const start = untrack(() => initial);
	let name = $state(start.name);
	let zones = $state<string[]>([...start.zones]);
	let metric = $state(start.metric);
	let comparator = $state(start.comparator === 'IsTrue' ? 'GreaterThan' : start.comparator);
	let threshold = $state<number | string>(start.threshold ?? '');
	let clearThreshold = $state<number | string>(start.clearThreshold ?? '');
	let minQueueLength = $state<number | string>(start.minQueueLength ?? '');
	let leadMinutes = $state<number | string>(start.leadMinutes ?? 30);
	let sustainMinutes = $state<number | string>(start.sustainMinutes);
	let clearAfterMinutes = $state<number | string>(start.clearAfterMinutes);
	let severity = $state(start.severity);
	let ownerRole = $state(start.ownerRole ?? '');
	let escalateAfterMinutes = $state<number | string>(start.escalateAfterMinutes ?? '');
	let escalateToRole = $state(start.escalateToRole ?? '');
	let escalationContact = $state(start.escalationContact ?? '');
	let notifyByEmail = $state(start.notifyByEmail);
	let enabled = $state(start.enabled);
	let problems = $state<string[]>([]);
	let busy = $state(false);

	const unit = $derived(rules.unitOf(metric));
	// A saved rule keeps its roles in the choices even when the caller does not hold them (the server decides changes).
	const roleChoices = $derived([
		...new Set([...roles, start.ownerRole, start.escalateToRole].filter((r): r is string => !!r))
	]);
	const label = (key: string): string => $_(`alertRules.${key}`);
	// A value the screen has no words for is shown as it is, never used as a message key.
	const known: Record<string, readonly string[]> = {
		metrics: rules.metrics,
		comparators: rules.comparators,
		severities: rules.severities,
		roles: rules.roleCodes
	};
	const option = (group: string) => (value: string) => ({
		value,
		label: known[group]?.includes(value) ? $_(`alertRules.${group}.${value}`) : value
	});

	function number(value: number | string | null | undefined): number | null {
		if (value === '' || value === null || value === undefined) return null;
		const n = Number(value);
		return Number.isFinite(n) ? n : null;
	}

	/** The rule as the form holds it now; the preview judges exactly this. */
	function current(): AlertRuleRequest {
		return {
			siteCode: start.siteCode,
			name,
			zones: [...zones],
			metric,
			comparator,
			threshold: number(threshold),
			minQueueLength: number(minQueueLength),
			clearThreshold: number(clearThreshold),
			sustainMinutes: number(sustainMinutes) ?? 0,
			clearAfterMinutes: number(clearAfterMinutes) ?? 0,
			severity,
			ownerRole: ownerRole || null,
			escalateAfterMinutes: number(escalateAfterMinutes),
			escalateToRole: escalateToRole || null,
			escalationContact: escalationContact || null,
			notifyByEmail,
			enabled,
			leadMinutes: number(leadMinutes)
		};
	}

	async function submit(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		if (busy) return;
		problems = zones.length ? [] : [label('form.zonesRequired')];
		if (problems.length) return;
		busy = true;
		const result = editing
			? await rules.update(editing.id, current())
			: await rules.create(current());
		busy = false;
		if (result.hasErrors || !result.data) {
			problems = result.errorMessages;
			return;
		}
		await onSaved(result.data);
	}

	function toggleZone(zone: string, on: boolean): void {
		zones = on ? [...zones, zone] : zones.filter((z) => z !== zone);
	}
</script>

<!-- Typed fields only: no expression of any kind (CWE-94). Zone names and server messages are text. -->
<form
	class="flex flex-col gap-4 rounded-xl border bg-card p-4"
	data-testid="rule-form"
	aria-labelledby="rule-form-title"
	onsubmit={submit}
>
	<h2 id="rule-form-title" class="text-base font-semibold">
		{editing
			? $_('alertRules.form.editTitle', { values: { code: editing.code } })
			: label('form.createTitle')}
	</h2>

	<FormField
		id="rule-name"
		label={label('fields.name')}
		bind:value={name}
		required
		maxlength={rules.limits.nameLength}
	/>

	<fieldset class="flex flex-col gap-1.5">
		<legend class="text-xs font-medium">{label('fields.zones')}</legend>
		{#if zoneNames.length === 0}
			<p class="text-xs text-muted-foreground">{label('form.noZones')}</p>
		{:else}
			<div class="flex flex-wrap gap-x-4 gap-y-1.5">
				{#each zoneNames as zone (zone)}
					<label class="inline-flex items-center gap-1.5 text-sm">
						<input
							type="checkbox"
							checked={zones.includes(zone)}
							onchange={(event) => toggleZone(zone, event.currentTarget.checked)}
						/>
						<span>{zone}</span>
					</label>
				{/each}
			</div>
		{/if}
	</fieldset>

	<div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
		<SelectField
			id="rule-metric"
			label={label('fields.metric')}
			bind:value={metric}
			options={rules.metrics.map(option('metrics'))}
		/>
		{#if unit !== 'condition'}
			<SelectField
				id="rule-comparator"
				label={label('fields.comparator')}
				bind:value={comparator}
				options={rules.comparators.map(option('comparators'))}
			/>
			<FormField
				id="rule-threshold"
				type="number"
				label={$_('alertRules.fields.threshold', { values: { unit: label(`units.${unit}`) } })}
				bind:value={threshold}
				required
				min={0}
				max={rules.maxThreshold(metric)}
				step="any"
			/>
			<FormField
				id="rule-clear-threshold"
				type="number"
				label={$_('alertRules.fields.clearThreshold', { values: { unit: label(`units.${unit}`) } })}
				hint={label('hints.clearThreshold')}
				bind:value={clearThreshold}
				min={0}
				max={rules.maxThreshold(metric)}
				step="any"
			/>
		{:else}
			<p class="self-end text-xs text-muted-foreground sm:col-span-2">{label('hints.condition')}</p>
		{/if}
		{#if metric === 'Nowcast'}
			<FormField
				id="rule-min-queue"
				type="number"
				label={label('fields.minQueueLength')}
				hint={label('hints.minQueueLength')}
				bind:value={minQueueLength}
				min={0}
				max={100000}
			/>
		{/if}
		{#if metric === 'PredictedNowcast'}
			<FormField
				id="rule-lead"
				type="number"
				label={label('fields.leadMinutes')}
				hint={label('hints.leadMinutes')}
				bind:value={leadMinutes}
				required
				min={rules.limits.leadMin}
				max={rules.limits.leadMax}
			/>
		{/if}
		<FormField
			id="rule-sustain"
			type="number"
			label={label('fields.sustainMinutes')}
			bind:value={sustainMinutes}
			required
			min={1}
			max={rules.limits.minutes}
		/>
		<FormField
			id="rule-clear-after"
			type="number"
			label={label('fields.clearAfterMinutes')}
			bind:value={clearAfterMinutes}
			required
			min={1}
			max={rules.limits.minutes}
		/>
		<SelectField
			id="rule-severity"
			label={label('fields.severity')}
			bind:value={severity}
			options={rules.severities.map(option('severities'))}
		/>
		<SelectField
			id="rule-owner"
			label={label('fields.ownerRole')}
			bind:value={ownerRole}
			options={[
				...(allowNoOwner ? [{ value: '', label: label('form.noOwner') }] : []),
				...roleChoices.map(option('roles'))
			]}
		/>
		<FormField
			id="rule-escalate-after"
			type="number"
			label={label('fields.escalateAfterMinutes')}
			hint={label('hints.escalation')}
			bind:value={escalateAfterMinutes}
			min={1}
			max={rules.limits.escalationMinutes}
		/>
		<SelectField
			id="rule-escalate-to"
			label={label('fields.escalateToRole')}
			bind:value={escalateToRole}
			options={[{ value: '', label: label('form.noRole') }, ...roleChoices.map(option('roles'))]}
		/>
		<FormField
			id="rule-contact"
			label={label('fields.escalationContact')}
			bind:value={escalationContact}
			maxlength={rules.limits.contactLength}
		/>
	</div>

	<div class="flex flex-wrap gap-x-6 gap-y-2 text-sm">
		<label class="inline-flex items-center gap-2">
			<input type="checkbox" bind:checked={notifyByEmail} />
			{label('fields.notifyByEmail')}
		</label>
		<label class="inline-flex items-center gap-2">
			<input type="checkbox" bind:checked={enabled} />
			{label('fields.enabled')}
		</label>
	</div>

	{#if canPreview}
		<BacktestPreview request={current} />
	{/if}

	{#if problems.length}
		<ul class="list-disc ps-5 text-sm text-status-danger-foreground" data-testid="rule-problems">
			{#each problems as problem, index (index)}<li>{problem}</li>{/each}
		</ul>
	{/if}

	<div class="flex flex-wrap gap-2">
		<button
			type="submit"
			data-testid="save-rule"
			disabled={busy}
			class="inline-flex h-9 items-center rounded-md bg-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
		>
			{editing ? label('actions.save') : label('actions.create')}
		</button>
		<button
			type="button"
			onclick={onCancel}
			class="inline-flex h-9 items-center rounded-md border bg-card px-3 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			{label('actions.cancel')}
		</button>
	</div>
</form>
