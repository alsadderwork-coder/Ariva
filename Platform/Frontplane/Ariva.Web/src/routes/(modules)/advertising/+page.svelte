<script lang="ts">
	import { Megaphone, TriangleAlert } from '@lucide/svelte';
	import { onDestroy, onMount } from 'svelte';
	import { _ } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import PlaylistsPanel from '$lib/components/pages/advertising/PlaylistsPanel.svelte';
	import SchedulesPanel from '$lib/components/pages/advertising/SchedulesPanel.svelte';
	import TimelinePanel from '$lib/components/pages/advertising/TimelinePanel.svelte';
	import IllustrativeBanner from '$lib/components/shared/IllustrativeBanner.svelte';
	import SimplePageHeader from '$lib/components/shared/SimplePageHeader.svelte';
	import * as advertisingApi from '$lib/core/advertising';
	import type { AdvertisingPolicy } from '$lib/core/advertising';
	import * as media from '$lib/core/advertising-media';
	import type { AdvertisingMedia } from '$lib/core/advertising-media';
	import * as plans from '$lib/core/advertising-plans';
	import type { Playlist, Schedule } from '$lib/core/advertising-plans';
	import { auth } from '$lib/core/auth.svelte';
	import * as displaysApi from '$lib/core/displays';
	import type { Display } from '$lib/core/displays';
	import * as slidesApi from '$lib/core/slides';
	import type { Slide } from '$lib/core/slides';
	import * as topology from '$lib/core/topology';
	import type { Site } from '$lib/core/topology';

	/**
	 * Advertising (ARV-143a): a site's playlists, the schedules that play them on its displays, and one display's week. For
	 * the holders of Advertising.View or Advertising.Manage; only Advertising.Manage changes anything. While the site's
	 * advertising policy does not allow advertising (the policy says so, or an advertising call answers 403) the page shows
	 * a note and asks for nothing more. The server decides every read and every change; every word is shown as text.
	 */
	type View = 'loading' | 'none' | 'off' | 'ready';

	let siteList = $state<Site[]>([]);
	let siteCode = $state('');
	let view = $state<View>('loading');
	let policy = $state<AdvertisingPolicy | null>(null);
	let playlists = $state<Playlist[]>([]);
	let schedules = $state<Schedule[]>([]);
	let library = $state<AdvertisingMedia[]>([]);
	let slides = $state<Slide[]>([]);
	let boards = $state<Display[]>([]);
	/** Grows with every change of a playlist or a schedule, so the timeline is read again. */
	let version = $state(0);
	let generation = 0;

	const canView = $derived(auth.can('Advertising.View') || auth.can('Advertising.Manage'));
	const canManage = $derived(auth.can('Advertising.Manage'));
	const canReadPolicy = $derived(auth.can('AdvertisingPolicy.View'));
	const canReadSlides = $derived(auth.can('DisplayContent.View'));
	const canListDisplays = $derived(auth.can('Display.Search'));
	const zoneNames = $derived(
		[...(policy?.publishedQueueZones ?? [])].sort((a, b) => a.localeCompare(b))
	);
	const displayCodes = $derived(boards.map((d) => d.code));

	function failed(reason: string): void {
		toast.error($_('advertisingPlans.messages.loadFailed', { values: { reason } }));
	}

	function clear(): void {
		policy = null;
		playlists = [];
		schedules = [];
		library = [];
		slides = [];
		boards = [];
	}

	async function loadSite(): Promise<void> {
		const mine = ++generation;
		const code = siteCode;
		view = 'loading';
		clear();
		if (!code) {
			view = 'none';
			return;
		}
		// The policy first, where the role may read it: while it is off, nothing else is asked for.
		if (canReadPolicy) {
			const read = await advertisingApi.get(code);
			if (mine !== generation) return;
			if (read.status === 403 || (read.data && !read.data.allowed)) {
				view = 'off';
				return;
			}
			policy = read.data;
		}
		const [foundPlaylists, foundSchedules] = await Promise.all([
			plans.listPlaylists(code),
			plans.listSchedules(code)
		]);
		if (mine !== generation) return;
		if (foundPlaylists.status === 403 || foundSchedules.status === 403) {
			view = 'off';
			return;
		}
		if (foundPlaylists.hasErrors || foundSchedules.hasErrors)
			failed([...foundPlaylists.errorMessages, ...foundSchedules.errorMessages].join(' '));
		playlists = foundPlaylists.data ?? [];
		schedules = foundSchedules.data ?? [];
		// What the forms offer (the library and the slides, for managers) and the displays the timeline shows.
		const [foundMedia, foundSlides, foundDisplays] = await Promise.all([
			canManage ? media.list(code) : Promise.resolve(null),
			canManage && canReadSlides ? slidesApi.search(code) : Promise.resolve(null),
			canListDisplays ? displaysApi.search(code) : Promise.resolve(null)
		]);
		if (mine !== generation) return;
		if (foundMedia?.status === 403) {
			view = 'off';
			return;
		}
		library = foundMedia?.data ?? [];
		slides = foundSlides?.data ?? [];
		boards = [...(foundDisplays?.data ?? [])].sort((a, b) => a.code.localeCompare(b.code));
		view = 'ready';
	}

	/** Reads the playlists, the schedules and the library again after a change, and the timeline with them. */
	async function refresh(): Promise<void> {
		const mine = generation;
		const code = siteCode;
		const [foundPlaylists, foundSchedules, foundMedia] = await Promise.all([
			plans.listPlaylists(code),
			plans.listSchedules(code),
			canManage ? media.list(code) : Promise.resolve(null)
		]);
		if (mine !== generation) return;
		if (foundPlaylists.status === 403 || foundSchedules.status === 403) {
			view = 'off';
			return;
		}
		playlists = foundPlaylists.data ?? playlists;
		schedules = foundSchedules.data ?? schedules;
		if (foundMedia?.data) library = foundMedia.data;
		version++;
	}

	/** A change answered 403: the policy was turned off meanwhile; read the site again from its policy. */
	function refused(): void {
		void loadSite();
	}

	onMount(async () => {
		// A role without the permission sees the notice and the page asks the API for nothing.
		if (!canView) return;
		const result = await topology.sites();
		siteList = result.data ?? [];
		siteCode = siteList.find((s) => s.code === 'DMO')?.code ?? siteList[0]?.code ?? '';
		await loadSite();
	});

	onDestroy(() => {
		generation++;
	});
</script>

<svelte:head>
	<title>{$_('advertisingPlans.title')} · {$_('app.title')}</title>
</svelte:head>

<SimplePageHeader
	icon={Megaphone}
	title={$_('advertisingPlans.title')}
	description={$_('advertisingPlans.description')}
>
	{#snippet actions()}
		{#if canView}
			<label for="advertising-site" class="sr-only">{$_('topology.site')}</label>
			<select
				id="advertising-site"
				data-testid="plans-site"
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
		{/if}
	{/snippet}
</SimplePageHeader>

<IllustrativeBanner site={siteList.find((s) => s.code === siteCode)} />

{#if !canView}
	<section class="rounded-xl border bg-card p-6" data-testid="no-access">
		<h2 class="text-base font-semibold">{$_('access.title')}</h2>
		<p class="mt-1 text-sm text-muted-foreground">{$_('access.description')}</p>
	</section>
{:else}
	<div class="flex flex-col gap-4" data-testid="plans-page" data-state={view} data-site={siteCode}>
		{#if view === 'loading'}
			<p class="text-sm text-muted-foreground" role="status" data-testid="plans-loading">
				{$_('advertisingPlans.loading')}
			</p>
		{:else if view === 'none'}
			<p class="text-sm text-muted-foreground" data-testid="plans-no-site">
				{$_('advertisingPlans.noSite')}
			</p>
		{:else if view === 'off'}
			<p
				class="rounded-xl border bg-card px-5 py-4 text-sm text-secondary-foreground"
				role="note"
				data-testid="plans-off"
			>
				{$_('advertisingPlans.off')}
			</p>
		{:else}
			{#if policy?.profileChanged}
				<p
					class="flex items-start gap-2 rounded-xl border border-status-warning-border bg-status-warning px-4 py-3 text-sm text-status-warning-foreground"
					role="note"
					data-testid="plans-profile-changed"
				>
					<TriangleAlert class="mt-0.5 size-4 shrink-0" aria-hidden="true" />
					{$_('advertisingPlans.profileChanged')}
				</p>
			{/if}
			<PlaylistsPanel
				{siteCode}
				{playlists}
				{library}
				{slides}
				slidesReadable={canReadSlides}
				{canManage}
				onChanged={refresh}
				onRefused={refused}
			/>
			<SchedulesPanel
				{siteCode}
				{schedules}
				{playlists}
				{zoneNames}
				{displayCodes}
				{canManage}
				onChanged={refresh}
				onRefused={refused}
			/>
			<TimelinePanel
				{siteCode}
				displays={boards}
				displaysReadable={canListDisplays}
				fallingBack={policy?.displaysFallingBack ?? []}
				{version}
				onRefused={refused}
			/>
		{/if}
	</div>
{/if}
