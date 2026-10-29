<script lang="ts">
	import { Megaphone, Pencil } from '@lucide/svelte';
	import { _ } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import * as advertising from '$lib/core/advertising';
	import type { AdvertisingPolicy } from '$lib/core/advertising';

	/**
	 * A site's advertising policy on the displays screen (ARV-145): whether the site's displays may show advertising, the
	 * queue zones where it stays off, the reason of the last change, and the displays set to advertising that show
	 * instructions because of it. Holders of AdvertisingPolicy.Edit (system administrators) change it with a reason naming
	 * who authorised it; the save asks for a second factor (Api.ts opens the step-up dialog) and names the zone profile version
	 * shown here, so the server refuses it (409) when another version was published meanwhile. ARV-193: the form takes the
	 * version it reviews, with its queue zones, when it opens, and keeps them: a newer version that a refresh or a refused save
	 * brings is shown in a notice with an action to review it, never switched in silently. The screen keys the panel by its
	 * site, so changing site closes the form. Everything is shown as text.
	 */
	interface Props {
		siteCode: string;
		policy: AdvertisingPolicy;
		canEdit: boolean;
		onChanged: (policy: AdvertisingPolicy) => void;
	}

	let { siteCode, policy, canEdit, onChanged }: Props = $props();

	let editing = $state(false);
	let allowed = $state(false);
	let excluded = $state<string[]>([]);
	let reason = $state('');
	let problems = $state<string[]>([]);
	let busy = $state(false);
	/**
	 * The zone profile version the open form reviews, taken from the policy when the form opens (or when the administrator
	 * reviews a newer one): its id, which the save names, its version and its queue zones (read with the policy, never from
	 * another request, so they always match it).
	 */
	let review = $state<{ profileId: string | null; version: number | null; zones: string[] } | null>(
		null
	);

	/** The reviewed version's queue zones, then any excluded zone no longer among them, so it can be taken off the list. */
	const published = $derived(review?.zones ?? []);
	const offered = $derived([...published, ...excluded.filter((zone) => !published.includes(zone))]);
	/** A version published since the form took its review: saving is refused (409) until the administrator reviews it. */
	const stale = $derived(
		editing && review !== null && policy.publishedProfileId !== review.profileId
	);

	function take(): void {
		review = {
			profileId: policy.publishedProfileId,
			version: policy.publishedProfileVersion,
			zones: [...(policy.publishedQueueZones ?? [])]
		};
		problems = [];
	}

	function edit(): void {
		allowed = policy.allowed;
		excluded = [...policy.excludedZones];
		reason = '';
		take();
		editing = true;
	}

	function toggle(zone: string, on: boolean): void {
		excluded = on ? [...excluded, zone] : excluded.filter((z) => z !== zone);
	}

	async function save(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		if (busy) return;
		busy = true;
		// The site this save is for, kept across its awaits: the screen may move to another site meanwhile, and what comes back
		// for this one is then not shown on the other (the screen checks it too).
		const site = siteCode;
		const reviewed = review?.profileId ?? null;
		const result = await advertising.change(site, {
			allowed,
			excludedZones: [...excluded],
			reason: reason.trim(),
			reviewedProfileId: reviewed
		});
		busy = false;
		if (siteCode !== site) return;
		if (result.hasErrors || !result.data) {
			problems = result.errorMessages;
			// Another zone profile may have been published since the review: the notice shows it, and the form keeps the version
			// it reviews until the administrator reviews the new one.
			const fresh = await advertising.get(site);
			if (siteCode !== site) return;
			if (fresh.data && fresh.data.publishedProfileId !== reviewed) onChanged(fresh.data);
			return;
		}
		editing = false;
		review = null;
		toast.success($_('advertising.messages.saved'));
		onChanged(result.data);
	}
</script>

<section
	class="rounded-xl border bg-card"
	aria-labelledby="advertising-title"
	data-testid="advertising-policy"
	data-allowed={policy.allowed}
>
	<div class="flex flex-wrap items-center justify-between gap-2 border-b px-4 py-3">
		<h2 id="advertising-title" class="flex items-center gap-2 text-base font-semibold">
			<Megaphone class="size-4" aria-hidden="true" />
			{$_('advertising.title')}
		</h2>
		<span class="flex items-center gap-2">
			<StatusBadge
				tone={policy.allowed ? 'info' : 'neutral'}
				label={policy.allowed ? $_('advertising.allowed') : $_('advertising.off')}
			/>
			{#if canEdit && !editing}
				<button
					type="button"
					data-testid="edit-advertising"
					onclick={edit}
					class="inline-flex h-9 items-center gap-1.5 rounded-md border bg-card px-2.5 text-sm hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
				>
					<Pencil class="size-4" aria-hidden="true" />
					{$_('advertising.actions.change')}
				</button>
			{/if}
		</span>
	</div>
	<div class="flex flex-col gap-2 px-4 py-3 text-sm">
		<p class="text-muted-foreground">{$_('advertising.description')}</p>
		{#if policy.excludedZones.length > 0}
			<p data-testid="advertising-excluded">
				{$_('advertising.excluded', { values: { zones: policy.excludedZones.join(', ') } })}
			</p>
		{/if}
		{#if policy.reason}
			<p class="text-xs text-muted-foreground" data-testid="advertising-reason">
				{$_('advertising.lastChange', {
					values: {
						reason: policy.reason,
						when: policy.changedOn ? new Date(policy.changedOn).toLocaleString() : ''
					}
				})}
			</p>
		{/if}
		{#if policy.profileChanged}
			<p
				class="rounded-md border border-status-warning-border bg-status-warning px-3 py-2 text-xs text-status-warning-foreground"
				data-testid="advertising-profile-changed"
			>
				{#if policy.publishedProfileVersion === null}
					{$_('advertising.noProfile')}
				{:else if policy.confirmedProfileVersion === null}
					{$_('advertising.profileChangedFirst', {
						values: { current: policy.publishedProfileVersion }
					})}
				{:else}
					{$_('advertising.profileChanged', {
						values: {
							current: policy.publishedProfileVersion,
							confirmed: policy.confirmedProfileVersion
						}
					})}
				{/if}
			</p>
		{/if}
		{#if policy.unpublishedZones.length > 0}
			<p
				class="rounded-md border border-status-warning-border bg-status-warning px-3 py-2 text-xs text-status-warning-foreground"
				data-testid="advertising-unpublished"
			>
				{$_('advertising.unpublished', { values: { zones: policy.unpublishedZones.join(', ') } })}
			</p>
		{/if}
		{#if policy.displaysFallingBack.length > 0}
			<p class="text-xs" data-testid="advertising-falling-back">
				{$_('advertising.fallingBack', {
					values: { displays: policy.displaysFallingBack.join(', ') }
				})}
			</p>
		{/if}

		{#if editing}
			<form class="mt-2 flex flex-col gap-3" data-testid="advertising-form" onsubmit={save}>
				<p class="text-xs text-muted-foreground" data-testid="advertising-profile-version">
					{review?.version
						? $_('advertising.reviewing', { values: { version: review.version } })
						: $_('advertising.noProfile')}
				</p>
				{#if stale}
					<div
						class="flex flex-wrap items-center gap-2 rounded-md border border-status-warning-border bg-status-warning px-3 py-2 text-xs text-status-warning-foreground"
						role="status"
						data-testid="advertising-review-stale"
					>
						<p class="min-w-0 flex-1">
							{#if policy.publishedProfileVersion === null}
								{$_('advertising.reviewStaleGone')}
							{:else if !review?.version}
								{$_('advertising.reviewStaleFirst', {
									values: { current: policy.publishedProfileVersion }
								})}
							{:else}
								{$_('advertising.reviewStale', {
									values: { current: policy.publishedProfileVersion, reviewed: review?.version }
								})}
							{/if}
						</p>
						{#if policy.publishedProfileVersion !== null}
							<button
								type="button"
								data-testid="review-advertising"
								onclick={take}
								class="inline-flex h-8 items-center rounded-md border bg-card px-2.5 text-xs text-foreground hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
							>
								{$_('advertising.actions.review', {
									values: { version: policy.publishedProfileVersion }
								})}
							</button>
						{/if}
					</div>
				{/if}
				<label class="inline-flex items-center gap-2 text-sm">
					<input type="checkbox" data-testid="advertising-allowed" bind:checked={allowed} />
					<span>{$_('advertising.fields.allowed')}</span>
				</label>
				<fieldset class="flex flex-col gap-1.5">
					<legend class="text-xs font-medium">{$_('advertising.fields.excluded')}</legend>
					{#if offered.length === 0}
						<p class="text-xs text-muted-foreground">{$_('advertising.noZones')}</p>
					{:else}
						<div class="flex flex-wrap gap-x-4 gap-y-1.5">
							{#each offered as zone (zone)}
								<label class="inline-flex items-center gap-1.5 text-sm">
									<input
										type="checkbox"
										data-testid="advertising-zone"
										data-zone={zone}
										checked={excluded.includes(zone)}
										onchange={(event) => toggle(zone, event.currentTarget.checked)}
									/>
									<span class="font-mono text-xs">{zone}</span>
								</label>
							{/each}
						</div>
					{/if}
				</fieldset>
				<div class="flex flex-col gap-1">
					<label for="advertising-reason-input" class="text-xs font-medium"
						>{$_('advertising.fields.reason')}</label
					>
					<input
						id="advertising-reason-input"
						data-testid="advertising-reason-input"
						bind:value={reason}
						required
						minlength={advertising.limits.minReason}
						maxlength={advertising.limits.maxReason}
						aria-describedby="advertising-reason-hint"
						class="h-9 min-w-0 rounded-md border border-input bg-background px-2 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
					/>
					<p id="advertising-reason-hint" class="text-xs text-muted-foreground">
						{$_('advertising.hints.reason')}
					</p>
				</div>
				{#if problems.length > 0}
					<ul
						class="list-disc ps-5 text-xs text-destructive"
						role="alert"
						data-testid="advertising-problems"
					>
						{#each problems as problem, index (index)}
							<li>{problem}</li>
						{/each}
					</ul>
				{/if}
				<div class="flex flex-wrap gap-2">
					<button
						type="submit"
						data-testid="save-advertising"
						disabled={busy}
						class="inline-flex h-9 items-center rounded-md bg-primary px-3 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
					>
						{$_('advertising.actions.save')}
					</button>
					<button
						type="button"
						onclick={() => {
							editing = false;
							review = null;
						}}
						class="inline-flex h-9 items-center rounded-md border bg-card px-3 text-sm hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
					>
						{$_('advertising.actions.cancel')}
					</button>
				</div>
			</form>
		{/if}
	</div>
</section>
