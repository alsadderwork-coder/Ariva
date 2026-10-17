<script lang="ts">
	import { Check, Images, Pencil, Upload } from '@lucide/svelte';
	import { onDestroy, untrack } from 'svelte';
	import { _ } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import ConfirmButton from '$lib/components/shared/ConfirmButton.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import type { StatusTone } from '$lib/components/shared/StatusBadge.svelte';
	import * as media from '$lib/core/advertising-media';
	import type { AdvertisingMedia, MediaDetails, MediaStatus } from '$lib/core/advertising-media';

	/**
	 * A site's advertisement library on the displays screen (ARV-142a), shown while the site's advertising policy allows
	 * advertising to the holders of Advertising.View or Advertising.Manage. Managers upload an image (the server types it
	 * by its bytes and writes it anew) with who advertises, the campaign and its days, change those details (an approved
	 * item goes back to draft) and retire items; approvers approve a draft they neither uploaded nor last changed, with a
	 * second factor (Api.ts opens the step-up dialog), naming the item as the list showed it (its review token: the server
	 * refuses the approval when the item changed since, and the list is read again). The server decides every one of these;
	 * the buttons only follow it.
	 * Previews are fetched with the user's token and shown through object URLs, revoked when the list changes. Every word
	 * is shown as text.
	 */
	interface Props {
		siteCode: string;
		canManage: boolean;
		canApprove: boolean;
	}

	let { siteCode, canManage, canApprove }: Props = $props();

	/** The most previews fetched at once: the newest items, retired ones left out. */
	const maxPreviews = 40;

	let items = $state<AdvertisingMedia[]>([]);
	let previews = $state<Record<string, string>>({});
	let loading = $state(true);
	let busy = $state(false);
	let file = $state<File | null>(null);
	let fileInput = $state<HTMLInputElement | undefined>();
	let details = $state<MediaDetails>(blank());
	let problems = $state<string[]>([]);
	let editing = $state<string | null>(null);
	let edited = $state<MediaDetails>(blank());
	let editProblems = $state<string[]>([]);
	let generation = 0;

	/** A day of this browser's calendar as "yyyy-MM-dd" (a starting value only: the site's own days decide what plays). */
	function day(offset = 0): string {
		const date = new Date();
		date.setDate(date.getDate() + offset);
		return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
	}

	function blank(): MediaDetails {
		return { advertiser: '', campaign: '', startsOn: day(), endsOn: day(30) };
	}

	function trimmed(values: MediaDetails): MediaDetails {
		return { ...values, advertiser: values.advertiser.trim(), campaign: values.campaign.trim() };
	}

	function revokeAll(): void {
		for (const url of Object.values(previews)) URL.revokeObjectURL(url);
		previews = {};
	}

	async function load(code: string): Promise<void> {
		const mine = ++generation;
		loading = true;
		const result = await media.list(code);
		if (mine !== generation) return;
		revokeAll();
		items = result.data ?? [];
		loading = false;
		if (result.hasErrors)
			toast.error(
				$_('advertisingMedia.messages.failed', {
					values: { reason: result.errorMessages.join(' ') }
				})
			);
		for (const item of items.filter((i) => i.status !== 'Retired').slice(0, maxPreviews)) {
			const blob = await media.content(code, item.id);
			if (mine !== generation) return;
			if (!blob.hasErrors && blob.data instanceof Blob)
				previews = { ...previews, [item.id]: URL.createObjectURL(blob.data) };
		}
	}

	$effect(() => {
		const code = siteCode;
		untrack(() => {
			editing = null;
			problems = [];
			void load(code);
		});
	});

	onDestroy(() => {
		generation++;
		revokeAll();
	});

	function chosen(event: Event): void {
		const input = event.currentTarget as HTMLInputElement;
		file = input.files?.[0] ?? null;
		problems =
			file && file.size > media.limits.maxBytes ? [$_('advertisingMedia.messages.tooLarge')] : [];
	}

	async function submit(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		if (busy || !file) return;
		if (file.size > media.limits.maxBytes) {
			problems = [$_('advertisingMedia.messages.tooLarge')];
			return;
		}
		busy = true;
		const result = await media.upload(siteCode, file, trimmed(details));
		busy = false;
		if (result.hasErrors || !result.data) {
			problems = result.errorMessages;
			return;
		}
		problems = [];
		file = null;
		if (fileInput) fileInput.value = '';
		details = blank();
		toast.success($_('advertisingMedia.messages.uploaded'));
		await load(siteCode);
	}

	function startEdit(item: AdvertisingMedia): void {
		editing = item.id;
		edited = {
			advertiser: item.advertiser,
			campaign: item.campaign,
			startsOn: item.startsOn,
			endsOn: item.endsOn
		};
		editProblems = [];
	}

	async function saveEdit(event: SubmitEvent, item: AdvertisingMedia): Promise<void> {
		event.preventDefault();
		if (busy) return;
		busy = true;
		const result = await media.change(siteCode, item.id, trimmed(edited));
		busy = false;
		if (result.hasErrors || !result.data) {
			editProblems = result.errorMessages;
			return;
		}
		editing = null;
		toast.success(
			$_(
				item.status === 'Approved'
					? 'advertisingMedia.messages.changedBackToDraft'
					: 'advertisingMedia.messages.changed'
			)
		);
		await load(siteCode);
	}

	async function act(item: AdvertisingMedia, action: 'approve' | 'retire'): Promise<void> {
		if (busy) return;
		busy = true;
		// An approval names the item as this list showed it; the server refuses it once someone changed the item since.
		const result =
			action === 'approve'
				? await media.approve(siteCode, item.id, item.reviewToken)
				: await media.retire(siteCode, item.id);
		busy = false;
		if (result.hasErrors) {
			toast.error(
				$_('advertisingMedia.messages.failed', {
					values: { reason: result.errorMessages.join(' ') }
				})
			);
			// Show the item as it is now, so a second look is of what would be approved.
			await load(siteCode);
			return;
		}
		toast.success($_(`advertisingMedia.messages.${action === 'approve' ? 'approved' : 'retired'}`));
		await load(siteCode);
	}

	const tones: Record<MediaStatus, StatusTone> = {
		Draft: 'warning',
		Approved: 'success',
		Retired: 'neutral'
	};

	const inputClass =
		'h-9 min-w-0 rounded-md border border-input bg-background px-2 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none';
	const buttonClass =
		'inline-flex h-9 items-center gap-1.5 rounded-md border bg-card px-2.5 text-sm hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60';
</script>

<section
	class="rounded-xl border bg-card"
	aria-labelledby="advertising-library-title"
	data-testid="advertising-library"
>
	<div class="flex flex-wrap items-center justify-between gap-2 border-b px-4 py-3">
		<h2 id="advertising-library-title" class="flex items-center gap-2 text-base font-semibold">
			<Images class="size-4" aria-hidden="true" />
			{$_('advertisingMedia.title')}
		</h2>
	</div>
	<div class="flex flex-col gap-3 px-4 py-3 text-sm">
		<p class="text-muted-foreground">{$_('advertisingMedia.description')}</p>

		{#if canManage}
			<form
				class="grid gap-3 rounded-lg border p-3 sm:grid-cols-2"
				data-testid="media-upload-form"
				onsubmit={submit}
			>
				<div class="flex flex-col gap-1 sm:col-span-2">
					<label for="media-file" class="text-xs font-medium"
						>{$_('advertisingMedia.fields.file')}</label
					>
					<input
						id="media-file"
						type="file"
						accept={media.accept}
						data-testid="media-file"
						required
						bind:this={fileInput}
						onchange={chosen}
						aria-describedby="media-file-hint"
						class="text-sm"
					/>
					<p id="media-file-hint" class="text-xs text-muted-foreground">
						{$_('advertisingMedia.hints.file')}
					</p>
				</div>
				<div class="flex flex-col gap-1">
					<label for="media-advertiser" class="text-xs font-medium"
						>{$_('advertisingMedia.fields.advertiser')}</label
					>
					<input
						id="media-advertiser"
						data-testid="media-advertiser"
						bind:value={details.advertiser}
						required
						maxlength={media.limits.maxName}
						class={inputClass}
					/>
				</div>
				<div class="flex flex-col gap-1">
					<label for="media-campaign" class="text-xs font-medium"
						>{$_('advertisingMedia.fields.campaign')}</label
					>
					<input
						id="media-campaign"
						data-testid="media-campaign"
						bind:value={details.campaign}
						required
						maxlength={media.limits.maxName}
						class={inputClass}
					/>
				</div>
				<div class="flex flex-col gap-1">
					<label for="media-starts" class="text-xs font-medium"
						>{$_('advertisingMedia.fields.startsOn')}</label
					>
					<input
						id="media-starts"
						type="date"
						data-testid="media-starts"
						bind:value={details.startsOn}
						required
						class={inputClass}
					/>
				</div>
				<div class="flex flex-col gap-1">
					<label for="media-ends" class="text-xs font-medium"
						>{$_('advertisingMedia.fields.endsOn')}</label
					>
					<input
						id="media-ends"
						type="date"
						data-testid="media-ends"
						bind:value={details.endsOn}
						required
						class={inputClass}
					/>
				</div>
				{#if problems.length > 0}
					<ul
						class="list-disc ps-5 text-xs text-destructive sm:col-span-2"
						role="alert"
						data-testid="media-problems"
					>
						{#each problems as problem, index (index)}
							<li>{problem}</li>
						{/each}
					</ul>
				{/if}
				<div class="sm:col-span-2">
					<button
						type="submit"
						data-testid="media-upload"
						disabled={busy || !file}
						class="inline-flex h-9 items-center gap-1.5 rounded-md bg-primary px-3 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
					>
						<Upload class="size-4" aria-hidden="true" />
						{$_('advertisingMedia.actions.upload')}
					</button>
				</div>
			</form>
		{/if}

		{#if loading}
			<p class="text-muted-foreground" data-testid="media-loading">
				{$_('advertisingMedia.loading')}
			</p>
		{:else if items.length === 0}
			<p class="text-muted-foreground" data-testid="media-empty">{$_('advertisingMedia.none')}</p>
		{:else}
			<ul class="flex flex-col divide-y" data-testid="media-list">
				{#each items as item (item.id)}
					<li
						class="flex flex-wrap items-start gap-3 py-3"
						data-testid="media-row"
						data-id={item.id}
						data-status={item.status}
					>
						<div
							class="flex size-24 shrink-0 items-center justify-center overflow-hidden rounded-md border bg-muted"
						>
							{#if previews[item.id]}
								<img
									src={previews[item.id]}
									alt={$_('advertisingMedia.previewAlt', {
										values: { advertiser: item.advertiser, campaign: item.campaign }
									})}
									class="max-h-full max-w-full object-contain"
									data-testid="media-preview"
								/>
							{:else}
								<Images class="size-6 text-muted-foreground" aria-hidden="true" />
							{/if}
						</div>
						<div class="flex min-w-0 flex-1 flex-col gap-1">
							<p class="font-medium break-words" data-testid="media-advertiser-text">
								{item.advertiser}
							</p>
							<p class="break-words text-muted-foreground" data-testid="media-campaign-text">
								{item.campaign}
							</p>
							<p class="text-xs text-muted-foreground">
								{$_('advertisingMedia.dates', {
									values: { first: item.startsOn, last: item.endsOn }
								})}
								{' · '}
								{$_('advertisingMedia.size', {
									values: { width: item.widthPixels, height: item.heightPixels }
								})}
								{#if item.originalFileName}{' · '}<span class="font-mono break-all"
										>{item.originalFileName}</span
									>{/if}
							</p>
							<p class="text-xs text-muted-foreground">
								{$_('advertisingMedia.uploadedBy', { values: { name: item.uploadedBy ?? '' } })}
								{#if item.approvedBy}{' · '}{$_('advertisingMedia.approvedBy', {
										values: { name: item.approvedBy }
									})}{/if}
							</p>
							{#if editing === item.id}
								<form
									class="mt-2 grid gap-2 sm:grid-cols-2"
									data-testid="media-edit-form"
									onsubmit={(event) => saveEdit(event, item)}
								>
									<label class="flex flex-col gap-1 text-xs font-medium">
										{$_('advertisingMedia.fields.advertiser')}
										<input
											data-testid="media-edit-advertiser"
											bind:value={edited.advertiser}
											required
											maxlength={media.limits.maxName}
											class={inputClass}
										/>
									</label>
									<label class="flex flex-col gap-1 text-xs font-medium">
										{$_('advertisingMedia.fields.campaign')}
										<input
											data-testid="media-edit-campaign"
											bind:value={edited.campaign}
											required
											maxlength={media.limits.maxName}
											class={inputClass}
										/>
									</label>
									<label class="flex flex-col gap-1 text-xs font-medium">
										{$_('advertisingMedia.fields.startsOn')}
										<input
											type="date"
											data-testid="media-edit-starts"
											bind:value={edited.startsOn}
											required
											class={inputClass}
										/>
									</label>
									<label class="flex flex-col gap-1 text-xs font-medium">
										{$_('advertisingMedia.fields.endsOn')}
										<input
											type="date"
											data-testid="media-edit-ends"
											bind:value={edited.endsOn}
											required
											class={inputClass}
										/>
									</label>
									{#if item.status === 'Approved'}
										<p
											class="text-xs text-status-warning-foreground sm:col-span-2"
											data-testid="media-edit-warning"
										>
											{$_('advertisingMedia.hints.backToDraft')}
										</p>
									{/if}
									{#if editProblems.length > 0}
										<ul
											class="list-disc ps-5 text-xs text-destructive sm:col-span-2"
											role="alert"
											data-testid="media-edit-problems"
										>
											{#each editProblems as problem, index (index)}
												<li>{problem}</li>
											{/each}
										</ul>
									{/if}
									<div class="flex flex-wrap gap-2 sm:col-span-2">
										<button
											type="submit"
											data-testid="media-save"
											disabled={busy}
											class={buttonClass}>{$_('advertisingMedia.actions.save')}</button
										>
										<button type="button" onclick={() => (editing = null)} class={buttonClass}
											>{$_('advertisingMedia.actions.cancel')}</button
										>
									</div>
								</form>
							{/if}
						</div>
						<div class="flex flex-col items-end gap-2">
							<span class="flex flex-wrap items-center gap-1.5">
								<StatusBadge
									tone={tones[item.status]}
									label={$_(`advertisingMedia.status.${item.status}`)}
								/>
								{#if item.playable}
									<StatusBadge tone="info" label={$_('advertisingMedia.playing')} />
								{/if}
							</span>
							<span class="flex flex-wrap justify-end gap-2">
								{#if canApprove && item.canApprove}
									<button
										type="button"
										data-testid="media-approve"
										disabled={busy}
										onclick={() => act(item, 'approve')}
										class={buttonClass}
									>
										<Check class="size-4" aria-hidden="true" />
										{$_('advertisingMedia.actions.approve')}
									</button>
								{/if}
								{#if canManage && item.status !== 'Retired'}
									{#if editing !== item.id}
										<button
											type="button"
											data-testid="media-edit"
											disabled={busy}
											onclick={() => startEdit(item)}
											class={buttonClass}
										>
											<Pencil class="size-4" aria-hidden="true" />
											{$_('advertisingMedia.actions.change')}
										</button>
									{/if}
									<ConfirmButton
										label={$_('advertisingMedia.actions.retire')}
										confirmLabel={$_('advertisingMedia.actions.confirmRetire')}
										testId="media-retire"
										disabled={busy}
										onConfirm={() => act(item, 'retire')}
									/>
								{/if}
							</span>
						</div>
					</li>
				{/each}
			</ul>
		{/if}
	</div>
</section>
