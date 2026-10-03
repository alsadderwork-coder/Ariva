<script lang="ts">
	import { Map as MapIcon } from '@lucide/svelte';
	import { onMount } from 'svelte';
	import { _ } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import TreeColumn, { type ColumnItem } from '$lib/components/pages/topology/TreeColumn.svelte';
	import EntityPanel from '$lib/components/pages/topology/EntityPanel.svelte';
	import CreateForm from '$lib/components/pages/topology/CreateForm.svelte';
	import SimplePageHeader from '$lib/components/shared/SimplePageHeader.svelte';
	import { auth } from '$lib/core/auth.svelte';
	import * as topology from '$lib/core/topology';
	import type {
		Airport,
		Checkpoint,
		Desk,
		EntityName,
		Level,
		Site,
		Terminal
	} from '$lib/core/topology';

	type Selection = Record<EntityName, string | null>;
	const order: EntityName[] = ['airport', 'terminal', 'level', 'checkpoint', 'desk'];
	const permissionEntity: Record<EntityName, string> = {
		airport: 'Airport',
		terminal: 'Terminal',
		level: 'Level',
		checkpoint: 'Checkpoint',
		desk: 'Desk'
	};

	let siteList = $state<Site[]>([]);
	let siteCode = $state('');
	let airports = $state<Airport[]>([]);
	let terminals = $state<Terminal[]>([]);
	let levels = $state<Level[]>([]);
	let checkpoints = $state<Checkpoint[]>([]);
	let desks = $state<Desk[]>([]);
	let totals = $state<Record<EntityName, number>>({
		airport: 0,
		terminal: 0,
		level: 0,
		checkpoint: 0,
		desk: 0
	});
	let selection = $state<Selection>({
		airport: null,
		terminal: null,
		level: null,
		checkpoint: null,
		desk: null
	});
	let focus = $state<EntityName | null>(null);
	let adding = $state<EntityName | 'range' | null>(null);

	/** Changes need the permission; airports are deployment-wide, so only an all-sites administrator changes them. */
	function can(entity: EntityName, action: 'Create' | 'Edit' | 'Delete'): boolean {
		return (
			auth.can(`${permissionEntity[entity]}.${action}`) &&
			(entity !== 'airport' || auth.user?.allSites === true)
		);
	}
	const writes = $derived(order.some((entity) => can(entity, 'Create') || can(entity, 'Edit')));

	const lists = $derived<Record<EntityName, { id: string }[]>>({
		airport: airports,
		terminal: terminals,
		level: levels,
		checkpoint: checkpoints,
		desk: desks
	});

	function kindLabel(kind: string): string {
		return $_(`topology.kinds.${kind}`, { default: kind });
	}

	const columns = $derived<Record<EntityName, ColumnItem[]>>({
		airport: airports.map((a) => ({
			id: a.id,
			code: a.iataCode,
			name: a.name,
			detail: a.icaoCode ?? undefined
		})),
		terminal: terminals.map((t) => ({ id: t.id, code: t.code, name: t.name, detail: t.siteCode })),
		level: levels.map((l) => ({
			id: l.id,
			code: l.code,
			name: l.name,
			detail: `${$_('topology.fields.floorNumber')} ${l.floorNumber}, ${l.widthMetres} x ${l.depthMetres} m`
		})),
		checkpoint: checkpoints.map((c) => ({
			id: c.id,
			code: c.code,
			name: c.name,
			detail: kindLabel(c.kind)
		})),
		desk: desks.map((d) => ({
			id: d.id,
			code: d.code,
			name: d.name ?? '',
			detail: [
				kindLabel(d.kind),
				d.laneCategories.join(' '),
				d.inService ? '' : $_('topology.outOfService')
			]
				.filter(Boolean)
				.join(', '),
			muted: !d.inService
		}))
	});

	function failed(reason: string): void {
		toast.error($_('topology.messages.failed', { values: { reason } }));
	}

	async function load(entity: EntityName): Promise<void> {
		const index = order.indexOf(entity);
		const parent = index > 0 ? selection[order[index - 1]] : null;
		if (index > 0 && !parent) return set(entity, [], 0);
		const site = siteCode;
		const result = await topology.search<{ id: string }>(entity, {
			parentId: parent ?? undefined,
			siteCode: entity === 'airport' ? undefined : site || undefined
		});
		// A newer choice (another parent or site) won while this answer was on its way: it belongs to nothing shown.
		// Airports are deployment-wide, so a site change does not make their answer stale.
		if (
			(index > 0 && selection[order[index - 1]] !== parent) ||
			(entity !== 'airport' && siteCode !== site)
		)
			return;
		if (result.hasErrors || !result.data) {
			toast.error(
				$_('topology.messages.loadFailed', { values: { reason: result.errorMessages[0] ?? '' } })
			);
			return set(entity, [], 0);
		}
		set(entity, result.data.data, result.data.totalCount);
	}

	function set(entity: EntityName, items: { id: string }[], total: number): void {
		totals[entity] = total;
		if (entity === 'airport') airports = items as Airport[];
		else if (entity === 'terminal') terminals = items as Terminal[];
		else if (entity === 'level') levels = items as Level[];
		else if (entity === 'checkpoint') checkpoints = items as Checkpoint[];
		else desks = items as Desk[];
	}

	/** Selects an item and clears and reloads everything below it. */
	async function select(entity: EntityName, id: string | null): Promise<void> {
		selection[entity] = id;
		focus = id ? entity : null;
		adding = null;
		const below = order.slice(order.indexOf(entity) + 1);
		for (const child of below) {
			selection[child] = null;
			set(child, [], 0);
		}
		if (id && below.length) await load(below[0]);
	}

	async function changeSite(): Promise<void> {
		selection = { ...selection, terminal: null, level: null, checkpoint: null, desk: null };
		focus = selection.airport ? 'airport' : null;
		for (const child of order.slice(2)) set(child, [], 0);
		await load('terminal');
	}

	/** After a create, update or delete: reload the entity's column, keeping or clearing its selection. */
	async function changed(entity: EntityName, kept: boolean): Promise<void> {
		if (!kept) await select(entity, null);
		await load(entity);
	}

	onMount(async () => {
		const result = await topology.sites();
		siteList = result.data ?? [];
		siteCode = siteList.find((s) => s.code === 'DMO')?.code ?? siteList[0]?.code ?? '';
		await load('airport');
	});

	const focused = $derived(
		focus ? (lists[focus].find((item) => item.id === selection[focus!]) ?? null) : null
	);
	const parentLabels = $derived<Record<EntityName, string | null>>({
		airport: null,
		terminal: selection.airport ? null : $_('topology.choose.terminal'),
		level: selection.terminal ? null : $_('topology.choose.level'),
		checkpoint: selection.level ? null : $_('topology.choose.checkpoint'),
		desk: selection.checkpoint ? null : $_('topology.choose.desk')
	});
	const checkpointKind = $derived(
		checkpoints.find((c) => c.id === selection.checkpoint)?.kind ?? 'Immigration'
	);
</script>

<svelte:head>
	<title>{$_('topology.title')} · {$_('app.title')}</title>
</svelte:head>

<SimplePageHeader
	icon={MapIcon}
	title={$_('topology.title')}
	description={$_('topology.description')}
>
	{#snippet actions()}
		<label for="topology-site" class="text-sm font-medium">{$_('topology.site')}</label>
		<select
			id="topology-site"
			bind:value={siteCode}
			onchange={changeSite}
			class="h-10 rounded-lg border border-input bg-card px-3 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			{#each siteList as site (site.code)}
				<option value={site.code}
					>{site.name && site.name !== site.code ? `${site.code}, ${site.name}` : site.code}</option
				>
			{/each}
		</select>
	{/snippet}
</SimplePageHeader>

{#if !writes}
	<p
		data-testid="topology-read-only"
		class="mb-4 rounded-lg border border-status-info-border bg-status-info px-4 py-3 text-sm text-status-info-foreground"
	>
		{$_('topology.readOnly')}
	</p>
{:else if !auth.user?.allSites}
	<p
		class="mb-4 rounded-lg border border-status-info-border bg-status-info px-4 py-3 text-sm text-status-info-foreground"
	>
		{$_('topology.deploymentWide')}
	</p>
{/if}

<div class="grid gap-3 md:grid-cols-2 xl:grid-cols-5">
	{#each order as entity (entity)}
		<TreeColumn
			id={entity}
			title={$_(`topology.columns.${entity}`)}
			items={columns[entity]}
			total={totals[entity]}
			selectedId={selection[entity]}
			waitingFor={parentLabels[entity]}
			canAdd={can(entity, 'Create') && (entity !== 'terminal' || !!siteCode)}
			adding={adding === entity || (entity === 'desk' && adding === 'range')}
			onAdd={() => (adding = adding === entity ? null : entity)}
			onSelect={(id) => select(entity, id)}
		>
			{#snippet form()}
				<CreateForm
					entity={adding === 'range' ? 'range' : entity}
					parentId={entity === 'airport' ? null : selection[order[order.indexOf(entity) - 1]]}
					{siteCode}
					{checkpointKind}
					canRange={entity === 'desk'}
					onRange={() => (adding = adding === 'range' ? 'desk' : 'range')}
					onCancel={() => (adding = null)}
					onCreated={async () => {
						adding = null;
						await load(entity);
					}}
					onFailed={failed}
				/>
			{/snippet}
		</TreeColumn>
	{/each}
</div>

{#if focus && focused}
	{#key `${focus}:${focused.id}`}
		<EntityPanel
			entity={focus}
			item={focused}
			canEdit={can(focus, 'Edit')}
			canDelete={can(focus, 'Delete')}
			onClose={() => (focus = null)}
			onSaved={() => changed(focus!, true)}
			onDeleted={() => changed(focus!, false)}
			onFailed={failed}
		/>
	{/key}
{/if}
