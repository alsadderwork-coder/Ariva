<script lang="ts">
	import { Plus, Trash2 } from '@lucide/svelte';
	import { untrack } from 'svelte';
	import { _ } from 'svelte-i18n';
	import FormField from '$lib/components/shared/FormField.svelte';
	import SelectField from '$lib/components/shared/SelectField.svelte';
	import * as displays from '$lib/core/displays';
	import type { Display, DisplayEntry, DisplayRequest } from '$lib/core/displays';

	interface Props {
		siteCode: string;
		/** The display being changed, or null for a new one. */
		editing: Display | null;
		/** Queue zones of the site's published zone profile. */
		zoneNames: string[];
		onSubmit: (request: DisplayRequest) => Promise<string[]>;
		onCancel: () => void;
	}

	let { siteCode, editing, zoneNames, onSubmit, onCancel }: Props = $props();

	const start = untrack(() => editing);
	let code = $state(start?.code ?? '');
	let name = $state(start?.name ?? '');
	let location = $state(start?.location ?? '');
	let orientation = $state(start?.orientation ?? 'Landscape');
	/** The board's languages in the order they were chosen (the first is shown first). */
	let languages = $state<string[]>(start ? [...start.languages] : ['ar', 'en']);
	let bandMinutes = $state<number | string>(start?.bandMinutes ?? 5);
	let hysteresisMinutes = $state<number | string>(start?.hysteresisMinutes ?? 1);
	let staleSeconds = $state<number | string>(start?.staleSeconds ?? 150);
	let entries = $state<DisplayEntry[]>(
		start
			? start.entries.map((e) => ({ zone: e.zone, labels: { ...e.labels } }))
			: [{ zone: untrack(() => zoneNames[0] ?? ''), labels: {} }]
	);
	let fallback = $state<Record<string, string>>(start ? { ...start.fallback } : {});
	let enabled = $state(start?.enabled ?? true);
	let problems = $state<string[]>([]);
	let busy = $state(false);

	function toggleLanguage(language: string, on: boolean): void {
		languages = on ? [...languages, language] : languages.filter((l) => l !== language);
	}

	function addEntry(): void {
		const used = new Set(entries.map((e) => e.zone));
		entries = [...entries, { zone: zoneNames.find((z) => !used.has(z)) ?? '', labels: {} }];
	}

	async function submit(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		if (busy) return;
		busy = true;
		// Only the chosen languages' words go to the server, in the chosen order.
		const pick = (text: Record<string, string>) =>
			Object.fromEntries(languages.map((l) => [l, (text[l] ?? '').trim()]));
		problems = await onSubmit({
			siteCode,
			code: code.trim(),
			name: name.trim(),
			location: location.trim() || null,
			orientation,
			languages: [...languages],
			bandMinutes: Number(bandMinutes),
			hysteresisMinutes: Number(hysteresisMinutes),
			staleSeconds: Number(staleSeconds),
			entries: entries.map((e) => ({ zone: e.zone, labels: pick(e.labels) })),
			fallback: pick(fallback),
			enabled
		});
		busy = false;
	}
</script>

<!-- Every value is text or a number; labels and messages are shown on the board as text, never as markup. -->
<form
	class="flex flex-col gap-4 rounded-xl border bg-card p-4"
	data-testid="display-form"
	aria-labelledby="display-form-title"
	onsubmit={submit}
>
	<h2 id="display-form-title" class="text-base font-semibold">
		{editing
			? $_('displays.form.editTitle', { values: { code: editing.code } })
			: $_('displays.form.createTitle')}
	</h2>
	<div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
		<FormField
			id="display-code"
			label={$_('displays.fields.code')}
			bind:value={code}
			required
			maxlength={16}
			pattern="[A-Z0-9]+(-[A-Z0-9]+)*"
			uppercase
			readonly={!!editing}
		/>
		<FormField
			id="display-name"
			label={$_('displays.fields.name')}
			bind:value={name}
			required
			maxlength={displays.limits.name}
		/>
		<FormField
			id="display-location"
			label={$_('displays.fields.location')}
			bind:value={location}
			maxlength={displays.limits.name}
		/>
		<SelectField
			id="display-orientation"
			label={$_('displays.fields.orientation')}
			bind:value={orientation}
			options={['Landscape', 'Portrait'].map((o) => ({
				value: o,
				label: $_(`displays.orientations.${o}`)
			}))}
		/>
		<FormField
			id="display-band"
			type="number"
			label={$_('displays.fields.band')}
			bind:value={bandMinutes}
			required
			min={1}
			max={30}
		/>
		<FormField
			id="display-hysteresis"
			type="number"
			label={$_('displays.fields.hysteresis')}
			hint={$_('displays.hints.hysteresis')}
			bind:value={hysteresisMinutes}
			required
			min={0}
			max={29}
			step="any"
		/>
		<FormField
			id="display-stale"
			type="number"
			label={$_('displays.fields.stale')}
			hint={$_('displays.hints.stale')}
			bind:value={staleSeconds}
			required
			min={60}
			max={1800}
		/>
	</div>

	<fieldset class="flex flex-col gap-1.5">
		<legend class="text-xs font-medium">{$_('displays.fields.languages')}</legend>
		<div class="flex flex-wrap gap-x-4 gap-y-1.5">
			{#each displays.boardLanguages as language (language)}
				<label class="inline-flex items-center gap-1.5 text-sm">
					<input
						type="checkbox"
						checked={languages.includes(language)}
						onchange={(event) => toggleLanguage(language, event.currentTarget.checked)}
					/>
					<span>{$_(`displays.languages.${language}`)}</span>
				</label>
			{/each}
		</div>
		<p class="text-xs text-muted-foreground" data-testid="language-order">
			{$_('displays.hints.order', {
				values: { order: languages.map((l) => $_(`displays.languages.${l}`)).join(', ') }
			})}
		</p>
	</fieldset>

	<fieldset class="flex flex-col gap-2">
		<legend class="text-xs font-medium">{$_('displays.fields.entries')}</legend>
		{#if zoneNames.length === 0}
			<p class="text-xs text-muted-foreground">{$_('displays.form.noZones')}</p>
		{/if}
		{#each entries as entry, index (index)}
			<div class="flex flex-wrap items-end gap-2 rounded-lg border p-2" data-testid="display-entry">
				<label class="flex min-w-0 flex-col gap-1 text-xs font-medium">
					{$_('displays.fields.zone')}
					<select
						bind:value={entry.zone}
						class="h-9 min-w-0 rounded-md border border-input bg-background px-2 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
					>
						{#each zoneNames as zone (zone)}<option value={zone}>{zone}</option>{/each}
					</select>
				</label>
				{#each languages as language (language)}
					<label class="flex min-w-0 flex-1 flex-col gap-1 text-xs font-medium">
						{$_('displays.fields.label', {
							values: { language: $_(`displays.languages.${language}`) }
						})}
						<input
							bind:value={entry.labels[language]}
							required
							maxlength={displays.limits.label}
							dir={language === 'ar' ? 'rtl' : 'ltr'}
							class="h-9 min-w-0 rounded-md border border-input bg-background px-2 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
						/>
					</label>
				{/each}
				{#if entries.length > 1}
					<button
						type="button"
						aria-label={$_('displays.actions.removeEntry', { values: { n: index + 1 } })}
						onclick={() => (entries = entries.filter((_, i) => i !== index))}
						class="inline-flex size-9 items-center justify-center rounded-md border bg-card hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
					>
						<Trash2 class="size-4" aria-hidden="true" />
					</button>
				{/if}
			</div>
		{/each}
		{#if entries.length < displays.limits.entries}
			<button
				type="button"
				data-testid="add-entry"
				onclick={addEntry}
				class="inline-flex h-9 w-fit items-center gap-2 rounded-md border bg-card px-3 text-sm hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
			>
				<Plus class="size-4" aria-hidden="true" />
				{$_('displays.actions.addEntry')}
			</button>
		{/if}
	</fieldset>

	<fieldset class="grid gap-2 sm:grid-cols-2">
		<legend class="mb-1 text-xs font-medium">{$_('displays.fields.fallback')}</legend>
		{#each languages as language (language)}
			<label class="flex min-w-0 flex-col gap-1 text-xs font-medium">
				{$_('displays.fields.fallbackIn', {
					values: { language: $_(`displays.languages.${language}`) }
				})}
				<input
					bind:value={fallback[language]}
					required
					maxlength={displays.limits.message}
					dir={language === 'ar' ? 'rtl' : 'ltr'}
					class="h-9 min-w-0 rounded-md border border-input bg-background px-2 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
				/>
			</label>
		{/each}
	</fieldset>

	<label class="inline-flex items-center gap-2 text-sm">
		<input type="checkbox" bind:checked={enabled} />
		{$_('displays.fields.enabled')}
	</label>

	{#if problems.length}
		<ul class="list-disc ps-5 text-sm text-status-danger-foreground" data-testid="display-problems">
			{#each problems as problem, index (index)}<li>{problem}</li>{/each}
		</ul>
	{/if}

	<div class="flex flex-wrap gap-2">
		<button
			type="submit"
			data-testid="save-display"
			disabled={busy || languages.length === 0}
			class="inline-flex h-9 items-center rounded-md bg-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
		>
			{editing ? $_('displays.actions.save') : $_('displays.actions.create')}
		</button>
		<button
			type="button"
			onclick={onCancel}
			class="inline-flex h-9 items-center rounded-md border bg-card px-3 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			{$_('displays.actions.cancel')}
		</button>
	</div>
</form>
