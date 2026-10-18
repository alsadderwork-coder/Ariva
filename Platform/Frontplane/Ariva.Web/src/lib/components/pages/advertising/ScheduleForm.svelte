<script lang="ts">
	import { onMount, untrack } from 'svelte';
	import { _ } from 'svelte-i18n';
	import * as plans from '$lib/core/advertising-plans';
	import type {
		Playlist,
		Schedule,
		ScheduleRequest,
		ScheduleScope,
		WeekDay
	} from '$lib/core/advertising-plans';

	/**
	 * A schedule's settings (ARV-143a): the active playlist it plays, the displays it applies to (all of them, those
	 * showing given queue zones, or named ones, typed as a comma separated list), its days of the week and its window of
	 * the site's local time. A closing time at or before the opening time is on the next day; "until the end of the day"
	 * closes at 24:00, which a time field cannot hold. The server checks every value again.
	 */
	interface Props {
		/** The schedule being changed, or null for a new one. */
		editing: Schedule | null;
		playlists: Playlist[];
		/** Queue zones of the site's published zone profile, as a hint (empty when unknown). */
		zoneNames: string[];
		/** Codes of the site's displays, as a hint (empty when unknown). */
		displayCodes: string[];
		onSubmit: (request: ScheduleRequest) => Promise<string[]>;
		onCancel: () => void;
	}

	let { editing, playlists, zoneNames, displayCodes, onSubmit, onCancel }: Props = $props();

	const start = untrack(() => editing);
	const active = $derived(playlists.filter((p) => p.status === 'Active'));
	let playlistId = $state(
		untrack(() =>
			start
				? (playlists.find((p) => p.id === start.playlistId && p.status === 'Active')?.id ?? '')
				: (playlists.find((p) => p.status === 'Active')?.id ?? '')
		)
	);
	let scope = $state<ScheduleScope>(start?.scope ?? 'Site');
	let targetsText = $state(start ? start.targets.join(', ') : '');
	let days = $state<WeekDay[]>(start ? [...start.days] : [...plans.weekDays]);
	let opens = $state(start?.opens ?? '00:00');
	let endOfDay = $state(start ? start.closes === '24:00' : true);
	let closes = $state(start && start.closes !== '24:00' ? start.closes : '00:00');
	let problems = $state<string[]>([]);
	let busy = $state(false);
	let playlistSelect = $state<HTMLSelectElement | undefined>();

	const targets = $derived(scope === 'Site' ? [] : plans.parseTargets(targetsText));
	const nextDay = $derived(!endOfDay && closes !== '' && opens !== '' && closes <= opens);
	const separator = $derived($_('advertisingPlans.listSeparator'));
	const ready = $derived(
		playlistId !== '' &&
			days.length > 0 &&
			(scope === 'Site' || targets.length > 0) &&
			opens !== '' &&
			(endOfDay || closes !== '')
	);

	onMount(() => {
		// A schedule taken for a change brings its form into view.
		if (start && playlistSelect) {
			playlistSelect.scrollIntoView({ block: 'center' });
			playlistSelect.focus();
		}
	});

	function toggleDay(day: WeekDay, on: boolean): void {
		const next = new Set(days);
		if (on) next.add(day);
		else next.delete(day);
		days = plans.weekDays.filter((d) => next.has(d));
	}

	async function submit(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		if (busy || !ready) return;
		busy = true;
		problems = await onSubmit({
			playlistId,
			scope,
			targets: [...targets],
			days: [...days],
			opens,
			closes: endOfDay ? '24:00' : closes
		});
		busy = false;
	}

	const inputClass =
		'h-9 min-w-0 rounded-md border border-input bg-background px-2 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60';
</script>

<form
	class="flex flex-col gap-4 rounded-lg border p-4"
	data-testid="schedule-form"
	data-mode={editing ? 'edit' : 'create'}
	aria-labelledby="schedule-form-title"
	onsubmit={submit}
>
	<h3 id="schedule-form-title" class="text-sm font-semibold break-words">
		{editing
			? $_('advertisingPlans.schedules.form.editTitle', { values: { name: editing.playlistName } })
			: $_('advertisingPlans.schedules.form.createTitle')}
	</h3>

	<div class="flex max-w-xl flex-col gap-1">
		<label for="advertising-schedule-playlist" class="text-xs font-medium"
			>{$_('advertisingPlans.schedules.form.playlist')}</label
		>
		<select
			id="advertising-schedule-playlist"
			data-testid="schedule-playlist"
			bind:this={playlistSelect}
			bind:value={playlistId}
			required
			disabled={active.length === 0}
			aria-describedby={active.length === 0 ? 'advertising-schedule-no-playlists' : undefined}
			class={inputClass}
		>
			<option value="">{$_('advertisingPlans.schedules.form.choosePlaylist')}</option>
			{#each active as playlist (playlist.id)}
				<option value={playlist.id}>{playlist.name}</option>
			{/each}
		</select>
		{#if active.length === 0}
			<p
				id="advertising-schedule-no-playlists"
				class="text-xs text-muted-foreground"
				data-testid="schedule-no-playlists"
			>
				{$_('advertisingPlans.schedules.form.noPlaylists')}
			</p>
		{/if}
	</div>

	<fieldset class="flex flex-col gap-1.5">
		<legend class="text-xs font-medium">{$_('advertisingPlans.schedules.form.scope')}</legend>
		<div class="flex flex-wrap gap-x-5 gap-y-1.5">
			{#each plans.scopes as option (option)}
				<label class="inline-flex items-center gap-1.5 text-sm">
					<input
						type="radio"
						name="advertising-schedule-scope"
						value={option}
						bind:group={scope}
						data-testid={`schedule-scope-${option.toLowerCase()}`}
					/>
					{$_(`advertisingPlans.schedules.form.scopes.${option}`)}
				</label>
			{/each}
		</div>
	</fieldset>

	{#if scope !== 'Site'}
		<div class="flex max-w-xl flex-col gap-1">
			<label for="advertising-schedule-targets" class="text-xs font-medium"
				>{scope === 'Zone'
					? $_('advertisingPlans.schedules.form.zones')
					: $_('advertisingPlans.schedules.form.displays')}</label
			>
			<input
				id="advertising-schedule-targets"
				data-testid="schedule-targets"
				bind:value={targetsText}
				required
				maxlength={2000}
				autocomplete="off"
				spellcheck="false"
				aria-describedby="advertising-schedule-targets-hint"
				class={inputClass}
			/>
			<p id="advertising-schedule-targets-hint" class="text-xs break-words text-muted-foreground">
				{#if scope === 'Zone' && zoneNames.length > 0}
					{$_('advertisingPlans.schedules.form.knownZones', {
						values: { names: zoneNames.join(separator) }
					})}
				{:else if scope === 'Display' && displayCodes.length > 0}
					{$_('advertisingPlans.schedules.form.knownDisplays', {
						values: { codes: displayCodes.join(separator) }
					})}
				{/if}
			</p>
		</div>
	{/if}

	<fieldset class="flex flex-col gap-1.5">
		<legend class="text-xs font-medium">{$_('advertisingPlans.schedules.form.days')}</legend>
		<div class="flex flex-wrap gap-x-4 gap-y-1.5">
			{#each plans.weekDays as day (day)}
				<label class="inline-flex items-center gap-1.5 text-sm">
					<input
						type="checkbox"
						data-testid="schedule-day"
						data-day={day}
						checked={days.includes(day)}
						onchange={(event) => toggleDay(day, event.currentTarget.checked)}
					/>
					{$_(`advertisingPlans.days.${day}`)}
				</label>
			{/each}
		</div>
	</fieldset>

	<div class="flex flex-col gap-1">
		<div class="flex flex-wrap items-end gap-3">
			<div class="flex flex-col gap-1">
				<label for="advertising-schedule-opens" class="text-xs font-medium"
					>{$_('advertisingPlans.schedules.form.opens')}</label
				>
				<input
					id="advertising-schedule-opens"
					type="time"
					step="60"
					data-testid="schedule-opens"
					bind:value={opens}
					required
					aria-describedby="advertising-schedule-window-hint"
					class="{inputClass} tabular-nums"
				/>
			</div>
			<div class="flex flex-col gap-1">
				<label for="advertising-schedule-closes" class="text-xs font-medium"
					>{$_('advertisingPlans.schedules.form.closes')}</label
				>
				<input
					id="advertising-schedule-closes"
					type="time"
					step="60"
					data-testid="schedule-closes"
					bind:value={closes}
					required={!endOfDay}
					disabled={endOfDay}
					aria-describedby="advertising-schedule-window-hint"
					class="{inputClass} tabular-nums"
				/>
			</div>
			<label class="inline-flex h-9 items-center gap-1.5 text-sm">
				<input type="checkbox" data-testid="schedule-end-of-day" bind:checked={endOfDay} />
				{$_('advertisingPlans.schedules.form.endOfDay')}
			</label>
		</div>
		<p id="advertising-schedule-window-hint" class="text-xs text-muted-foreground">
			{$_('advertisingPlans.schedules.form.hint')}
		</p>
		{#if nextDay}
			<p class="text-xs text-status-info-foreground" data-testid="schedule-next-day">
				{$_('advertisingPlans.schedules.form.nextDay', { values: { closes } })}
			</p>
		{/if}
	</div>

	{#if problems.length > 0}
		<ul
			class="list-disc ps-5 text-xs text-destructive"
			role="alert"
			data-testid="schedule-problems"
		>
			{#each problems as problem, index (index)}
				<li>{problem}</li>
			{/each}
		</ul>
	{/if}

	<div class="flex flex-wrap gap-2">
		<button
			type="submit"
			data-testid="schedule-save"
			disabled={busy || !ready}
			class="inline-flex h-9 items-center rounded-md bg-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
		>
			{editing
				? $_('advertisingPlans.schedules.form.save')
				: $_('advertisingPlans.schedules.form.create')}
		</button>
		{#if editing}
			<button
				type="button"
				data-testid="schedule-cancel"
				onclick={onCancel}
				class="inline-flex h-9 items-center rounded-md border bg-card px-3 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
			>
				{$_('advertisingPlans.schedules.form.cancel')}
			</button>
		{/if}
	</div>
</form>
