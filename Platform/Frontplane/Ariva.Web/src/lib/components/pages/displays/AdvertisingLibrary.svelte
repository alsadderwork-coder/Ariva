<script lang="ts">
	import { Check, Film, Images, Pencil, Upload } from '@lucide/svelte';
	import { onDestroy, untrack } from 'svelte';
	import { _ } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import ConfirmButton from '$lib/components/shared/ConfirmButton.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import type { StatusTone } from '$lib/components/shared/StatusBadge.svelte';
	import * as media from '$lib/core/advertising-media';
	import type { AdvertisingMedia, MediaDetails, MediaStatus } from '$lib/core/advertising-media';

	/**
	 * A site's advertisement library on the displays screen (ARV-142a, videos ARV-142b), shown while the site's advertising
	 * policy allows advertising to the holders of Advertising.View or Advertising.Manage. Managers upload an image (the
	 * server types it by its bytes and writes it anew) or a video (the server checks its container and strips its
	 * metadata) with who advertises, the campaign and its days, change those details (an approved
	 * item goes back to draft) and retire items; approvers approve a draft they neither uploaded nor last changed, with a
	 * second factor (Api.ts opens the step-up dialog), naming the item as the list showed it (its review token: the server
	 * refuses the approval when the item changed since, and the list is read again). The server decides every one of these;
	 * the buttons only follow it.
	 * Previews are fetched with the user's token and shown through object URLs, revoked when the list changes: images for
	 * the newest items as the list loads, a video only when asked (it can be 100 MB), played muted in a video element from
	 * its object URL (media-src blob:). ARV-194: drafts' previews are fetched first, whatever their age; Approve is offered only
	 * for a draft whose preview loaded, and opens it at its full size (or the screen's) on the board's background in a dialog,
	 * whose own Approve sends the approval once the browser has decoded the picture there (a video: its first frame, not the
	 * whole of it watched). The review token proves the details did not change since the list; the dialog only puts the
	 * picture in front of the approver before they confirm. Every word is shown as text.
	 */
	interface Props {
		siteCode: string;
		canManage: boolean;
		canApprove: boolean;
	}

	let { siteCode, canManage, canApprove }: Props = $props();

	/** The most image previews fetched at once: the newest images, retired ones left out (videos load when asked). */
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
	/** The draft shown at full size before its approval is sent (ARV-194), and its dialog. */
	let reviewing = $state<AdvertisingMedia | null>(null);
	let reviewDialog = $state<HTMLDialogElement>();
	let reviewCancel = $state<HTMLButtonElement>();
	/** Whether the browser decoded the reviewed picture (or a video's first frame) in the dialog. */
	let reviewShown = $state<'loading' | 'shown' | 'failed'>('loading');
	/** While the list's pictures are being fetched, a draft without one yet is loading, not missing. */
	let fetchingPreviews = $state(false);

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
		// The review shows a preview's object URL, which goes with the list.
		closeReview();
		revokeAll();
		items = result.data ?? [];
		fetchingPreviews = true;
		loading = false;
		if (result.hasErrors)
			toast.error(
				$_('advertisingMedia.messages.failed', {
					values: { reason: result.errorMessages.join(' ') }
				})
			);
		const images = items.filter((i) => i.status !== 'Retired' && !media.isVideoType(i.contentType));
		// Drafts first, whatever their age, so a draft waiting for approval has its picture to be judged by.
		const ordered = [
			...images.filter((i) => i.status === 'Draft'),
			...images.filter((i) => i.status !== 'Draft')
		];
		for (const item of ordered.slice(0, maxPreviews)) {
			const blob = await media.content(code, item.id);
			if (mine !== generation) return;
			if (!blob.hasErrors && blob.data instanceof Blob)
				previews = { ...previews, [item.id]: URL.createObjectURL(blob.data) };
		}
		fetchingPreviews = false;
	}

	/** The videos being fetched now: a second click waits for the first fetch rather than leaking its object URL. */
	const fetching = new Set<string>();

	/** Fetches one video with the user's token for its preview; a later list or another site drops it. */
	async function loadVideo(item: AdvertisingMedia): Promise<void> {
		if (previews[item.id] || fetching.has(item.id)) return;
		fetching.add(item.id);
		const mine = generation;
		const code = siteCode;
		const blob = await media.content(code, item.id).finally(() => fetching.delete(item.id));
		if (mine !== generation) return;
		if (blob.hasErrors || !(blob.data instanceof Blob)) {
			toast.error(
				$_('advertisingMedia.messages.failed', {
					values: { reason: blob.errorMessages.join(' ') }
				})
			);
			return;
		}
		previews = { ...previews, [item.id]: URL.createObjectURL(blob.data) };
	}

	/** The length in seconds, to a tenth (2,008 ms shows as 2). */
	function seconds(milliseconds: number): number {
		return Math.round(milliseconds / 100) / 10;
	}

	function tooLarge(chosenFile: File): string | null {
		if (media.isVideoType(chosenFile.type))
			return chosenFile.size > media.limits.maxVideoBytes
				? $_('advertisingMedia.messages.tooLargeVideo')
				: null;
		return chosenFile.size > media.limits.maxBytes
			? $_('advertisingMedia.messages.tooLarge')
			: null;
	}

	$effect(() => {
		const code = siteCode;
		untrack(() => {
			editing = null;
			problems = [];
			closeReview();
			void load(code);
		});
	});

	// The review opens as soon as it is shown: a native modal, so focus stays inside it and the page behind is inert. It
	// waits for Cancel to be bound too, so focus starts there (not on a video's controls) and a stray Enter does not approve.
	$effect(() => {
		if (reviewing && reviewDialog && reviewCancel && !reviewDialog.open) {
			reviewDialog.showModal();
			reviewCancel.focus();
		}
	});

	/** Shows a draft at full size before its approval; only a draft whose preview loaded can be reviewed. */
	function review(item: AdvertisingMedia): void {
		if (!previews[item.id]) return;
		reviewShown = 'loading';
		reviewing = item;
	}

	/**
	 * Judges the reviewed picture once the browser answers for it (load, loadeddata and error are queued as tasks, so the
	 * handlers attached as the element is made hear them): shown only when it decoded to a picture of some size. An image
	 * is decoded in full (`decode()`, not only its header); a video once its first frame is there. An answer from a dialog
	 * already closed (its element is out of the page) or that comes after another review began says nothing of this one.
	 */
	function judged(event: Event): void {
		const element = event.currentTarget;
		const item = reviewing;
		const current = () => reviewing === item && element instanceof Element && element.isConnected;
		if (!item || !current()) return;
		if (event.type === 'error') reviewShown = 'failed';
		else if (element instanceof HTMLImageElement)
			element.decode().then(
				() => {
					if (current()) reviewShown = element.naturalWidth > 0 ? 'shown' : 'failed';
				},
				() => {
					if (current()) reviewShown = 'failed';
				}
			);
		else if (element instanceof HTMLVideoElement)
			reviewShown =
				element.readyState >= HTMLMediaElement.HAVE_CURRENT_DATA && element.videoWidth > 0
					? 'shown'
					: 'failed';
	}

	function closeReview(): void {
		reviewing = null;
		if (reviewDialog?.open) reviewDialog.close();
	}

	/** Sends the approval of the draft in the review, which closes first (the step-up dialog may follow). */
	async function approveReviewed(): Promise<void> {
		const item = reviewing;
		if (!item || !previews[item.id] || reviewShown !== 'shown') return;
		closeReview();
		await act(item, 'approve');
	}

	onDestroy(() => {
		generation++;
		revokeAll();
	});

	function chosen(event: Event): void {
		const input = event.currentTarget as HTMLInputElement;
		file = input.files?.[0] ?? null;
		const problem = file ? tooLarge(file) : null;
		problems = problem ? [problem] : [];
	}

	async function submit(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		if (busy || !file) return;
		const problem = tooLarge(file);
		if (problem) {
			problems = [problem];
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
							{#if media.isVideoType(item.contentType)}
								{#if previews[item.id]}
									<video
										src={previews[item.id]}
										controls
										muted
										playsinline
										preload="metadata"
										aria-label={$_('advertisingMedia.videoLabel', {
											values: { advertiser: item.advertiser, campaign: item.campaign }
										})}
										class="max-h-full max-w-full object-contain"
										data-testid="media-video"
									></video>
								{:else}
									<button
										type="button"
										data-testid="media-load-video"
										onclick={() => loadVideo(item)}
										class="flex size-full flex-col items-center justify-center gap-1 text-xs text-muted-foreground hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
									>
										<Film class="size-6" aria-hidden="true" />
										{$_('advertisingMedia.actions.loadVideo')}
									</button>
								{/if}
							{:else if previews[item.id]}
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
								{#if item.durationMilliseconds}{' · '}<span data-testid="media-length"
										>{$_('advertisingMedia.length', {
											values: { seconds: seconds(item.durationMilliseconds) }
										})}</span
									>{/if}
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
								{#if canApprove && item.canApprove && previews[item.id]}
									<button
										type="button"
										data-testid="media-approve"
										disabled={busy}
										onclick={() => review(item)}
										class={buttonClass}
									>
										<Check class="size-4" aria-hidden="true" />
										{$_('advertisingMedia.actions.approve')}
									</button>
								{:else if canApprove && item.canApprove}
									<span
										class="max-w-56 text-end text-xs text-muted-foreground"
										data-testid="media-approve-unavailable"
									>
										{$_(
											media.isVideoType(item.contentType)
												? 'advertisingMedia.review.loadVideoFirst'
												: fetchingPreviews
													? 'advertisingMedia.review.loadingPreview'
													: 'advertisingMedia.review.noPreview'
										)}
									</span>
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
	{#if reviewing}
		<dialog
			bind:this={reviewDialog}
			data-testid="media-review-dialog"
			aria-labelledby="media-review-title"
			aria-describedby="media-review-hint"
			oncancel={(event) => {
				event.preventDefault();
				closeReview();
			}}
			onclose={() => (reviewing = null)}
			class="m-auto max-h-[calc(100dvh-2rem)] max-w-[calc(100vw-2rem)] overflow-auto rounded-xl border bg-card p-0 text-card-foreground shadow-xl backdrop:bg-black/60"
		>
			<div class="flex flex-col gap-3 p-4">
				<h2 id="media-review-title" class="text-lg font-semibold">
					{$_('advertisingMedia.review.title')}
				</h2>
				<p id="media-review-hint" class="text-sm text-muted-foreground">
					{$_('advertisingMedia.review.hint')}
				</p>
				<p class="text-sm">
					<span class="font-medium break-words" data-testid="media-review-advertiser"
						>{reviewing.advertiser}</span
					>{' · '}<span class="break-words" data-testid="media-review-campaign"
						>{reviewing.campaign}</span
					>
				</p>
				<!-- The board's own background (a video on black, as the board plays it), so the picture is judged as the displays show it. -->
				<div class="flex justify-center overflow-auto rounded-md border bg-[#0b1220]">
					{#if media.isVideoType(reviewing.contentType)}
						<!-- preload="auto": the object URL is already in memory, and its first frame must decode to be judged. -->
						<video
							src={previews[reviewing.id]}
							controls
							muted
							playsinline
							preload="auto"
							onloadeddata={judged}
							onerror={judged}
							aria-label={$_('advertisingMedia.videoLabel', {
								values: { advertiser: reviewing.advertiser, campaign: reviewing.campaign }
							})}
							class="max-h-[70dvh] max-w-full bg-black object-contain"
							data-testid="media-review-video"
						></video>
					{:else}
						<img
							src={previews[reviewing.id]}
							alt={$_('advertisingMedia.previewAlt', {
								values: { advertiser: reviewing.advertiser, campaign: reviewing.campaign }
							})}
							class="max-h-[70dvh] max-w-full object-contain"
							data-testid="media-review-image"
							onload={judged}
							onerror={judged}
						/>
					{/if}
				</div>
				<p class="text-xs text-muted-foreground" data-testid="media-review-details">
					{$_('advertisingMedia.dates', {
						values: { first: reviewing.startsOn, last: reviewing.endsOn }
					})}
					{' · '}
					{$_('advertisingMedia.size', {
						values: { width: reviewing.widthPixels, height: reviewing.heightPixels }
					})}
				</p>
				{#if reviewShown === 'failed'}
					<p class="text-sm text-destructive" role="alert" data-testid="media-review-failed">
						{$_('advertisingMedia.review.notShown')}
					</p>
				{/if}
				<div class="flex flex-wrap justify-end gap-2">
					<button
						type="button"
						data-testid="media-review-cancel"
						bind:this={reviewCancel}
						onclick={closeReview}
						class={buttonClass}
					>
						{$_('advertisingMedia.actions.cancel')}
					</button>
					<button
						type="button"
						data-testid="media-approve-confirm"
						disabled={busy || reviewShown !== 'shown'}
						onclick={approveReviewed}
						class="inline-flex h-9 items-center gap-1.5 rounded-md bg-primary px-3 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
					>
						<Check class="size-4" aria-hidden="true" />
						{$_('advertisingMedia.actions.approve')}
					</button>
				</div>
			</div>
		</dialog>
	{/if}
</section>
