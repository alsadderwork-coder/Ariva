<script lang="ts">
	import { ArrowDown, ArrowUp, Pencil, Plus, Presentation } from '@lucide/svelte';
	import { onMount } from 'svelte';
	import { _ } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import SlideForm from '$lib/components/pages/displays/SlideForm.svelte';
	import SlideIcon from '$lib/components/pages/displays/SlideIcon.svelte';
	import IllustrativeBanner from '$lib/components/shared/IllustrativeBanner.svelte';
	import SimplePageHeader from '$lib/components/shared/SimplePageHeader.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { auth } from '$lib/core/auth.svelte';
	import * as displays from '$lib/core/displays';
	import * as slides from '$lib/core/slides';
	import type { Slide, SlideRequest } from '$lib/core/slides';
	import * as topology from '$lib/core/topology';
	import type { Site } from '$lib/core/topology';
	import * as zonesApi from '$lib/core/zones';

	/**
	 * Instruction slides (ARV-141): what the passenger displays of a site say beside the waits. A list in the order the
	 * displays rotate them, each with where it shows; DisplayContent.Manage adds, changes, orders, retires and restores them.
	 * Every word is shown as text.
	 */
	const rtl = new Set(['ar']);
	let siteList = $state<Site[]>([]);
	let siteCode = $state('');
	let list = $state<Slide[]>([]);
	let zoneNames = $state<string[]>([]);
	let displayCodes = $state<string[]>([]);
	let loading = $state(true);
	let busy = $state(false);
	let form = $state<{ key: number; editing: Slide | null } | null>(null);
	let formKey = 0;

	const canView = $derived(auth.can('DisplayContent.View'));
	const canManage = $derived(auth.can('DisplayContent.Manage'));

	function failed(reason: string): void {
		toast.error($_('slides.messages.failed', { values: { reason } }));
	}

	/** The slide's words in the first language it has, in the board's order, for the list. */
	function headline(slide: Slide): { language: string; title: string } {
		const language = displays.boardLanguages.find((l) => Object.hasOwn(slide.texts, l)) ?? 'en';
		return {
			language,
			title: Object.hasOwn(slide.texts, language) ? slide.texts[language].title : ''
		};
	}

	function appliesTo(slide: Slide): string {
		if (slide.scope === 'Site') return $_('slides.appliesTo.Site');
		return $_(`slides.appliesTo.${slide.scope === 'Zone' ? 'Zone' : 'Display'}`, {
			values: { targets: slide.targets.join(', ') }
		});
	}

	async function loadSite(): Promise<void> {
		form = null;
		loading = true;
		const [found, profiles, boards] = await Promise.all([
			slides.search(siteCode),
			zonesApi.history(siteCode),
			displays.search(siteCode)
		]);
		if (found.hasErrors) failed(found.errorMessages.join(' '));
		list = found.data ?? [];
		displayCodes = (boards.data ?? []).map((d) => d.code).sort((a, b) => a.localeCompare(b));
		const published = (profiles.data ?? []).find((p) => p.status === 'Published');
		const zones = published ? ((await zonesApi.get(published.id)).data?.zones ?? []) : [];
		zoneNames = zones
			.filter((z) => z.kind === 'Queue')
			.map((z) => z.name)
			.sort((a, b) => a.localeCompare(b));
		loading = false;
	}

	async function refresh(): Promise<void> {
		const found = await slides.search(siteCode);
		list = found.data ?? list;
	}

	async function submit(request: SlideRequest): Promise<string[]> {
		const editing = form?.editing;
		const result = editing
			? await slides.update(editing.id, request)
			: await slides.create(request);
		if (result.hasErrors || !result.data) return result.errorMessages;
		toast.success($_('slides.messages.saved', { values: { position: result.data.position } }));
		form = null;
		await refresh();
		return [];
	}

	async function setStatus(slide: Slide, retire: boolean): Promise<void> {
		busy = true;
		const result = retire ? await slides.retire(slide.id) : await slides.restore(slide.id);
		busy = false;
		if (result.hasErrors) return failed(result.errorMessages.join(' '));
		toast.success(
			$_(retire ? 'slides.messages.retired' : 'slides.messages.restored', {
				values: { position: slide.position }
			})
		);
		await refresh();
	}

	async function move(index: number, by: -1 | 1): Promise<void> {
		const target = index + by;
		if (target < 0 || target >= list.length) return;
		const ids = list.map((s) => s.id);
		[ids[index], ids[target]] = [ids[target], ids[index]];
		busy = true;
		const result = await slides.reorder(siteCode, ids);
		busy = false;
		if (result.hasErrors || !result.data) {
			failed(result.errorMessages.join(' '));
			return refresh();
		}
		list = result.data;
		toast.success($_('slides.messages.moved'));
	}

	onMount(async () => {
		// A role without the permission sees the notice and the page asks the API for nothing.
		if (!canView) {
			loading = false;
			return;
		}
		const result = await topology.sites();
		siteList = result.data ?? [];
		siteCode = siteList.find((s) => s.code === 'DMO')?.code ?? siteList[0]?.code ?? '';
		if (siteCode) await loadSite();
		else loading = false;
	});
</script>

<svelte:head>
	<title>{$_('slides.title')} · {$_('app.title')}</title>
</svelte:head>

<SimplePageHeader
	icon={Presentation}
	title={$_('slides.title')}
	description={$_('slides.description')}
>
	{#snippet actions()}
		<label for="slides-site" class="sr-only">{$_('topology.site')}</label>
		<select
			id="slides-site"
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
		{#if canManage}
			<button
				type="button"
				data-testid="add-slide"
				disabled={loading || !siteCode}
				onclick={() => (form = { key: ++formKey, editing: null })}
				class="inline-flex h-10 items-center gap-2 rounded-lg bg-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
			>
				<Plus class="size-4" aria-hidden="true" />
				{$_('slides.actions.add')}
			</button>
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
	<div class="flex flex-col gap-4">
		{#if form}
			{#key form.key}
				<SlideForm
					{siteCode}
					editing={form.editing}
					{zoneNames}
					{displayCodes}
					onSubmit={submit}
					onCancel={() => (form = null)}
				/>
			{/key}
		{/if}

		<section class="overflow-hidden rounded-xl border bg-card" aria-labelledby="slides-title">
			<div class="border-b px-4 py-3">
				<h2 id="slides-title" class="text-base font-semibold">{$_('slides.listTitle')}</h2>
				<p class="text-xs text-muted-foreground">{$_('slides.orderHint')}</p>
			</div>
			{#if !loading && list.length === 0}
				<p class="px-4 py-6 text-sm text-muted-foreground">{$_('slides.none')}</p>
			{:else}
				<div class="overflow-x-auto">
					<table class="w-full text-sm">
						<thead class="bg-muted/40 text-xs text-muted-foreground">
							<tr>
								<th class="px-4 py-2 text-start font-medium">{$_('slides.columns.order')}</th>
								<th class="px-4 py-2 text-start font-medium">{$_('slides.columns.slide')}</th>
								<th class="px-4 py-2 text-start font-medium">{$_('slides.columns.scope')}</th>
								<th class="px-4 py-2 text-start font-medium">{$_('slides.columns.status')}</th>
								<th class="px-4 py-2 text-end font-medium"
									><span class="sr-only">{$_('slides.columns.actions')}</span></th
								>
							</tr>
						</thead>
						<tbody>
							{#each list as slide, index (slide.id)}
								{@const head = headline(slide)}
								<tr
									class="border-t align-top"
									data-testid="slide-row"
									data-position={slide.position}
									data-status={slide.status}
								>
									<td class="px-4 py-2">
										<span class="flex items-center gap-1">
											<span class="w-6 font-mono text-xs tabular-nums">{slide.position}</span>
											{#if canManage}
												<button
													type="button"
													data-testid="slide-up"
													disabled={busy || index === 0}
													aria-label={$_('slides.actions.up', {
														values: { position: slide.position }
													})}
													onclick={() => move(index, -1)}
													class="inline-flex size-7 items-center justify-center rounded-md border bg-card hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-40"
												>
													<ArrowUp class="size-3.5" aria-hidden="true" />
												</button>
												<button
													type="button"
													data-testid="slide-down"
													disabled={busy || index === list.length - 1}
													aria-label={$_('slides.actions.down', {
														values: { position: slide.position }
													})}
													onclick={() => move(index, 1)}
													class="inline-flex size-7 items-center justify-center rounded-md border bg-card hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-40"
												>
													<ArrowDown class="size-3.5" aria-hidden="true" />
												</button>
											{/if}
										</span>
									</td>
									<td class="px-4 py-2">
										<div class="flex items-start gap-2">
											<SlideIcon
												name={slide.icon}
												class="mt-0.5 size-4 shrink-0 text-muted-foreground"
											/>
											<div class="flex min-w-0 flex-col">
												<span
													dir={rtl.has(head.language) ? 'rtl' : 'ltr'}
													lang={head.language}
													data-testid="slide-title">{head.title}</span
												>
												<span class="text-xs text-muted-foreground">
													{Object.keys(slide.texts).join(', ')} · {$_('slides.seconds', {
														values: { seconds: slide.durationSeconds }
													})}{#if slide.pinned}
														· {$_('slides.pinned')}{/if}
												</span>
											</div>
										</div>
									</td>
									<td class="px-4 py-2 text-xs" data-testid="slide-scope">{appliesTo(slide)}</td>
									<td class="px-4 py-2">
										<StatusBadge
											tone={slide.status === 'Active' ? 'success' : 'neutral'}
											label={slide.status === 'Active' ? $_('slides.active') : $_('slides.retired')}
										/>
									</td>
									<td class="px-4 py-2">
										<span class="flex flex-wrap justify-end gap-1.5">
											{#if canManage}
												<button
													type="button"
													data-testid="edit-slide"
													onclick={() => (form = { key: ++formKey, editing: slide })}
													class="inline-flex h-8 items-center gap-1.5 rounded-md border bg-card px-2.5 text-xs font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
												>
													<Pencil class="size-3.5" aria-hidden="true" />
													{$_('slides.actions.edit')}
												</button>
												<button
													type="button"
													data-testid={slide.status === 'Active' ? 'retire-slide' : 'restore-slide'}
													disabled={busy}
													onclick={() => setStatus(slide, slide.status === 'Active')}
													class="inline-flex h-8 items-center rounded-md border bg-card px-2.5 text-xs font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
												>
													{slide.status === 'Active'
														? $_('slides.actions.retire')
														: $_('slides.actions.restore')}
												</button>
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
	</div>
{/if}
