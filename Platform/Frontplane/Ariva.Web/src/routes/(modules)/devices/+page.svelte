<script lang="ts">
	import { Cpu, FileUp, Plus } from '@lucide/svelte';
	import { onDestroy, onMount } from 'svelte';
	import { _ } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import CoveragePlan from '$lib/components/pages/devices/CoveragePlan.svelte';
	import CredentialReveal from '$lib/components/pages/devices/CredentialReveal.svelte';
	import DevicePanel from '$lib/components/pages/devices/DevicePanel.svelte';
	import HealthAlarms from '$lib/components/pages/devices/HealthAlarms.svelte';
	import CsvImportDialog from '$lib/components/pages/imports/CsvImportDialog.svelte';
	import RegisterForm from '$lib/components/pages/devices/RegisterForm.svelte';
	import IllustrativeBanner from '$lib/components/shared/IllustrativeBanner.svelte';
	import SimplePageHeader from '$lib/components/shared/SimplePageHeader.svelte';
	import StatusBadge, { type StatusTone } from '$lib/components/shared/StatusBadge.svelte';
	import { auth } from '$lib/core/auth.svelte';
	import * as devicesApi from '$lib/core/devices';
	import type { Device, HealthOverview, Issued, Mapping } from '$lib/core/devices';
	import * as topology from '$lib/core/topology';
	import type { Level, Site } from '$lib/core/topology';
	import * as zones from '$lib/core/zones';
	import type { Zone } from '$lib/core/zones';
	import { cn } from '$lib/utils';

	let siteList = $state<Site[]>([]);
	let siteCode = $state('');
	let levels = $state<Level[]>([]);
	let levelId = $state('');
	let list = $state<Device[]>([]);
	let overview = $state<HealthOverview | null>(null);
	let profileZones = $state<Zone[]>([]);
	let profileLines = $state<zones.Line[]>([]);
	let mappings = $state<Mapping[]>([]);
	let selectedId = $state<string | null>(null);
	let registering = $state(false);
	/** The CSV import dialog is open (ARV-123b); it holds the new credentials itself and drops them when it closes. */
	let importing = $state(false);
	/** The credential of the last registration or rotation, shown once and dropped when the user is done. */
	let issued = $state<Issued | null>(null);
	let plan = $state<zones.PlanImage | null>(null);
	/** The site's floor plans by level (asked once per site). */
	let plans: Record<string, zones.FloorPlan> = {};
	let destroyed = false;
	/** Each site load's number: a load the site selector has moved on from drops its answers, whichever comes back last (ARV-241). */
	let generation = 0;
	/** The site's levels and zones are in: registering needs them. */
	let loaded = $state(false);

	const tone: Record<string, StatusTone> = {
		Online: 'success',
		Commissioning: 'info',
		Degraded: 'warning',
		Offline: 'danger',
		Retired: 'neutral'
	};
	const zoneTone: Record<string, StatusTone> = {
		Healthy: 'success',
		Degraded: 'warning',
		Unmonitored: 'neutral'
	};

	const level = $derived(levels.find((l) => l.id === levelId) ?? null);
	const selected = $derived(list.find((d) => d.id === selectedId) ?? null);
	const healthOf = $derived(new Map((overview?.devices ?? []).map((h) => [h.id, h])));
	/** The queue zone names of each level, by level id (built with Object.fromEntries: own properties only). */
	const queueZones = $derived.by(() => {
		const queues = profileZones.filter((z) => z.kind === 'Queue');
		return Object.fromEntries(
			[...new Set(queues.map((z) => z.levelId))].map((id) => [
				id,
				queues.filter((z) => z.levelId === id).map((z) => z.name)
			])
		);
	});
	/**
	 * The entry and exit line names of each queue zone, by zone name (ARV-259e4): the lines a device of the zone can declare
	 * it counts. Built with Object.fromEntries: own properties only.
	 */
	const zoneLines = $derived.by(() =>
		Object.fromEntries(
			profileZones
				.filter((z) => z.kind === 'Queue')
				.map((z) => [
					z.name,
					profileLines
						.filter((l) => l.zoneId === z.id && (l.role === 'Entry' || l.role === 'Exit'))
						.map((l) => l.name)
						.sort()
				])
		)
	);
	const planZones = $derived(
		profileZones
			.filter((z) => z.kind === 'Queue' && z.levelId === levelId)
			.map((z) => ({ id: z.id, name: z.name, points: zones.parsePolygon(z.polygon) ?? [] }))
	);
	const levelDevices = $derived(list.filter((d) => d.levelId === levelId && d.state !== 'Retired'));

	function failed(reason: string): void {
		toast.error($_('topology.messages.failed', { values: { reason } }));
	}

	function seen(id: string): string {
		const h = healthOf.get(id);
		return h?.secondsSinceSeen != null
			? $_('devices.secondsAgo', { values: { seconds: Math.round(h.secondsSinceSeen) } })
			: $_('devices.never');
	}

	function calibration(d: Device): string {
		if (!d.lastCalibratedOn) return $_('devices.notCalibrated');
		const date = new Date(d.lastCalibratedOn).toLocaleDateString();
		return d.lastCalibrationPassed
			? $_('devices.passed', { values: { date } })
			: $_('devices.failedOn', { values: { date } });
	}

	/** The site's devices and their health; by default for the site shown, as a refresh after a change. */
	async function loadDevices(mine = generation, code = siteCode): Promise<void> {
		const [found, health] = await Promise.all([devicesApi.search(code), devicesApi.health(code)]);
		if (mine !== generation) return;
		if (found.hasErrors) failed(found.errorMessages[0] ?? '');
		list = found.data?.data ?? [];
		overview = health.data ?? null;
	}

	/** The published version's zones (else the draft's): the queue zones devices are assigned to and drawn over. */
	async function loadZones(mine: number, code: string): Promise<void> {
		const history = await zones.history(code);
		if (mine !== generation) return;
		const pick =
			(history.data ?? []).find((p) => p.status === 'Published') ??
			(history.data ?? []).find((p) => p.status === 'Draft');
		const picked = pick ? (await zones.get(pick.id)).data : null;
		if (mine !== generation) return;
		profileZones = picked?.zones ?? [];
		profileLines = picked?.lines ?? [];
	}

	async function loadSite(): Promise<void> {
		const mine = ++generation;
		const code = siteCode;
		// Nothing of the site shown before stays on the screen, or answers for it, while this one loads.
		list = [];
		overview = null;
		profileZones = [];
		profileLines = [];
		levels = [];
		levelId = '';
		plans = {};
		dropPlan();
		selectedId = null;
		registering = false;
		loaded = false;
		// A credential belongs to the site it was issued at: switching sites drops it.
		issued = null;
		const sitePlans = await zones.floorPlans(code);
		if (mine !== generation) return;
		plans = sitePlans;
		const levelResult = await topology.search<Level>('level', { siteCode: code });
		if (mine !== generation) return;
		levels = levelResult.data?.data ?? [];
		await Promise.all([loadDevices(mine, code), loadZones(mine, code)]);
		if (mine !== generation) return;
		loaded = true;
		levelId =
			list[0]?.levelId ??
			levels.find((l) => profileZones.some((z) => z.levelId === l.id))?.id ??
			levels[0]?.id ??
			'';
		await loadPlan();
	}

	function dropPlan(): void {
		if (plan) URL.revokeObjectURL(plan.url);
		plan = null;
	}

	async function loadPlan(): Promise<void> {
		dropPlan();
		const target = levelId;
		const meta = target ? plans[target] : undefined;
		if (!meta) return;
		const image = await zones.planImage(meta, () => target === levelId && !destroyed);
		if (!image) return;
		dropPlan();
		plan = image;
	}

	onMount(async () => {
		const [result, mapped] = await Promise.all([topology.sites(), devicesApi.mappings()]);
		siteList = result.data ?? [];
		mappings = mapped.data ?? [];
		siteCode = siteList.find((s) => s.code === 'DMO')?.code ?? siteList[0]?.code ?? '';
		if (siteCode) await loadSite();
	});

	/** A page restored from the back/forward cache never shows a credential again. */
	function restored(event: PageTransitionEvent): void {
		if (event.persisted) issued = null;
	}

	onDestroy(() => {
		destroyed = true;
		dropPlan();
		issued = null;
	});

	/**
	 * A credential is shown only under the site it was issued at (ARV-241): one whose answer comes after another site was
	 * chosen is not shown there; the device's credential is then issued again from its own site.
	 */
	function reveal(next: Issued): boolean {
		if (next.device.siteCode === siteCode) {
			issued = next;
			return true;
		}
		toast.warning(
			$_('devices.messages.issuedElsewhere', {
				values: { code: next.device.code, site: next.device.siteCode }
			})
		);
		return false;
	}

	async function registered(next: Issued): Promise<void> {
		registering = false;
		if (!reveal(next)) return;
		toast.success($_('devices.messages.registered', { values: { code: next.device.code } }));
		const mine = generation;
		await loadDevices();
		if (mine !== generation) return;
		selectedId = next.device.id;
		levelId = next.device.levelId;
	}

	async function changed(device: Device | null): Promise<void> {
		// A panel action that answers after another site was chosen changes nothing there.
		if (device && device.siteCode !== siteCode) return;
		const mine = generation;
		await loadDevices();
		if (mine !== generation) return;
		if (device && device.levelId !== levelId) {
			levelId = device.levelId;
			await loadPlan();
		}
	}
</script>

<svelte:window onpageshow={restored} onpagehide={() => (issued = null)} />

<svelte:head>
	<title>{$_('devices.title')} · {$_('app.title')}</title>
</svelte:head>

<SimplePageHeader icon={Cpu} title={$_('devices.title')} description={$_('devices.description')}>
	{#snippet actions()}
		<label for="devices-site" class="text-sm font-medium">{$_('topology.site')}</label>
		<select
			id="devices-site"
			bind:value={siteCode}
			onchange={loadSite}
			class="h-10 rounded-lg border border-input bg-card px-3 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			{#each siteList as site (site.code)}
				<option value={site.code}
					>{site.name && site.name !== site.code ? `${site.code}, ${site.name}` : site.code}</option
				>
			{/each}
		</select>
		{#if auth.can('Device.Create')}
			<button
				type="button"
				data-testid="register-device"
				disabled={!loaded}
				onclick={() => ((registering = !registering), (selectedId = null))}
				class="inline-flex h-10 items-center gap-1 rounded-lg bg-button-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60 dark:text-white"
			>
				<Plus class="size-4" aria-hidden="true" />
				{$_('devices.register')}
			</button>
			<button
				type="button"
				data-testid="import-devices"
				disabled={!siteCode}
				onclick={() => ((importing = true), (registering = false))}
				class="inline-flex h-10 items-center gap-1 rounded-lg border bg-card px-4 text-sm font-medium whitespace-nowrap hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
			>
				<FileUp class="size-4" aria-hidden="true" />
				{$_('csvImport.devices.action')}
			</button>
		{/if}
	{/snippet}
</SimplePageHeader>

<IllustrativeBanner site={siteList.find((s) => s.code === siteCode)} />

{#if !auth.can('Device.Create') && !auth.can('Device.Edit')}
	<p
		data-testid="devices-read-only"
		class="mb-4 rounded-lg border border-status-info-border bg-status-info px-4 py-3 text-sm text-status-info-foreground"
	>
		{$_('devices.noRights')}
	</p>
{/if}

{#if issued}
	<div class="mb-4">
		<CredentialReveal
			code={issued.device.code}
			credential={issued.credential}
			onDone={() => (issued = null)}
		/>
	</div>
{/if}

{#if importing && siteCode}
	<CsvImportDialog
		kind="devices"
		{siteCode}
		onClose={() => (importing = false)}
		onCommitted={() => loadDevices()}
	/>
{/if}

{#if registering}
	<div class="mb-4 rounded-xl border bg-card p-5">
		<RegisterForm
			{levels}
			{queueZones}
			{mappings}
			onRegistered={registered}
			onCancel={() => (registering = false)}
			onFailed={failed}
		/>
	</div>
{/if}

<div class="grid gap-4 xl:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]">
	<section class="min-w-0 rounded-xl border bg-card" aria-labelledby="devices-list-title">
		<h2 id="devices-list-title" class="border-b px-5 py-3 text-base font-semibold">
			{$_('devices.list')}
		</h2>
		{#if list.length === 0}
			<p class="p-5 text-sm text-muted-foreground">{$_('devices.none')}</p>
		{:else}
			<div class="overflow-x-auto">
				<table class="w-full text-sm" data-testid="devices-table">
					<thead class="bg-surface-2 text-xs font-semibold tracking-wide text-tertiary uppercase">
						<tr>
							<th scope="col" class="px-4 py-2.5 text-start">{$_('devices.columns.code')}</th>
							<th scope="col" class="px-4 py-2.5 text-start">{$_('devices.columns.zone')}</th>
							<th scope="col" class="px-4 py-2.5 text-start">{$_('devices.columns.state')}</th>
							<th scope="col" class="px-4 py-2.5 text-start">{$_('devices.columns.lastSeen')}</th>
							<th scope="col" class="px-4 py-2.5 text-start">{$_('devices.columns.calibration')}</th
							>
						</tr>
					</thead>
					<tbody>
						{#each list as device (device.id)}
							<tr
								data-testid="device-row"
								class={cn(
									'odd:bg-surface even:bg-surface-2/50 hover:bg-muted',
									selectedId === device.id && 'bg-primary/10'
								)}
							>
								<th scope="row" class="px-4 py-2 text-start font-normal">
									<button
										type="button"
										aria-current={selectedId === device.id ? 'true' : undefined}
										onclick={() => (
											(selectedId = device.id),
											(registering = false),
											device.levelId !== levelId && ((levelId = device.levelId), loadPlan())
										)}
										class="font-mono font-medium text-primary underline-offset-4 hover:underline focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none dark:text-foreground"
									>
										{device.code}
									</button>
									<span class="block text-xs text-muted-foreground"
										>{$_(`devices.families.${device.family}`, { default: device.family })}</span
									>
								</th>
								<td class="px-4 py-2"
									>{device.queueZoneName}{#if device.linesNotInZone?.length}
										<span
											class="block text-xs text-status-warning-foreground"
											data-testid="lines-not-in-zone"
											>{$_('devices.coverage.notInZone', {
												values: { lines: device.linesNotInZone.join(', ') }
											})}</span
										>
									{/if}</td
								>
								<td class="px-4 py-2"
									><StatusBadge
										tone={tone[device.state] ?? 'neutral'}
										label={$_(`devices.states.${device.state}`)}
									/></td
								>
								<td class="px-4 py-2 tabular-nums">{seen(device.id)}</td>
								<td class="px-4 py-2">{calibration(device)}</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		{/if}
		{#if overview && overview.zones.length}
			<div class="border-t px-5 py-3">
				<h3 class="mb-2 text-sm font-semibold">{$_('devices.zonesHealth')}</h3>
				<ul class="flex flex-wrap gap-2" data-testid="zone-health">
					{#each overview.zones as zone (zone.queueZoneName)}
						<li class="flex items-center gap-1.5 text-xs">
							<span>{zone.queueZoneName}</span>
							<StatusBadge
								tone={zoneTone[zone.state] ?? 'neutral'}
								label={$_(`devices.zoneStates.${zone.state}`, { default: zone.state })}
							/>
						</li>
					{/each}
				</ul>
				<p class="mt-2 text-xs text-muted-foreground">
					{$_('devices.heartbeat', { values: { seconds: overview.heartbeatTimeoutSeconds } })}
				</p>
			</div>
		{/if}
		{#if overview?.uncoveredLines?.length}
			<div class="border-t px-5 py-3">
				<h3 class="mb-2 text-sm font-semibold">{$_('devices.coverage.uncovered')}</h3>
				<ul class="flex flex-col gap-1 text-xs" data-testid="uncovered-lines">
					{#each overview.uncoveredLines as zone (zone.queueZoneName)}
						<li>
							{$_('devices.coverage.uncoveredRow', {
								values: { zone: zone.queueZoneName, lines: zone.lines.join(', ') }
							})}
						</li>
					{/each}
				</ul>
			</div>
		{/if}
	</section>

	<section
		class="flex min-w-0 flex-col gap-2 rounded-xl border bg-card p-4"
		aria-labelledby="coverage-title"
	>
		<div class="flex flex-wrap items-center justify-between gap-2">
			<h2 id="coverage-title" class="text-base font-semibold">{$_('devices.plan')}</h2>
			<label class="flex items-center gap-2 text-xs font-medium">
				{$_('devices.fields.level')}
				<select
					bind:value={levelId}
					onchange={loadPlan}
					class="h-9 rounded-md border border-input bg-card px-2 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
				>
					{#each levels as item (item.id)}
						<option value={item.id}>{item.code}, {item.name}</option>
					{/each}
				</select>
			</label>
		</div>
		{#if level}
			<CoveragePlan
				width={level.widthMetres}
				depth={level.depthMetres}
				zones={planZones}
				devices={levelDevices}
				{selectedId}
				{plan}
				onSelect={(id) => ((selectedId = id), (registering = false))}
			/>
		{/if}
	</section>
</div>

<!-- ARV-120: the open alarms of the continuous health checks per queue zone (the alerts the caller's role is responsible for). -->
{#if siteCode && auth.can('Alert.Search')}
	<div class="mt-4">
		<HealthAlarms {siteCode} />
	</div>
{/if}

{#if selected}
	<div class="mt-4">
		{#key selected.id}
			<DevicePanel
				device={selected}
				health={healthOf.get(selected.id) ?? null}
				{levels}
				{queueZones}
				{zoneLines}
				{mappings}
				onChanged={changed}
				onIssued={reveal}
				onClose={() => (selectedId = null)}
				onFailed={failed}
			/>
		{/key}
	</div>
{/if}
