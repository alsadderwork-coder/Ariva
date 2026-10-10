<script lang="ts">
	import { Plus, Search } from '@lucide/svelte';
	import { onDestroy, onMount } from 'svelte';
	import { _, locale } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import {
		campaignStatuses,
		createCampaign,
		searchCampaigns,
		type CampaignStatus,
		type CampaignSummary,
		type CreateCampaignRequest
	} from '$lib/core/validation';
	import CampaignForm from './CampaignForm.svelte';
	import { dayLabel, siteDateTime, statusTone } from './campaignFormat';

	interface Props {
		siteCode: string;
		canManage: boolean;
		seesDesks: boolean;
		/** The address of a campaign of this site. */
		hrefOf: (campaignId: string) => string;
		onCreated: (campaignId: string) => void;
	}

	let { siteCode, canManage, seesDesks, hrefOf, onCreated }: Props = $props();

	let list = $state<CampaignSummary[]>([]);
	let total = $state(0);
	let text = $state('');
	let status = $state<CampaignStatus | ''>('');
	let loading = $state(true);
	let problem = $state('');
	let creating = $state(false);
	let destroyed = false;
	/** The newest search wins: a slow answer to an earlier one is dropped. */
	let ticket = 0;

	async function load(): Promise<void> {
		const mine = ++ticket;
		loading = true;
		const result = await searchCampaigns(siteCode, { text, status });
		if (destroyed || mine !== ticket) return;
		loading = false;
		if (result.hasErrors || !result.data) {
			problem = $_('validationCampaigns.failed', {
				values: { reason: result.errorMessages.join(' ') }
			});
			list = [];
			total = 0;
			return;
		}
		problem = '';
		list = result.data.data;
		total = result.data.totalCount;
	}

	function search(event: SubmitEvent): void {
		event.preventDefault();
		void load();
	}

	async function create(request: CreateCampaignRequest): Promise<string[]> {
		const result = await createCampaign(siteCode, request);
		if (result.hasErrors || !result.data) return result.errorMessages;
		toast.success($_('validationCampaigns.form.created'));
		creating = false;
		onCreated(result.data.id);
		return [];
	}

	onMount(() => {
		void load();
	});

	onDestroy(() => {
		destroyed = true;
	});
</script>

{#if creating}
	<CampaignForm {siteCode} {seesDesks} onSubmit={create} onCancel={() => (creating = false)} />
{/if}

<section aria-labelledby="campaigns-title" class="rounded-xl border bg-card p-5">
	<div class="flex flex-wrap items-start justify-between gap-3">
		<div class="min-w-0">
			<h2 id="campaigns-title" class="text-xl font-semibold">
				{$_('validationCampaigns.list.title')}
			</h2>
			<p class="mt-1 max-w-prose text-sm text-secondary-foreground">
				{$_('validationCampaigns.list.intro')}
			</p>
		</div>
		{#if canManage && !creating}
			<button
				type="button"
				data-testid="new-campaign"
				onclick={() => (creating = true)}
				class="inline-flex h-10 items-center gap-2 rounded-lg bg-button-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none dark:text-white"
			>
				<Plus class="size-4" aria-hidden="true" />
				{$_('validationCampaigns.list.new')}
			</button>
		{/if}
	</div>

	<form class="mt-4 flex flex-wrap items-end gap-3" role="search" onsubmit={search}>
		<div class="flex min-w-0 flex-col gap-1">
			<label for="campaigns-text" class="text-xs font-medium"
				>{$_('validationCampaigns.list.search')}</label
			>
			<input
				id="campaigns-text"
				data-testid="campaigns-text"
				type="search"
				bind:value={text}
				maxlength={64}
				autocomplete="off"
				class="h-10 w-64 max-w-full min-w-0 rounded-lg border border-input bg-background px-3 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
			/>
		</div>
		<div class="flex min-w-0 flex-col gap-1">
			<label for="campaigns-status" class="text-xs font-medium"
				>{$_('validationCampaigns.list.status')}</label
			>
			<select
				id="campaigns-status"
				data-testid="campaigns-status"
				bind:value={status}
				onchange={() => void load()}
				class="h-10 rounded-lg border border-input bg-background px-3 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
			>
				<option value="">{$_('validationCampaigns.list.allStatuses')}</option>
				{#each campaignStatuses as option (option)}
					<option value={option}>{$_(`validationCampaigns.statuses.${option}`)}</option>
				{/each}
			</select>
		</div>
		<button
			type="submit"
			data-testid="campaigns-search"
			class="inline-flex h-10 items-center gap-2 rounded-lg border bg-card px-3 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			<Search class="size-4" aria-hidden="true" />
			{$_('validationCampaigns.list.search')}
		</button>
	</form>

	{#if problem}
		<p role="alert" class="mt-4 text-sm text-status-danger-foreground">{problem}</p>
	{:else if loading && list.length === 0}
		<p class="mt-4 text-sm text-muted-foreground" role="status">
			{$_('validationCampaigns.loading')}
		</p>
	{:else if list.length === 0}
		<p class="mt-4 text-sm text-muted-foreground" data-testid="campaigns-none">
			{text.trim() || status
				? $_('validationCampaigns.list.noneMatching')
				: $_('validationCampaigns.list.none')}
		</p>
	{:else}
		<div class="mt-4 overflow-x-auto rounded-lg border">
			<table class="w-full text-sm" data-testid="campaigns-table">
				<caption class="sr-only"
					>{$_('validationCampaigns.list.caption', { values: { site: siteCode } })}</caption
				>
				<thead class="bg-surface-2">
					<tr>
						{#each ['name', 'status', 'version', 'days', 'scope', 'created'] as column (column)}
							<th
								scope="col"
								class="p-4 text-start text-xs font-semibold tracking-wide text-tertiary uppercase"
								>{$_(`validationCampaigns.list.columns.${column}`)}</th
							>
						{/each}
					</tr>
				</thead>
				<tbody>
					{#each list as campaign, index (index)}
						<tr
							data-testid="campaign-row"
							data-status={campaign.status}
							class="odd:bg-surface even:bg-surface-2/50 hover:bg-muted"
						>
							<td class="px-4 py-3">
								<a
									href={hrefOf(campaign.id)}
									data-testid="campaign-link"
									class="font-medium break-all text-primary underline-offset-4 hover:underline focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
									>{campaign.name}</a
								>
							</td>
							<td class="px-4 py-3">
								<StatusBadge
									tone={statusTone(campaign.status)}
									label={$_(`validationCampaigns.statuses.${campaign.status}`)}
								/>
							</td>
							<td class="px-4 py-3 tabular-nums">v{campaign.profileVersion}</td>
							<td class="px-4 py-3 whitespace-nowrap tabular-nums">
								{#if campaign.days.length > 0}
									<time datetime={campaign.days[0]}
										>{$_('validationCampaigns.list.days', {
											values: {
												count: campaign.days.length,
												first: dayLabel(campaign.days[0], $locale)
											}
										})}</time
									>
								{/if}
							</td>
							<td class="px-4 py-3"
								>{$_('validationCampaigns.list.scope', {
									values: { zones: campaign.zones, lines: campaign.lines }
								})}</td
							>
							<td class="px-4 py-3 whitespace-nowrap tabular-nums">
								<time datetime={campaign.createdUtc}
									>{siteDateTime(campaign.createdUtc, 'UTC', $locale)} UTC</time
								>
							</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
		{#if total > list.length}
			<p class="mt-3 text-xs text-muted-foreground" data-testid="campaigns-more">
				{$_('validationCampaigns.list.more', { values: { shown: list.length, total } })}
			</p>
		{/if}
	{/if}
</section>
