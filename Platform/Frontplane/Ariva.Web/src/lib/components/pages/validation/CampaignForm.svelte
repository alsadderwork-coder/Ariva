<script lang="ts">
	import { CalendarPlus, X } from '@lucide/svelte';
	import { onDestroy, onMount } from 'svelte';
	import { _, locale } from 'svelte-i18n';
	import FormField from '$lib/components/shared/FormField.svelte';
	import * as topology from '$lib/core/topology';
	import type { Checkpoint, Desk } from '$lib/core/topology';
	import {
		campaignLimits,
		placeholderTargets,
		type CreateCampaignRequest
	} from '$lib/core/validation';
	import * as zonesApi from '$lib/core/zones';
	import type { Line, ProfileSummary, Zone } from '$lib/core/zones';
	import { addDays, dayLabel, today } from './campaignFormat';

	interface Props {
		siteCode: string;
		/** Border desks are offered only to a caller who sees them (BorderDesks.View); the server checks it too. */
		seesDesks: boolean;
		onSubmit: (request: CreateCampaignRequest) => Promise<string[]>;
		onCancel: () => void;
	}

	let { siteCode, seesDesks, onSubmit, onCancel }: Props = $props();

	interface BorderDesk {
		id: string;
		checkpoint: string;
		code: string;
	}

	let published = $state<ProfileSummary | null>(null);
	let queueZones = $state<Zone[]>([]);
	let allZones = $state<Zone[]>([]);
	let allLines = $state<Line[]>([]);
	let borderDesks = $state<BorderDesk[]>([]);
	let loading = $state(true);
	let loadProblem = $state('');

	let name = $state('');
	let zoneIds = $state<string[]>([]);
	let lineIds = $state<string[]>([]);
	let days = $state<string[]>([]);
	let dayInput = $state(today());
	let bins = $state<string | number>('');
	let runs = $state<string | number>('');
	let deskIds = $state<string[]>([]);
	let problems = $state<string[]>([]);
	let busy = $state(false);
	let destroyed = false;

	const earliest = addDays(today(), -campaignLimits.daysBack);
	const latest = addDays(today(), campaignLimits.daysAhead);

	/** The queue zone whose crossings a line counts: its own queue zone, or the queue zone of its overflow band. */
	function ownerOf(line: Line): string | null {
		const zone = allZones.find((z) => z.id === line.zoneId);
		if (!zone) return null;
		if (zone.kind === 'Queue') return zone.id;
		if (zone.kind === 'Overflow' && zone.queueZoneId) {
			const queue = allZones.find((z) => z.id === zone.queueZoneId);
			return queue?.kind === 'Queue' ? queue.id : null;
		}
		return null;
	}

	const zoneName = (id: string | null): string => allZones.find((z) => z.id === id)?.name ?? '';

	/** The lines of the chosen queue zones and their overflow bands, by zone name then line name. */
	const eligibleLines = $derived(
		allLines
			.map((line) => ({ line, owner: ownerOf(line) }))
			.filter((entry) => entry.owner !== null && zoneIds.includes(entry.owner))
			.sort(
				(a, b) =>
					zoneName(a.owner).localeCompare(zoneName(b.owner)) ||
					a.line.name.localeCompare(b.line.name)
			)
	);
	const zoneDays = $derived(zoneIds.length * days.length);

	function toggleZone(id: string, on: boolean): void {
		zoneIds = on ? [...zoneIds, id] : zoneIds.filter((z) => z !== id);
		if (on) {
			// A queue zone chosen brings its lines; any of them can be taken out again.
			const added = allLines.filter((l) => ownerOf(l) === id && !lineIds.includes(l.id));
			lineIds = [...lineIds, ...added.map((l) => l.id)];
		} else {
			lineIds = lineIds.filter((lineId) => {
				const line = allLines.find((l) => l.id === lineId);
				return line !== undefined && ownerOf(line) !== id;
			});
		}
	}

	function toggleLine(id: string, on: boolean): void {
		lineIds = on ? [...lineIds, id] : lineIds.filter((l) => l !== id);
	}

	function toggleDesk(id: string, on: boolean): void {
		deskIds = on ? [...deskIds, id] : deskIds.filter((d) => d !== id);
	}

	function addDay(): void {
		const day = dayInput;
		if (!/^\d{4}-\d{2}-\d{2}$/.test(day) || days.includes(day)) return;
		days = [...days, day].sort();
	}

	function removeDay(day: string): void {
		days = days.filter((d) => d !== day);
	}

	/** A target field: empty for the placeholder, otherwise a whole number within its bounds (undefined when not). */
	function target(
		value: string | number | null | undefined,
		min: number,
		max: number
	): number | null | undefined {
		const text = String(value ?? '').trim();
		if (text === '') return null;
		const n = Number(text);
		return Number.isInteger(n) && n >= min && n <= max ? n : undefined;
	}

	async function submit(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		if (busy || !published?.version) return;
		const binsTarget = target(bins, 1, campaignLimits.targetBinsPerLine);
		const runsTarget = target(runs, 0, campaignLimits.targetTracerRuns);
		const found: string[] = [];
		const trimmed = name.trim();
		if (trimmed.length === 0 || trimmed.length > campaignLimits.name)
			found.push($_('validationCampaigns.form.problems.name'));
		if (zoneIds.length === 0 || zoneIds.length > campaignLimits.zones)
			found.push($_('validationCampaigns.form.problems.zones'));
		if (lineIds.length > campaignLimits.lines)
			found.push($_('validationCampaigns.form.problems.lines'));
		if (days.length === 0 || days.length > campaignLimits.days)
			found.push($_('validationCampaigns.form.problems.days'));
		else if (zoneDays > campaignLimits.zoneDays)
			found.push($_('validationCampaigns.form.problems.zoneDays'));
		if (binsTarget === undefined || runsTarget === undefined)
			found.push($_('validationCampaigns.form.problems.targets'));
		if (deskIds.length > campaignLimits.desks)
			found.push($_('validationCampaigns.form.problems.desks'));
		problems = found;
		if (found.length > 0) return;
		busy = true;
		problems = await onSubmit({
			name: trimmed,
			profileVersion: published.version,
			zoneIds: queueZones.map((z) => z.id).filter((id) => zoneIds.includes(id)),
			lineIds: eligibleLines.map((e) => e.line.id).filter((id) => lineIds.includes(id)),
			days,
			targetBinsPerLine: binsTarget ?? null,
			targetTracerRuns: runsTarget ?? null,
			deskIds: seesDesks ? borderDesks.map((d) => d.id).filter((id) => deskIds.includes(id)) : []
		});
		if (!destroyed) busy = false;
	}

	async function loadDesks(): Promise<BorderDesk[]> {
		const [checkpoints, desks] = await Promise.all([
			topology.search<Checkpoint>('checkpoint', { siteCode }),
			topology.search<Desk>('desk', { siteCode })
		]);
		const border = new Map(
			(checkpoints.data?.data ?? [])
				.filter((c) => c.kind === 'Immigration' || c.kind === 'Emigration')
				.map((c) => [c.id, c.code])
		);
		// Staffed border desks in service: the server accepts no other (ValidationCampaign.IsBorderDesk).
		return (desks.data?.data ?? [])
			.filter((d) => d.kind === 'Desk' && d.inService && border.has(d.checkpointId))
			.map((d) => ({ id: d.id, checkpoint: border.get(d.checkpointId) ?? '', code: d.code }))
			.sort((a, b) => a.checkpoint.localeCompare(b.checkpoint) || a.code.localeCompare(b.code));
	}

	onMount(async () => {
		const history = await zonesApi.history(siteCode);
		if (destroyed) return;
		if (history.hasErrors) {
			loadProblem = $_('validationCampaigns.failed', {
				values: { reason: history.errorMessages.join(' ') }
			});
			loading = false;
			return;
		}
		const found = (history.data ?? []).find((p) => p.status === 'Published') ?? null;
		const [profile, desks] = await Promise.all([
			found ? zonesApi.get(found.id) : Promise.resolve(null),
			seesDesks ? loadDesks() : Promise.resolve([])
		]);
		if (destroyed) return;
		if (profile?.hasErrors) {
			loadProblem = $_('validationCampaigns.failed', {
				values: { reason: profile.errorMessages.join(' ') }
			});
		}
		published = profile?.data ? found : null;
		allZones = profile?.data?.zones ?? [];
		allLines = profile?.data?.lines ?? [];
		queueZones = allZones
			.filter((z) => z.kind === 'Queue')
			.sort((a, b) => a.name.localeCompare(b.name));
		borderDesks = desks;
		loading = false;
	});

	onDestroy(() => {
		destroyed = true;
	});
</script>

<form
	class="mb-4 flex flex-col gap-5 rounded-xl border bg-card p-5"
	data-testid="campaign-form"
	aria-labelledby="campaign-form-title"
	novalidate
	onsubmit={submit}
>
	<div>
		<h2 id="campaign-form-title" class="text-xl font-semibold">
			{$_('validationCampaigns.form.title')}
		</h2>
		<p class="mt-1 max-w-prose text-sm text-secondary-foreground">
			{$_('validationCampaigns.form.intro')}
		</p>
	</div>

	{#if loading}
		<p class="text-sm text-muted-foreground" role="status">
			{$_('validationCampaigns.form.loadingProfile')}
		</p>
	{:else if loadProblem}
		<p role="alert" class="text-sm text-status-danger-foreground">{loadProblem}</p>
	{:else if !published}
		<p
			class="rounded-lg border border-status-warning-border bg-status-warning p-3 text-sm text-status-warning-foreground"
			data-testid="campaign-form-no-profile"
		>
			{$_('validationCampaigns.form.noProfile')}
		</p>
	{:else}
		<div class="grid gap-4 md:grid-cols-2">
			<FormField
				id="campaign-name"
				label={$_('validationCampaigns.form.name')}
				hint={$_('validationCampaigns.form.nameHint')}
				bind:value={name}
				required
				maxlength={campaignLimits.name}
			/>
			<div class="flex min-w-0 flex-col gap-1">
				<p class="text-xs font-medium">{$_('validationCampaigns.form.version')}</p>
				<p class="text-sm tabular-nums" data-testid="campaign-form-version">
					{$_('validationCampaigns.form.versionValue', {
						values: {
							version: published.version ?? '',
							hash: (published.geometryHash ?? '').slice(0, 12)
						}
					})}
				</p>
			</div>
		</div>

		<fieldset class="flex flex-col gap-2" data-testid="campaign-form-zones">
			<legend class="text-sm font-semibold">{$_('validationCampaigns.form.zones')}</legend>
			<p class="text-xs text-muted-foreground">{$_('validationCampaigns.form.zonesHint')}</p>
			{#if queueZones.length === 0}
				<p class="text-sm text-muted-foreground">{$_('validationCampaigns.form.noZones')}</p>
			{:else}
				<div class="flex flex-wrap gap-x-5 gap-y-2">
					{#each queueZones as zone, index (index)}
						<label
							data-testid="campaign-form-zone"
							class="inline-flex min-h-6 items-center gap-2 text-sm"
						>
							<input
								type="checkbox"
								checked={zoneIds.includes(zone.id)}
								onchange={(event) => toggleZone(zone.id, event.currentTarget.checked)}
							/>
							<span class="break-all">{zone.name}</span>
						</label>
					{/each}
				</div>
			{/if}
		</fieldset>

		<fieldset class="flex flex-col gap-2" data-testid="campaign-form-lines">
			<legend class="text-sm font-semibold">{$_('validationCampaigns.form.lines')}</legend>
			<p class="text-xs text-muted-foreground">{$_('validationCampaigns.form.linesHint')}</p>
			{#if eligibleLines.length === 0}
				<p class="text-sm text-muted-foreground">{$_('validationCampaigns.form.linesNone')}</p>
			{:else}
				<div class="flex flex-wrap gap-x-5 gap-y-2">
					{#each eligibleLines as entry, index (index)}
						<label
							data-testid="campaign-form-line"
							class="inline-flex min-h-6 items-center gap-2 text-sm"
						>
							<input
								type="checkbox"
								checked={lineIds.includes(entry.line.id)}
								onchange={(event) => toggleLine(entry.line.id, event.currentTarget.checked)}
							/>
							<span class="break-all"
								>{$_('validationCampaigns.form.lineLabel', {
									values: {
										line: entry.line.name,
										role: $_(`validation.tally.roles.${entry.line.role}`),
										zone: zoneName(entry.owner)
									}
								})}</span
							>
						</label>
					{/each}
				</div>
			{/if}
		</fieldset>

		<fieldset class="flex flex-col gap-2" data-testid="campaign-form-days">
			<legend class="text-sm font-semibold">{$_('validationCampaigns.form.days')}</legend>
			<p class="text-xs text-muted-foreground">{$_('validationCampaigns.form.daysHint')}</p>
			<div class="flex flex-wrap items-end gap-2">
				<div class="flex flex-col gap-1">
					<label for="campaign-day" class="text-xs font-medium"
						>{$_('validationCampaigns.form.dayInput')}</label
					>
					<input
						id="campaign-day"
						data-testid="campaign-form-day"
						type="date"
						bind:value={dayInput}
						min={earliest}
						max={latest}
						class="h-9 rounded-md border border-input bg-background px-2.5 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
					/>
				</div>
				<button
					type="button"
					data-testid="campaign-form-add-day"
					onclick={addDay}
					class="inline-flex h-9 items-center gap-1.5 rounded-md border bg-card px-3 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
				>
					<CalendarPlus class="size-4" aria-hidden="true" />
					{$_('validationCampaigns.form.addDay')}
				</button>
			</div>
			{#if days.length === 0}
				<p class="text-sm text-muted-foreground">{$_('validationCampaigns.form.noDays')}</p>
			{:else}
				<ul class="flex flex-wrap gap-2" aria-label={$_('validationCampaigns.form.days')}>
					{#each days as day (day)}
						<li
							data-testid="campaign-form-chosen-day"
							data-day={day}
							class="inline-flex items-center gap-1 rounded-full border bg-surface-2 ps-3 pe-1 text-sm tabular-nums"
						>
							<time datetime={day}>{dayLabel(day, $locale)}</time>
							<button
								type="button"
								onclick={() => removeDay(day)}
								aria-label={$_('validationCampaigns.form.removeDay', {
									values: { day: dayLabel(day, $locale) }
								})}
								class="inline-flex size-7 items-center justify-center rounded-full hover:bg-muted focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
							>
								<X class="size-3.5" aria-hidden="true" />
							</button>
						</li>
					{/each}
				</ul>
			{/if}
			<p
				class="text-xs {zoneDays > campaignLimits.zoneDays
					? 'text-status-danger-foreground'
					: 'text-muted-foreground'}"
				data-testid="campaign-form-zone-days"
			>
				{$_('validationCampaigns.form.zoneDays', {
					values: { zoneDays, max: campaignLimits.zoneDays }
				})}
			</p>
		</fieldset>

		<fieldset class="flex flex-col gap-2">
			<legend class="text-sm font-semibold">{$_('validationCampaigns.form.targets')}</legend>
			<p class="text-xs text-muted-foreground">
				{$_('validationCampaigns.form.targetsHint', {
					values: { bins: placeholderTargets.binsPerLine, runs: placeholderTargets.tracerRuns }
				})}
			</p>
			<div class="grid max-w-md gap-3 sm:grid-cols-2">
				<FormField
					id="campaign-bins"
					type="number"
					label={$_('validationCampaigns.form.binsPerLine')}
					bind:value={bins}
					min={1}
					max={campaignLimits.targetBinsPerLine}
					step={1}
				/>
				<FormField
					id="campaign-runs"
					type="number"
					label={$_('validationCampaigns.form.tracerRuns')}
					bind:value={runs}
					min={0}
					max={campaignLimits.targetTracerRuns}
					step={1}
				/>
			</div>
		</fieldset>

		{#if seesDesks}
			<fieldset class="flex flex-col gap-2" data-testid="campaign-form-desks">
				<legend class="text-sm font-semibold">{$_('validationCampaigns.form.desks')}</legend>
				<p class="text-xs text-muted-foreground">{$_('validationCampaigns.form.desksHint')}</p>
				{#if borderDesks.length === 0}
					<p class="text-sm text-muted-foreground">{$_('validationCampaigns.form.noDesks')}</p>
				{:else}
					<div class="flex flex-wrap gap-x-5 gap-y-2">
						{#each borderDesks as desk, index (index)}
							<label
								data-testid="campaign-form-desk"
								class="inline-flex min-h-6 items-center gap-2 text-sm"
							>
								<input
									type="checkbox"
									checked={deskIds.includes(desk.id)}
									onchange={(event) => toggleDesk(desk.id, event.currentTarget.checked)}
								/>
								<span class="font-mono text-xs"
									>{$_('validationCampaigns.form.deskLabel', {
										values: { checkpoint: desk.checkpoint, desk: desk.code }
									})}</span
								>
							</label>
						{/each}
					</div>
				{/if}
			</fieldset>
		{/if}
	{/if}

	{#if problems.length}
		<ul
			role="alert"
			class="list-disc ps-5 text-sm text-status-danger-foreground"
			data-testid="campaign-form-problems"
		>
			{#each problems as problem, index (index)}<li>{problem}</li>{/each}
		</ul>
	{/if}

	<div class="flex flex-wrap gap-2">
		{#if published}
			<button
				type="submit"
				data-testid="campaign-form-create"
				disabled={busy}
				class="inline-flex h-10 items-center rounded-lg bg-button-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60 dark:text-white"
			>
				{$_('validationCampaigns.form.create')}
			</button>
		{/if}
		<button
			type="button"
			data-testid="campaign-form-cancel"
			onclick={onCancel}
			class="inline-flex h-10 items-center rounded-lg border bg-card px-4 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			{$_('validationCampaigns.form.cancel')}
		</button>
	</div>
</form>
