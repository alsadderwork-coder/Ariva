<script lang="ts">
	import { KeyRound, MonitorPlay, Pencil, Plus } from '@lucide/svelte';
	import { onDestroy, onMount } from 'svelte';
	import { _ } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import CredentialReveal from '$lib/components/pages/devices/CredentialReveal.svelte';
	import AdvertisingLibrary from '$lib/components/pages/displays/AdvertisingLibrary.svelte';
	import AdvertisingPanel from '$lib/components/pages/displays/AdvertisingPanel.svelte';
	import DisplayForm from '$lib/components/pages/displays/DisplayForm.svelte';
	import ConfirmButton from '$lib/components/shared/ConfirmButton.svelte';
	import IllustrativeBanner from '$lib/components/shared/IllustrativeBanner.svelte';
	import SimplePageHeader from '$lib/components/shared/SimplePageHeader.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import * as advertisingApi from '$lib/core/advertising';
	import type { AdvertisingPolicy } from '$lib/core/advertising';
	import { auth } from '$lib/core/auth.svelte';
	import * as displays from '$lib/core/displays';
	import type { Display, DisplayRequest } from '$lib/core/displays';
	import * as topology from '$lib/core/topology';
	import type { Site } from '$lib/core/topology';
	import * as zonesApi from '$lib/core/zones';

	let siteList = $state<Site[]>([]);
	let siteCode = $state('');
	let list = $state<Display[]>([]);
	let zoneNames = $state<string[]>([]);
	/** ARV-145: the site's advertising policy, for the roles that see it (off when it cannot be read). */
	let advertising = $state<AdvertisingPolicy | null>(null);
	let loading = $state(true);
	let form = $state<{ key: number; editing: Display | null } | null>(null);
	let formKey = 0;
	/** The player's address with its credential, shown once after a creation or a new credential, then dropped. */
	let issued = $state<{ code: string; address: string } | null>(null);
	/** The display whose new credential is being confirmed on the page. */
	let renewing = $state<string | null>(null);
	/** Each site load's number: a load the site selector has moved on from drops its answers, whichever comes back last. */
	let generation = 0;

	const canCreate = $derived(auth.can('Display.Create'));
	const canEdit = $derived(auth.can('Display.Edit'));
	const canDelete = $derived(auth.can('Display.Delete'));
	const canViewAdvertising = $derived(auth.can('AdvertisingPolicy.View'));
	const canEditAdvertising = $derived(auth.can('AdvertisingPolicy.Edit'));
	/** ARV-142a: the site's advertisement library, while its policy allows advertising. */
	const canViewMedia = $derived(auth.can('Advertising.View') || auth.can('Advertising.Manage'));
	const canManageMedia = $derived(auth.can('Advertising.Manage'));
	const canApproveMedia = $derived(auth.can('Advertising.Approve'));

	function failed(reason: string): void {
		toast.error($_('displays.messages.failed', { values: { reason } }));
	}

	async function loadSite(): Promise<void> {
		const mine = ++generation;
		const code = siteCode;
		form = null;
		issued = null;
		loading = true;
		const [found, profiles, policy] = await Promise.all([
			displays.search(code),
			zonesApi.history(code),
			canViewAdvertising ? advertisingApi.get(code) : Promise.resolve(null)
		]);
		if (mine !== generation) return;
		if (found.hasErrors) failed(found.errorMessages.join(' '));
		list = found.data ?? [];
		advertising = policy?.data ?? null;
		const published = (profiles.data ?? []).find((p) => p.status === 'Published');
		const zones = published ? ((await zonesApi.get(published.id)).data?.zones ?? []) : [];
		if (mine !== generation) return;
		zoneNames = zones
			.filter((z) => z.kind === 'Queue')
			.map((z) => z.name)
			.sort((a, b) => a.localeCompare(b));
		loading = false;
	}

	async function submit(request: DisplayRequest): Promise<string[]> {
		const editing = form?.editing;
		if (editing) {
			const result = await displays.update(editing.id, request);
			if (result.hasErrors || !result.data) return result.errorMessages;
			toast.success($_('displays.messages.saved', { values: { code: result.data.code } }));
		} else {
			const result = await displays.create(request);
			if (result.hasErrors || !result.data) return result.errorMessages;
			issued = {
				code: result.data.display.code,
				address: displays.playerUrl(result.data.display.code, result.data.credential)
			};
		}
		form = null;
		await refresh();
		return [];
	}

	async function refresh(): Promise<void> {
		const mine = generation;
		const code = siteCode;
		const [found, policy] = await Promise.all([
			displays.search(code),
			canViewAdvertising ? advertisingApi.get(code) : Promise.resolve(null)
		]);
		if (mine !== generation) return;
		list = found.data ?? list;
		if (policy?.data) advertising = policy.data;
	}

	async function renew(display: Display): Promise<void> {
		const result = await displays.newCredential(display.id);
		if (result.hasErrors || !result.data) return failed(result.errorMessages.join(' '));
		issued = {
			code: display.code,
			address: displays.playerUrl(display.code, result.data.credential)
		};
		await refresh();
	}

	async function remove(display: Display): Promise<void> {
		const result = await displays.remove(display.id);
		if (result.hasErrors) return failed(result.errorMessages.join(' '));
		toast.success($_('displays.messages.deleted', { values: { code: display.code } }));
		await refresh();
	}

	function dropIssued(): void {
		issued = null;
	}

	onMount(async () => {
		const result = await topology.sites();
		siteList = result.data ?? [];
		siteCode = siteList.find((s) => s.code === 'DMO')?.code ?? siteList[0]?.code ?? '';
		if (siteCode) await loadSite();
		else loading = false;
		// The credential never outlives the page.
		window.addEventListener('pagehide', dropIssued);
	});

	onDestroy(() => {
		issued = null;
		if (typeof window !== 'undefined') window.removeEventListener('pagehide', dropIssued);
	});
</script>

<svelte:head>
	<title>{$_('displays.title')} · {$_('app.title')}</title>
</svelte:head>

<SimplePageHeader
	icon={MonitorPlay}
	title={$_('displays.title')}
	description={$_('displays.description')}
>
	{#snippet actions()}
		<label for="displays-site" class="sr-only">{$_('topology.site')}</label>
		<select
			id="displays-site"
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
		{#if canCreate}
			<button
				type="button"
				data-testid="add-display"
				disabled={loading || !siteCode}
				onclick={() => {
					issued = null;
					form = { key: ++formKey, editing: null };
				}}
				class="inline-flex h-10 items-center gap-2 rounded-lg bg-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
			>
				<Plus class="size-4" aria-hidden="true" />
				{$_('displays.actions.add')}
			</button>
		{/if}
	{/snippet}
</SimplePageHeader>

<IllustrativeBanner site={siteList.find((s) => s.code === siteCode)} />

<div class="flex flex-col gap-4">
	{#if issued}
		<div class="flex flex-col gap-2">
			<!-- The player's address carries the credential (in the fragment, never sent to a server): shown once. -->
			<CredentialReveal code={issued.code} credential={issued.address} onDone={dropIssued} />
			<p class="text-xs text-muted-foreground">{$_('displays.playerHint')}</p>
		</div>
	{/if}

	{#if form}
		{#key form.key}
			<DisplayForm
				{siteCode}
				editing={form.editing}
				{zoneNames}
				{advertising}
				onSubmit={submit}
				onCancel={() => (form = null)}
			/>
		{/key}
	{/if}

	<section class="overflow-hidden rounded-xl border bg-card" aria-labelledby="displays-title">
		<div class="border-b px-4 py-3">
			<h2 id="displays-title" class="text-base font-semibold">{$_('displays.listTitle')}</h2>
		</div>
		{#if !loading && list.length === 0}
			<p class="px-4 py-6 text-sm text-muted-foreground">{$_('displays.none')}</p>
		{:else}
			<div class="overflow-x-auto">
				<table class="w-full text-sm" data-testid="displays-table">
					<thead class="bg-muted/50 text-xs text-muted-foreground">
						<tr>
							<th class="px-4 py-2 text-start font-medium">{$_('displays.columns.code')}</th>
							<th class="px-4 py-2 text-start font-medium">{$_('displays.columns.name')}</th>
							<th class="px-4 py-2 text-start font-medium">{$_('displays.columns.languages')}</th>
							<th class="px-4 py-2 text-start font-medium">{$_('displays.columns.entries')}</th>
							<th class="px-4 py-2 text-start font-medium">{$_('displays.columns.band')}</th>
							<th class="px-4 py-2 text-start font-medium">{$_('displays.columns.credential')}</th>
							<th class="px-4 py-2 text-start font-medium">{$_('displays.columns.status')}</th>
							<th class="px-4 py-2 text-end font-medium"
								><span class="sr-only">{$_('displays.columns.actions')}</span></th
							>
						</tr>
					</thead>
					<tbody>
						{#each list as display (display.id)}
							<tr class="border-t align-top" data-testid="display-row" data-code={display.code}>
								<td class="px-4 py-2 font-mono text-xs">{display.code}</td>
								<td class="px-4 py-2">
									<div>{display.name}</div>
									{#if display.location}<div class="text-xs text-muted-foreground">
											{display.location}
										</div>{/if}
								</td>
								<td class="px-4 py-2 text-xs">{display.languages.join(', ')}</td>
								<td class="px-4 py-2 text-xs">{display.entries.map((e) => e.zone).join(', ')}</td>
								<td class="px-4 py-2 text-xs tabular-nums"
									>{$_('displays.bandSummary', {
										values: {
											band: display.bandMinutes,
											hysteresis: display.hysteresisMinutes,
											stale: display.staleSeconds
										}
									})}
									<div class="text-muted-foreground" data-testid="display-layout-summary">
										{display.layout === 'Split'
											? $_('displays.layoutSummary.Split', {
													values: { share: display.queueShare }
												})
											: $_('displays.layoutSummary.Full')}
									</div>
									{#if display.layout === 'Split' && display.secondRegion === 'Advertising'}
										<div class="text-muted-foreground" data-testid="display-advertising">
											{advertising?.displaysFallingBack.includes(display.code)
												? $_('displays.advertisingFallsBack')
												: $_('displays.advertisingShown')}
										</div>
										<div
											class="text-muted-foreground"
											data-testid="display-advertising-pause-summary"
										>
											{display.advertisingPauseMinutes == null
												? $_('displays.advertisingPauseNever')
												: $_('displays.advertisingPauseSummary', {
														values: { minutes: display.advertisingPauseMinutes }
													})}
										</div>
									{/if}</td
								>
								<td class="px-4 py-2 font-mono text-xs">{display.credentialPrefix ?? ''}…</td>
								<td class="px-4 py-2">
									<StatusBadge
										tone={display.enabled ? 'success' : 'neutral'}
										label={display.enabled ? $_('displays.enabled') : $_('displays.disabled')}
									/>
								</td>
								<td class="px-4 py-2">
									<span class="flex flex-wrap justify-end gap-1.5">
										{#if canEdit}
											<button
												type="button"
												data-testid="edit-display"
												onclick={() => {
													issued = null;
													form = { key: ++formKey, editing: display };
												}}
												class="inline-flex h-9 items-center gap-1.5 rounded-md border bg-card px-2.5 text-sm hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
											>
												<Pencil class="size-4" aria-hidden="true" />
												{$_('displays.actions.edit')}
											</button>
											{#if renewing === display.id}
												<button
													type="button"
													data-testid="confirm-renew-display"
													onclick={async () => {
														renewing = null;
														await renew(display);
													}}
													class="inline-flex h-9 items-center gap-1.5 rounded-md border border-status-warning-border bg-status-warning px-2.5 text-sm font-medium text-status-warning-foreground focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
												>
													<KeyRound class="size-4" aria-hidden="true" />
													{$_('displays.actions.confirmNewCredential')}
												</button>
												<button
													type="button"
													onclick={() => (renewing = null)}
													class="inline-flex h-9 items-center rounded-md border bg-card px-2.5 text-sm hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
												>
													{$_('displays.actions.cancel')}
												</button>
											{:else}
												<button
													type="button"
													data-testid="renew-display"
													onclick={() => (renewing = display.id)}
													class="inline-flex h-9 items-center gap-1.5 rounded-md border bg-card px-2.5 text-sm hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
												>
													<KeyRound class="size-4" aria-hidden="true" />
													{$_('displays.actions.newCredential')}
												</button>
											{/if}
										{/if}
										{#if canDelete}
											<ConfirmButton
												label={$_('displays.actions.delete')}
												confirmLabel={$_('displays.actions.confirmDelete')}
												testId="delete-display"
												onConfirm={() => remove(display)}
											/>
										{/if}
									</span>
								</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		{/if}
	</section>
	{#if advertising && siteCode}
		<AdvertisingPanel
			{siteCode}
			policy={advertising}
			canEdit={canEditAdvertising}
			onChanged={async (policy) => {
				advertising = policy;
				await refresh();
			}}
		/>
		{#if advertising.allowed && canViewMedia}
			<AdvertisingLibrary {siteCode} canManage={canManageMedia} canApprove={canApproveMedia} />
		{/if}
	{/if}
	<p class="flex items-center gap-1.5 text-xs text-muted-foreground">
		<KeyRound class="size-3.5" aria-hidden="true" />
		{$_('displays.credentialNote')}
	</p>
</div>
