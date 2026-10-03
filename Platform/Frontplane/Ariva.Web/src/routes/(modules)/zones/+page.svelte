<script lang="ts">
	import { Plus, Timer, Upload } from '@lucide/svelte';
	import { onDestroy, onMount } from 'svelte';
	import { _ } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import AddShapeForm from '$lib/components/pages/zones/AddShapeForm.svelte';
	import ConfirmButton from '$lib/components/shared/ConfirmButton.svelte';
	import FloorPlanUpload from '$lib/components/pages/zones/FloorPlanUpload.svelte';
	import LineDetails from '$lib/components/pages/zones/LineDetails.svelte';
	import PublishPanel from '$lib/components/pages/zones/PublishPanel.svelte';
	import ZoneCanvas, { type Selected } from '$lib/components/pages/zones/ZoneCanvas.svelte';
	import ZoneDetails from '$lib/components/pages/zones/ZoneDetails.svelte';
	import SimplePageHeader from '$lib/components/shared/SimplePageHeader.svelte';
	import { auth } from '$lib/core/auth.svelte';
	import * as topology from '$lib/core/topology';
	import type { Desk, Level, Site } from '$lib/core/topology';
	import * as zones from '$lib/core/zones';
	import type { Line, Point, Profile, ProfileSummary, Zone } from '$lib/core/zones';
	import { cn } from '$lib/utils';

	let siteList = $state<Site[]>([]);
	let siteCode = $state('');
	let history = $state<ProfileSummary[]>([]);
	let profileId = $state('');
	let profile = $state<Profile | null>(null);
	let levels = $state<Level[]>([]);
	let levelId = $state('');
	let desks = $state<Desk[]>([]);
	let plan = $state<{ url: string; x: number; y: number; width: number; height: number } | null>(
		null
	);
	let selected = $state<Selected>(null);
	let adding = $state<'zone' | 'line' | null>(null);
	let uploading = $state(false);
	/** Unsaved shapes: zone corners and line ends moved on the plan or typed in the table. */
	let zoneEdits = $state<Record<string, Point[]>>({});
	let lineEdits = $state<Record<string, [Point, Point]>>({});
	let zoneName = $state('');
	/** Bumped after every saved change, so the publish panel asks for a fresh check. */
	let revision = $state(0);

	const level = $derived(levels.find((l) => l.id === levelId) ?? null);
	const isDraft = $derived(profile?.profile.status === 'Draft');
	const editable = $derived(isDraft && auth.can('ZoneProfile.Edit'));
	const draft = $derived(history.find((p) => p.status === 'Draft') ?? null);
	const levelZones = $derived(profile?.zones.filter((z) => z.levelId === levelId) ?? []);
	const levelLines = $derived(profile?.lines.filter((l) => l.levelId === levelId) ?? []);
	const selectedZone = $derived(
		selected?.kind === 'zone' ? (profile?.zones.find((z) => z.id === selected!.id) ?? null) : null
	);
	const selectedLine = $derived(
		selected?.kind === 'line' ? (profile?.lines.find((l) => l.id === selected!.id) ?? null) : null
	);

	function pointsOf(zone: Zone): Point[] {
		return zoneEdits[zone.id] ?? zones.parsePolygon(zone.polygon) ?? [];
	}

	function endsOf(line: Line): [Point, Point] {
		return (
			lineEdits[line.id] ?? [
				{ x: line.startX, y: line.startY },
				{ x: line.endX, y: line.endY }
			]
		);
	}

	function profileLabel(p: ProfileSummary): string {
		if (p.status === 'Draft')
			return p.basedOnVersion
				? $_('zones.draftLabel', { values: { version: p.basedOnVersion } })
				: $_('zones.draftLabelNew');
		return $_('zones.versionLabel', {
			values: { version: p.version ?? '', status: $_(`zones.status.${p.status}`) }
		});
	}

	function failed(reason: string): void {
		toast.error($_('topology.messages.failed', { values: { reason } }));
	}

	function select(next: Selected): void {
		selected = next;
		adding = null;
		zoneName =
			next?.kind === 'zone' ? (profile?.zones.find((z) => z.id === next.id)?.name ?? '') : '';
	}

	async function loadHistory(prefer?: string): Promise<void> {
		const result = await zones.history(siteCode);
		history = result.data ?? [];
		const pick =
			prefer ??
			(
				history.find((p) => p.status === 'Draft') ??
				history.find((p) => p.status === 'Published') ??
				history[0]
			)?.id;
		await loadProfile(pick ?? '');
	}

	async function loadProfile(id: string): Promise<void> {
		profileId = id;
		zoneEdits = {};
		lineEdits = {};
		select(null);
		if (!id) {
			profile = null;
			return;
		}
		const result = await zones.get(id);
		if (result.hasErrors || !result.data) {
			profile = null;
			return failed(result.errorMessages[0] ?? '');
		}
		profile = result.data;
		if (!levelId || !levels.some((l) => l.id === levelId))
			levelId = profile.zones[0]?.levelId ?? levels[0]?.id ?? '';
		await loadPlan();
	}

	async function loadSite(): Promise<void> {
		const [levelResult, deskResult] = await Promise.all([
			topology.search<Level>('level', { siteCode }),
			topology.search<Desk>('desk', { siteCode })
		]);
		levels = levelResult.data?.data ?? [];
		desks = deskResult.data?.data ?? [];
		levelId = '';
		await loadHistory();
	}

	function dropPlan(): void {
		if (plan) URL.revokeObjectURL(plan.url);
		plan = null;
	}

	/** The level's plan: fetched with the token, shown through an object URL at its origin and scale. */
	async function loadPlan(): Promise<void> {
		dropPlan();
		if (!levelId) return;
		const target = levelId;
		const meta = await zones.floorPlan(target);
		if (meta.hasErrors || !meta.data) return;
		const content = await zones.floorPlanContent(target);
		if (content.hasErrors || !(content.data instanceof Blob) || target !== levelId) return;
		if (destroyed) return;
		const url = URL.createObjectURL(content.data);
		let widthPixels = meta.data.widthPixels;
		let heightPixels = meta.data.heightPixels;
		if (!widthPixels || !heightPixels) {
			const image = new Image();
			image.src = url;
			await image.decode().catch(() => undefined);
			widthPixels = image.naturalWidth || 1;
			heightPixels = image.naturalHeight || 1;
		}
		// Another level was chosen (or the screen left) while the image decoded: this plan belongs to nothing shown.
		if (target !== levelId || destroyed) {
			URL.revokeObjectURL(url);
			return;
		}
		dropPlan();
		plan = {
			url,
			x: meta.data.originX,
			y: meta.data.originY,
			width: widthPixels * meta.data.metresPerPixel,
			height: heightPixels * meta.data.metresPerPixel
		};
	}

	onMount(async () => {
		const result = await topology.sites();
		siteList = result.data ?? [];
		siteCode = siteList.find((s) => s.code === 'DMO')?.code ?? siteList[0]?.code ?? '';
		if (siteCode) await loadSite();
	});

	let destroyed = false;
	onDestroy(() => {
		destroyed = true;
		dropPlan();
	});

	function replaceZone(zone: Zone): void {
		if (!profile) return;
		profile.zones = profile.zones.some((z) => z.id === zone.id)
			? profile.zones.map((z) => (z.id === zone.id ? zone : z))
			: [...profile.zones, zone];
	}

	function replaceLine(oldId: string | null, line: Line | null): void {
		if (!profile) return;
		const kept = profile.lines.filter((l) => l.id !== oldId);
		profile.lines = line ? [...kept, line] : kept;
	}

	async function saveZone(id: string): Promise<void> {
		const zone = profile?.zones.find((z) => z.id === id);
		if (!zone || !profile) return;
		const name = selected?.kind === 'zone' && selected.id === id ? zoneName.trim() : zone.name;
		const result = await zones.updateZone(profile.profile.id, id, {
			name,
			polygon: zones.formatPolygon(pointsOf(zone))
		});
		if (result.hasErrors || !result.data) return failed(result.errorMessages[0] ?? '');
		delete zoneEdits[id];
		replaceZone(result.data);
		revision++;
		toast.success($_('zones.saved', { values: { name: result.data.name } }));
	}

	/** Lines are not changed in place: the old line goes and a new one with the same name, role and zone takes its place. */
	async function saveLine(id: string): Promise<void> {
		const line = profile?.lines.find((l) => l.id === id);
		if (!line || !profile) return;
		const [a, b] = endsOf(line);
		const removed = await zones.removeLine(profile.profile.id, id);
		if (removed.hasErrors) return failed(removed.errorMessages[0] ?? '');
		const fields = { name: line.name, role: line.role, levelId: line.levelId, zoneId: line.zoneId };
		const added = await zones.addLine(profile.profile.id, {
			...fields,
			startX: a.x,
			startY: a.y,
			endX: b.x,
			endY: b.y
		});
		delete lineEdits[id];
		if (added.hasErrors || !added.data) {
			// Put the line back where it was, so a refused move loses nothing.
			const restored = await zones.addLine(profile.profile.id, {
				...fields,
				startX: line.startX,
				startY: line.startY,
				endX: line.endX,
				endY: line.endY
			});
			replaceLine(id, restored.data ?? null);
			if (restored.data) select({ kind: 'line', id: restored.data.id });
			return failed(added.errorMessages[0] ?? '');
		}
		replaceLine(id, added.data);
		select({ kind: 'line', id: added.data.id });
		revision++;
		toast.success($_('zones.saved', { values: { name: added.data.name } }));
	}

	async function commit(target: NonNullable<Selected>): Promise<void> {
		if (target.kind === 'zone') await saveZone(target.id);
		else await saveLine(target.id);
	}

	async function deleteZone(zone: Zone): Promise<void> {
		if (!profile) return;
		const result = await zones.removeZone(profile.profile.id, zone.id);
		if (result.hasErrors) return failed(result.errorMessages[0] ?? '');
		profile.zones = profile.zones.filter((z) => z.id !== zone.id);
		select(null);
		revision++;
		toast.success($_('zones.deleted', { values: { name: zone.name } }));
	}

	async function deleteLine(line: Line): Promise<void> {
		if (!profile) return;
		const result = await zones.removeLine(profile.profile.id, line.id);
		if (result.hasErrors) return failed(result.errorMessages[0] ?? '');
		replaceLine(line.id, null);
		select(null);
		revision++;
		toast.success($_('zones.deleted', { values: { name: line.name } }));
	}

	async function added(next: { kind: 'zone' | 'line'; id: string }): Promise<void> {
		adding = null;
		await loadProfile(profileId);
		select(next);
		revision++;
	}

	async function newDraft(): Promise<void> {
		const result = await zones.createDraft(siteCode);
		if (result.hasErrors || !result.data) return failed(result.errorMessages[0] ?? '');
		toast.success($_('zones.draftCreated'));
		await loadHistory(result.data.profile.id);
	}

	async function discardDraft(): Promise<void> {
		if (!draft) return;
		const result = await zones.discard(draft.id);
		if (result.hasErrors) return failed(result.errorMessages[0] ?? '');
		toast.success($_('zones.discarded'));
		await loadHistory();
	}
</script>

<svelte:head>
	<title>{$_('zones.title')} · {$_('app.title')}</title>
</svelte:head>

<SimplePageHeader icon={Timer} title={$_('zones.title')} description={$_('zones.description')}>
	{#snippet actions()}
		<div class="flex flex-wrap items-end gap-2">
			<div class="flex flex-col gap-1">
				<label for="zones-site" class="text-xs font-medium">{$_('topology.site')}</label>
				<select
					id="zones-site"
					bind:value={siteCode}
					onchange={loadSite}
					class="h-10 rounded-lg border border-input bg-card px-3 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
				>
					{#each siteList as site (site.code)}
						<option value={site.code}
							>{site.name && site.name !== site.code
								? `${site.code}, ${site.name}`
								: site.code}</option
						>
					{/each}
				</select>
			</div>
			<div class="flex flex-col gap-1">
				<label for="zones-profile" class="text-xs font-medium">{$_('zones.profile')}</label>
				<select
					id="zones-profile"
					value={profileId}
					onchange={(event) => loadProfile(event.currentTarget.value)}
					class="h-10 rounded-lg border border-input bg-card px-3 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
				>
					{#each history as item (item.id)}
						<option value={item.id}>{profileLabel(item)}</option>
					{/each}
				</select>
			</div>
		</div>
	{/snippet}
</SimplePageHeader>

<div class="mb-4 flex flex-wrap items-end gap-3">
	<div class="flex flex-col gap-1">
		<label for="zones-level" class="text-xs font-medium">{$_('zones.level')}</label>
		<select
			id="zones-level"
			bind:value={levelId}
			onchange={() => (select(null), loadPlan())}
			class="h-10 rounded-lg border border-input bg-card px-3 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			{#each levels as item (item.id)}
				<option value={item.id}>{item.code}, {item.name}</option>
			{/each}
		</select>
	</div>
	{#if editable && level}
		<button
			type="button"
			data-testid="add-zone"
			onclick={() => ((adding = adding === 'zone' ? null : 'zone'), (selected = null))}
			class="inline-flex h-10 items-center gap-1 rounded-lg border bg-card px-3 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			<Plus class="size-4" aria-hidden="true" />
			{$_('zones.addZone')}
		</button>
		<button
			type="button"
			data-testid="add-line"
			onclick={() => ((adding = adding === 'line' ? null : 'line'), (selected = null))}
			class="inline-flex h-10 items-center gap-1 rounded-lg border bg-card px-3 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			<Plus class="size-4" aria-hidden="true" />
			{$_('zones.addLine')}
		</button>
	{/if}
	{#if auth.can('FloorPlan.Create') && level}
		<button
			type="button"
			data-testid="upload-plan"
			onclick={() => (uploading = !uploading)}
			class="inline-flex h-10 items-center gap-1 rounded-lg border bg-card px-3 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			<Upload class="size-4" aria-hidden="true" />
			{$_('zones.uploadPlan')}
		</button>
	{/if}
	<div class="ms-auto flex flex-wrap gap-2">
		{#if !draft && auth.can('ZoneProfile.Create')}
			<button
				type="button"
				data-testid="new-draft"
				onclick={newDraft}
				class="inline-flex h-10 items-center rounded-lg bg-button-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none dark:text-white"
			>
				{$_('zones.newDraft')}
			</button>
		{/if}
		{#if draft && isDraft && auth.can('ZoneProfile.Delete')}
			<ConfirmButton
				label={$_('zones.discard')}
				confirmLabel={$_('zones.confirmDiscard')}
				testId="discard"
				onConfirm={discardDraft}
			/>
		{/if}
	</div>
</div>

{#if profile && !isDraft}
	<p
		data-testid="zones-read-only"
		class="mb-4 rounded-lg border border-status-info-border bg-status-info px-4 py-3 text-sm text-status-info-foreground"
	>
		{auth.can('ZoneProfile.Edit') ? $_('zones.readOnly') : $_('zones.noRights')}
	</p>
{/if}

{#if uploading && level}
	<div class="mb-4 max-w-xl">
		<FloorPlanUpload
			levelId={level.id}
			onCancel={() => (uploading = false)}
			onUploaded={async () => ((uploading = false), await loadPlan())}
		/>
	</div>
{/if}

{#if profile && level}
	<div class="grid gap-4 xl:grid-cols-[minmax(0,2fr)_minmax(20rem,1fr)]">
		<div class="flex min-w-0 flex-col gap-2">
			<ZoneCanvas
				width={level.widthMetres}
				depth={level.depthMetres}
				zones={levelZones}
				lines={levelLines}
				{pointsOf}
				{endsOf}
				{selected}
				{editable}
				{plan}
				onSelect={select}
				onMoveVertex={(zoneId, index, point) => {
					const zone = profile?.zones.find((z) => z.id === zoneId);
					if (zone) zoneEdits[zoneId] = pointsOf(zone).map((p, i) => (i === index ? point : p));
				}}
				onMoveLineEnd={(lineId, end, point) => {
					const line = profile?.lines.find((l) => l.id === lineId);
					if (line) {
						const [a, b] = endsOf(line);
						lineEdits[lineId] = end === 'start' ? [point, b] : [a, point];
					}
				}}
				onCommit={commit}
			/>
			<p class="text-xs text-muted-foreground">
				{plan ? `${$_('zones.plan')}: ${level.code}` : $_('zones.noPlan')}
			</p>
		</div>
		<aside class="flex min-w-0 flex-col gap-4">
			{#if adding}
				<div class="rounded-xl border bg-card p-4">
					<AddShapeForm
						mode={adding}
						profileId={profile.profile.id}
						levelId={level.id}
						width={level.widthMetres}
						depth={level.depthMetres}
						owners={levelZones.filter((z) => z.kind === 'Queue' || z.kind === 'Overflow')}
						{desks}
						onAdded={added}
						onCancel={() => (adding = null)}
						onFailed={failed}
					/>
				</div>
			{:else if selectedZone}
				<div class="rounded-xl border bg-card p-4">
					{#key selectedZone.id}
						<ZoneDetails
							zone={selectedZone}
							points={pointsOf(selectedZone)}
							queueZoneName={profile.zones.find((z) => z.id === selectedZone.queueZoneId)?.name ??
								null}
							deskCode={desks.find((d) => d.id === selectedZone.deskId)?.code ?? null}
							{editable}
							dirty={!!zoneEdits[selectedZone.id] || zoneName.trim() !== selectedZone.name}
							width={level.widthMetres}
							depth={level.depthMetres}
							bind:name={zoneName}
							onPoints={(points) => (zoneEdits[selectedZone.id] = points)}
							onSave={() => saveZone(selectedZone.id)}
							onRevert={() => {
								delete zoneEdits[selectedZone.id];
								zoneName = selectedZone.name;
							}}
							onDelete={() => deleteZone(selectedZone)}
						/>
					{/key}
				</div>
			{:else if selectedLine}
				<div class="rounded-xl border bg-card p-4">
					{#key selectedLine.id}
						<LineDetails
							line={selectedLine}
							ends={endsOf(selectedLine)}
							zoneName={profile.zones.find((z) => z.id === selectedLine.zoneId)?.name ?? null}
							{editable}
							dirty={!!lineEdits[selectedLine.id]}
							width={level.widthMetres}
							depth={level.depthMetres}
							onEnds={(ends) => (lineEdits[selectedLine.id] = ends)}
							onSave={() => saveLine(selectedLine.id)}
							onRevert={() => delete lineEdits[selectedLine.id]}
							onDelete={() => deleteLine(selectedLine)}
						/>
					{/key}
				</div>
			{/if}

			<section class="rounded-xl border bg-card p-4" aria-labelledby="zones-list-title">
				<h2 id="zones-list-title" class="mb-2 text-sm font-semibold">{$_('zones.zonesList')}</h2>
				{#if levelZones.length === 0}
					<p class="text-sm text-muted-foreground">{$_('zones.noZones')}</p>
				{:else}
					<ul
						class="sidebar-scroll flex max-h-64 flex-col overflow-y-auto"
						data-testid="zones-list"
					>
						{#each levelZones as zone (zone.id)}
							<li>
								<button
									type="button"
									aria-current={selected?.kind === 'zone' && selected.id === zone.id
										? 'true'
										: undefined}
									onclick={() => select({ kind: 'zone', id: zone.id })}
									class={cn(
										'flex w-full items-center justify-between gap-2 rounded-md px-2 py-1.5 text-start text-sm hover:bg-muted focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none',
										selected?.kind === 'zone' &&
											selected.id === zone.id &&
											'bg-primary/10 font-medium dark:bg-button-primary/20'
									)}
								>
									<span class="truncate">{zone.name}</span>
									<span class="shrink-0 text-xs text-muted-foreground"
										>{$_(`zones.kinds.${zone.kind}`)}</span
									>
								</button>
							</li>
						{/each}
					</ul>
				{/if}
				<h2 class="mt-4 mb-2 text-sm font-semibold">{$_('zones.linesList')}</h2>
				{#if levelLines.length === 0}
					<p class="text-sm text-muted-foreground">{$_('zones.noLines')}</p>
				{:else}
					<ul
						class="sidebar-scroll flex max-h-48 flex-col overflow-y-auto"
						data-testid="lines-list"
					>
						{#each levelLines as line (line.id)}
							<li>
								<button
									type="button"
									aria-current={selected?.kind === 'line' && selected.id === line.id
										? 'true'
										: undefined}
									onclick={() => select({ kind: 'line', id: line.id })}
									class={cn(
										'flex w-full items-center justify-between gap-2 rounded-md px-2 py-1.5 text-start text-sm hover:bg-muted focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none',
										selected?.kind === 'line' &&
											selected.id === line.id &&
											'bg-primary/10 font-medium dark:bg-button-primary/20'
									)}
								>
									<span class="truncate">{line.name}</span>
									<span class="shrink-0 text-xs text-muted-foreground"
										>{$_(`zones.roles.${line.role}`)}</span
									>
								</button>
							</li>
						{/each}
					</ul>
				{/if}
			</section>

			{#if isDraft}
				<PublishPanel
					profileId={profile.profile.id}
					canPublish={auth.can('ZoneProfile.Publish')}
					unsaved={Object.keys(zoneEdits).length + Object.keys(lineEdits).length > 0}
					{revision}
					onPublished={(id) => loadHistory(id)}
				/>
			{/if}
		</aside>
	</div>
{/if}
