<script lang="ts">
	import { untrack } from 'svelte';
	import { _ } from 'svelte-i18n';
	import FormField from '$lib/components/shared/FormField.svelte';
	import SelectField from '$lib/components/shared/SelectField.svelte';
	import * as displays from '$lib/core/displays';
	import * as slides from '$lib/core/slides';
	import type { Slide, SlideRequest } from '$lib/core/slides';
	import SlideView from './SlideView.svelte';

	/**
	 * An instruction slide's settings (ARV-141): where it shows, its icon and time on screen, whether it is pinned under the
	 * waits, and its words per language, with a preview of each language as a display shows it (Arabic right to left).
	 * Every word is plain text: the preview and the displays show it as text, never as markup.
	 */
	interface Props {
		siteCode: string;
		/** The slide being changed, or null for a new one. */
		editing: Slide | null;
		/** Queue zones of the site's published zone profile. */
		zoneNames: string[];
		/** Codes of the site's displays. */
		displayCodes: string[];
		onSubmit: (request: SlideRequest) => Promise<string[]>;
		onCancel: () => void;
	}

	let { siteCode, editing, zoneNames, displayCodes, onSubmit, onCancel }: Props = $props();

	const rtl = new Set(['ar']);
	const start = untrack(() => editing);
	let scope = $state<string>(start?.scope ?? 'Site');
	let targets = $state<string[]>(start ? [...start.targets] : []);
	let icon = $state<string>(start?.icon ?? 'Information');
	let durationSeconds = $state<number | string>(start?.durationSeconds ?? 10);
	let pinned = $state(start?.pinned ?? false);
	let words = $state<Record<string, { title: string; body: string }>>(
		Object.fromEntries(
			displays.boardLanguages.map((l) => [
				l,
				{ title: start?.texts[l]?.title ?? '', body: start?.texts[l]?.body ?? '' }
			])
		)
	);
	let problems = $state<string[]>([]);
	let busy = $state(false);

	const choices = $derived(scope === 'Zone' ? zoneNames : scope === 'Display' ? displayCodes : []);
	/** The languages with a title, in the board's order: what the slide will say. */
	const written = $derived(displays.boardLanguages.filter((l) => words[l].title.trim().length > 0));

	function toggleTarget(target: string, on: boolean): void {
		targets = on ? [...targets, target] : targets.filter((t) => t !== target);
	}

	function changeScope(next: string): void {
		scope = next;
		targets = [];
	}

	async function submit(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		if (busy) return;
		busy = true;
		problems = await onSubmit({
			siteCode,
			scope,
			targets: scope === 'Site' ? [] : [...targets],
			icon,
			durationSeconds: Number(durationSeconds),
			pinned,
			texts: Object.fromEntries(
				written.map((l) => [
					l,
					{ title: words[l].title.trim(), body: words[l].body.trim() || null }
				])
			)
		});
		busy = false;
	}
</script>

<!-- Every word is text: the preview below and the displays show it as text, never as markup. -->
<form
	class="flex flex-col gap-4 rounded-xl border bg-card p-4"
	data-testid="slide-form"
	aria-labelledby="slide-form-title"
	onsubmit={submit}
>
	<h2 id="slide-form-title" class="text-base font-semibold">
		{editing
			? $_('slides.form.editTitle', { values: { position: editing.position } })
			: $_('slides.form.createTitle')}
	</h2>
	<div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
		<div class="flex min-w-0 flex-col gap-1">
			<label for="slide-scope" class="text-xs font-medium">{$_('slides.form.scope')}</label>
			<select
				id="slide-scope"
				name="slide-scope"
				value={scope}
				onchange={(event) => changeScope(event.currentTarget.value)}
				class="h-9 min-w-0 rounded-md border border-input bg-background px-2 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
			>
				{#each slides.scopes as option (option)}
					<option value={option}>{$_(`slides.scopes.${option}`)}</option>
				{/each}
			</select>
		</div>
		<SelectField
			id="slide-icon"
			label={$_('slides.form.icon')}
			bind:value={icon}
			options={slides.icons.map((i) => ({ value: i, label: $_(`slides.icons.${i}`) }))}
		/>
		<FormField
			id="slide-duration"
			type="number"
			label={$_('slides.form.duration')}
			bind:value={durationSeconds}
			required
			min={slides.limits.minSeconds}
			max={slides.limits.maxSeconds}
		/>
		<label class="inline-flex items-center gap-2 self-end pb-2 text-sm">
			<input type="checkbox" bind:checked={pinned} data-testid="slide-pinned" />
			{$_('slides.form.pinned')}
		</label>
	</div>

	{#if scope !== 'Site'}
		<fieldset class="flex flex-col gap-1.5" data-testid="slide-targets">
			<legend class="text-xs font-medium">
				{scope === 'Zone' ? $_('slides.form.zones') : $_('slides.form.displays')}
			</legend>
			{#if choices.length === 0}
				<p class="text-xs text-muted-foreground">
					{scope === 'Zone' ? $_('slides.form.noZones') : $_('slides.form.noDisplays')}
				</p>
			{/if}
			<div class="flex flex-wrap gap-x-4 gap-y-1.5">
				{#each choices as choice (choice)}
					<label class="inline-flex items-center gap-1.5 text-sm">
						<input
							type="checkbox"
							checked={targets.includes(choice)}
							disabled={!targets.includes(choice) && targets.length >= slides.limits.targets}
							onchange={(event) => toggleTarget(choice, event.currentTarget.checked)}
						/>
						<span class="font-mono text-xs">{choice}</span>
					</label>
				{/each}
			</div>
		</fieldset>
	{/if}

	<fieldset class="flex flex-col gap-2">
		<legend class="text-xs font-medium">{$_('slides.form.words')}</legend>
		<p class="text-xs text-muted-foreground">{$_('slides.form.wordsHint')}</p>
		<div class="grid gap-3 md:grid-cols-2">
			{#each displays.boardLanguages as language (language)}
				<div class="flex flex-col gap-1.5 rounded-lg border p-2" data-testid="slide-words">
					<label class="flex min-w-0 flex-col gap-1 text-xs font-medium">
						{$_('slides.form.titleIn', {
							values: { language: $_(`displays.languages.${language}`) }
						})}
						<input
							bind:value={words[language].title}
							maxlength={slides.limits.title}
							dir={rtl.has(language) ? 'rtl' : 'ltr'}
							lang={language}
							class="h-9 min-w-0 rounded-md border border-input bg-background px-2 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
						/>
					</label>
					<label class="flex min-w-0 flex-col gap-1 text-xs font-medium">
						{$_('slides.form.bodyIn', {
							values: { language: $_(`displays.languages.${language}`) }
						})}
						<input
							bind:value={words[language].body}
							maxlength={slides.limits.body}
							dir={rtl.has(language) ? 'rtl' : 'ltr'}
							lang={language}
							class="h-9 min-w-0 rounded-md border border-input bg-background px-2 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
						/>
					</label>
				</div>
			{/each}
		</div>
	</fieldset>

	<section class="flex flex-col gap-2" aria-labelledby="slide-preview-title">
		<div>
			<h3 id="slide-preview-title" class="text-sm font-semibold">{$_('slides.preview.title')}</h3>
			<p class="text-xs text-muted-foreground">{$_('slides.preview.description')}</p>
		</div>
		{#if written.length === 0}
			<p class="text-xs text-muted-foreground">{$_('slides.preview.empty')}</p>
		{:else}
			<!-- One preview per language, on the board's own colours; Arabic right to left (SlideView sets dir per language). -->
			<div class="grid gap-2 md:grid-cols-2">
				{#each written as language (language)}
					<div
						data-testid="slide-preview"
						data-language={language}
						class="rounded-lg bg-[#0b1220] p-4 text-[10px] text-white"
					>
						<SlideView
							{icon}
							texts={[
								{
									language,
									title: words[language].title.trim(),
									body: words[language].body.trim() || null
								}
							]}
							{rtl}
							compact={pinned}
							testid="slide-preview-view"
						/>
					</div>
				{/each}
			</div>
		{/if}
	</section>

	{#if problems.length}
		<ul class="list-disc ps-5 text-sm text-status-danger-foreground" data-testid="slide-problems">
			{#each problems as problem, index (index)}<li>{problem}</li>{/each}
		</ul>
	{/if}

	<div class="flex flex-wrap gap-2">
		<button
			type="submit"
			data-testid="save-slide"
			disabled={busy || written.length === 0 || (scope !== 'Site' && targets.length === 0)}
			class="inline-flex h-9 items-center rounded-md bg-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
		>
			{editing ? $_('slides.actions.save') : $_('slides.actions.create')}
		</button>
		<button
			type="button"
			onclick={onCancel}
			class="inline-flex h-9 items-center rounded-md border bg-card px-3 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			{$_('slides.actions.cancel')}
		</button>
	</div>
</form>
