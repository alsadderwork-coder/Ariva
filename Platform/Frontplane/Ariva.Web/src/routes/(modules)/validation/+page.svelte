<script lang="ts">
	import { BadgeCheck } from '@lucide/svelte';
	import { onDestroy, onMount } from 'svelte';
	import { goto } from '$app/navigation';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { _ } from 'svelte-i18n';
	import CampaignDetail, {
		type CampaignView
	} from '$lib/components/pages/validation/CampaignDetail.svelte';
	import CampaignList from '$lib/components/pages/validation/CampaignList.svelte';
	import IllustrativeBanner from '$lib/components/shared/IllustrativeBanner.svelte';
	import SimplePageHeader from '$lib/components/shared/SimplePageHeader.svelte';
	import { auth } from '$lib/core/auth.svelte';
	import * as topology from '$lib/core/topology';
	import type { Site } from '$lib/core/topology';
	import { isCampaignId, isSiteCode } from '$lib/core/validation';

	// Validation campaigns (ARV-104h, wiki 07 section 8, wiki 12): the campaign list of a site, planning a campaign, its
	// progress, every observer's counts, start and close (a critical action: the step-up dialog), and its results. Reading
	// needs Validation.View; planning, starting and closing Validation.Manage. The server decides every call and projects
	// what each role reads (desks and desk-state results only to border roles). Which site, campaign and view are shown is
	// in the address (?site=&campaign=&view=), checked against the site list and a GUID before any request is made.

	const views: readonly CampaignView[] = ['progress', 'counts', 'results'];

	const canView = $derived(auth.can('Validation.View'));
	const canManage = $derived(auth.can('Validation.Manage'));
	const seesDesks = $derived(auth.can('BorderDesks.View'));

	let siteList = $state<Site[]>([]);
	let ready = $state(false);
	let destroyed = false;

	const siteParam = $derived(page.url.searchParams.get('site'));
	const fallbackSite = $derived(
		siteList.find((s) => s.code === 'DMO')?.code ?? siteList[0]?.code ?? ''
	);
	const siteCode = $derived(
		isSiteCode(siteParam) && siteList.some((s) => s.code === siteParam) ? siteParam : fallbackSite
	);
	const campaignParam = $derived(page.url.searchParams.get('campaign'));
	const campaignId = $derived(isCampaignId(campaignParam) ? campaignParam.toLowerCase() : null);
	const viewParam = $derived(page.url.searchParams.get('view'));
	const view = $derived<CampaignView>(views.find((v) => v === viewParam) ?? 'progress');

	/**
	 * This screen's address for a site, a campaign and a view. Only checked values reach it: a site code, a campaign GUID
	 * (from the server's answers too, never trusted as they come) and a view of the allowlist.
	 */
	function address(site: string, campaign?: string | null, next?: CampaignView): string {
		const query = new URLSearchParams();
		if (isSiteCode(site)) query.set('site', site);
		if (isCampaignId(campaign)) {
			query.set('campaign', campaign);
			if (next && next !== 'progress' && views.includes(next)) query.set('view', next);
		}
		return `${resolve('/validation')}?${query}`;
	}

	function changeSite(event: Event): void {
		const code = (event.currentTarget as HTMLSelectElement).value;
		if (isSiteCode(code)) void goto(address(code));
	}

	function showView(next: CampaignView): void {
		if (!campaignId) return;
		void goto(address(siteCode, campaignId, next), {
			replaceState: true,
			noScroll: true,
			keepFocus: true
		});
	}

	onMount(async () => {
		// Without Validation.View the screen says so and asks the server for nothing.
		if (!canView) return;
		if (auth.can('Site.Search')) {
			const result = await topology.sites();
			if (destroyed) return;
			siteList = result.data ?? [];
		} else {
			siteList = (auth.user?.sites ?? []).map((code) => ({ code, name: code }));
		}
		ready = true;
	});

	onDestroy(() => {
		destroyed = true;
	});
</script>

<svelte:head>
	<title>{$_('validationCampaigns.title')} · {$_('app.title')}</title>
</svelte:head>

<SimplePageHeader
	icon={BadgeCheck}
	title={$_('validationCampaigns.title')}
	description={$_('validationCampaigns.description')}
>
	{#snippet actions()}
		{#if canView && siteList.length > 0}
			<label for="campaigns-site" class="sr-only">{$_('validationCampaigns.site')}</label>
			<select
				id="campaigns-site"
				data-testid="campaigns-site"
				value={siteCode}
				onchange={changeSite}
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
{:else if !ready}
	<p class="text-sm text-muted-foreground" role="status">{$_('validationCampaigns.loading')}</p>
{:else if !siteCode}
	<p class="text-sm text-muted-foreground" data-testid="campaigns-no-sites">
		{$_('validationCampaigns.noSites')}
	</p>
{:else if campaignId}
	{#key `${siteCode}/${campaignId}`}
		<CampaignDetail
			{siteCode}
			{campaignId}
			{view}
			{canManage}
			listHref={address(siteCode)}
			onView={showView}
		/>
	{/key}
{:else}
	{#key siteCode}
		<CampaignList
			{siteCode}
			{canManage}
			{seesDesks}
			hrefOf={(id) => address(siteCode, id)}
			onCreated={(id) => {
				if (isCampaignId(id)) void goto(address(siteCode, id));
			}}
		/>
	{/key}
{/if}
