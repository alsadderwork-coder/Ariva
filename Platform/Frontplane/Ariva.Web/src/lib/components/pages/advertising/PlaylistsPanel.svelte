<script lang="ts">
	import { ListVideo, Pencil, RotateCcw } from '@lucide/svelte';
	import { _ } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import ConfirmButton from '$lib/components/shared/ConfirmButton.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import type { AdvertisingMedia } from '$lib/core/advertising-media';
	import * as plans from '$lib/core/advertising-plans';
	import type { Playlist, PlaylistItem, PlaylistRequest } from '$lib/core/advertising-plans';
	import type { Slide } from '$lib/core/slides';
	import PlaylistForm from './PlaylistForm.svelte';

	/**
	 * The site's playlists (ARV-143a): each with its state, its items and the length of one pass; a retired one is kept and
	 * can be restored, and its schedules stop applying meanwhile. Holders of Advertising.Manage create and change them in
	 * the form below the list, and retire them after a second click on the page. The server decides every change.
	 */
	interface Props {
		siteCode: string;
		playlists: Playlist[];
		library: AdvertisingMedia[];
		slides: Slide[];
		slidesReadable: boolean;
		canManage: boolean;
		/** Reads the lists again after a change. */
		onChanged: () => Promise<void>;
		/** A call answered 403: the site's advertising policy is off now. */
		onRefused: () => void;
	}

	let {
		siteCode,
		playlists,
		library,
		slides,
		slidesReadable,
		canManage,
		onChanged,
		onRefused
	}: Props = $props();

	let editing = $state<Playlist | null>(null);
	let formKey = $state(0);
	let busy = $state(false);

	function failed(reason: string): void {
		toast.error($_('advertisingPlans.messages.failed', { values: { reason } }));
	}

	function startEdit(playlist: Playlist): void {
		editing = playlist;
		formKey++;
	}

	function stopEdit(): void {
		editing = null;
		formKey++;
	}

	async function submit(request: PlaylistRequest): Promise<string[]> {
		const changing = editing;
		const result = changing
			? await plans.updatePlaylist(siteCode, changing.id, request)
			: await plans.createPlaylist(siteCode, request);
		if (result.hasErrors || !result.data) {
			if (result.status === 403) {
				onRefused();
				return [];
			}
			return result.errorMessages;
		}
		toast.success(
			$_(
				changing
					? 'advertisingPlans.messages.playlistSaved'
					: 'advertisingPlans.messages.playlistCreated'
			)
		);
		stopEdit();
		await onChanged();
		return [];
	}

	async function setStatus(playlist: Playlist, retire: boolean): Promise<void> {
		if (busy) return;
		busy = true;
		const result = retire
			? await plans.retirePlaylist(siteCode, playlist.id)
			: await plans.restorePlaylist(siteCode, playlist.id);
		busy = false;
		if (result.hasErrors) {
			if (result.status === 403) return onRefused();
			failed(result.errorMessages.join(' '));
			return;
		}
		if (editing?.id === playlist.id) stopEdit();
		toast.success(
			$_(
				retire
					? 'advertisingPlans.messages.playlistRetired'
					: 'advertisingPlans.messages.playlistRestored'
			)
		);
		await onChanged();
	}

	function kindOf(item: PlaylistItem): string {
		if (item.kind === 'Slide') return $_('advertisingPlans.playlists.kinds.Slide');
		return plans.isVideo(item.contentType)
			? $_('advertisingPlans.playlists.kinds.Video')
			: $_('advertisingPlans.playlists.kinds.Media');
	}

	function notPlaying(item: PlaylistItem): string {
		if (item.kind === 'Slide') return $_('advertisingPlans.playlists.notPlaying.Slide');
		return item.mediaStatus === 'Retired'
			? $_('advertisingPlans.playlists.notPlaying.Retired')
			: $_('advertisingPlans.playlists.notPlaying.Media');
	}

	const buttonClass =
		'inline-flex h-9 items-center gap-1.5 rounded-md border bg-card px-2.5 text-sm hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60';
</script>

<section
	class="rounded-xl border bg-card"
	aria-labelledby="advertising-playlists-title"
	data-testid="playlists"
>
	<div class="border-b px-4 py-3">
		<h2 id="advertising-playlists-title" class="flex items-center gap-2 text-base font-semibold">
			<ListVideo class="size-4" aria-hidden="true" />
			{$_('advertisingPlans.playlists.title')}
		</h2>
		<p class="text-xs text-muted-foreground">{$_('advertisingPlans.playlists.description')}</p>
	</div>
	<div class="flex flex-col gap-4 px-4 py-3 text-sm">
		{#if playlists.length === 0}
			<p class="text-muted-foreground" data-testid="playlists-empty">
				{$_('advertisingPlans.playlists.none')}
			</p>
		{:else}
			<div class="overflow-x-auto rounded-lg border">
				<table class="w-full text-sm" data-testid="playlists-table">
					<thead class="bg-surface-2 text-xs font-semibold tracking-wide text-tertiary uppercase">
						<tr>
							<th class="px-4 py-2 text-start">{$_('advertisingPlans.playlists.columns.name')}</th>
							<th class="px-4 py-2 text-start">{$_('advertisingPlans.playlists.columns.status')}</th
							>
							<th class="px-4 py-2 text-start">{$_('advertisingPlans.playlists.columns.items')}</th>
							<th class="px-4 py-2 text-start">{$_('advertisingPlans.playlists.columns.length')}</th
							>
							<th class="px-4 py-2 text-end"
								><span class="sr-only">{$_('advertisingPlans.playlists.columns.actions')}</span></th
							>
						</tr>
					</thead>
					<tbody>
						{#each playlists as playlist (playlist.id)}
							{@const silent = playlist.items.filter((i) => !i.playable).length}
							<tr
								class="border-t align-top odd:bg-surface even:bg-surface-2/50"
								data-testid="playlist-row"
								data-id={playlist.id}
								data-status={playlist.status}
							>
								<td class="px-4 py-3">
									<div class="flex min-w-0 flex-col gap-1">
										<span class="font-medium break-words" data-testid="playlist-row-name"
											>{playlist.name}</span
										>
										{#if silent > 0}
											<span class="text-xs text-muted-foreground" data-testid="playlist-row-silent"
												>{$_('advertisingPlans.playlists.notPlayingCount', {
													values: { count: silent }
												})}</span
											>
										{/if}
										<details class="text-xs">
											<summary
												class="cursor-pointer text-muted-foreground hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
												>{$_('advertisingPlans.playlists.showItems')}</summary
											>
											<ol class="mt-1 flex list-decimal flex-col gap-1 ps-5">
												{#each playlist.items as item (`${item.kind}:${item.id}`)}
													<li data-testid="playlist-row-item" data-kind={item.kind}>
														<span class="break-words"
															>{item.label ?? $_('advertisingPlans.playlists.unknownLabel')}</span
														>
														<span class="text-muted-foreground">
															{$_('advertisingPlans.playlists.itemSummary', {
																values: {
																	kind: kindOf(item),
																	seconds: item.seconds,
																	weight: item.weight
																}
															})}</span
														>
														{#if !item.playable}
															<span class="block text-muted-foreground">{notPlaying(item)}</span>
														{/if}
													</li>
												{/each}
											</ol>
										</details>
									</div>
								</td>
								<td class="px-4 py-3">
									<StatusBadge
										tone={playlist.status === 'Active' ? 'success' : 'neutral'}
										label={$_(`advertisingPlans.playlists.status.${playlist.status}`)}
									/>
								</td>
								<td class="px-4 py-3 tabular-nums" data-testid="playlist-row-count"
									>{$_('advertisingPlans.playlists.itemCount', {
										values: { count: playlist.items.length }
									})}</td
								>
								<td class="px-4 py-3 tabular-nums" data-testid="playlist-row-seconds"
									>{$_('advertisingPlans.playlists.totalSeconds', {
										values: { seconds: playlist.totalSeconds }
									})}</td
								>
								<td class="px-4 py-3">
									{#if canManage}
										<span class="flex flex-wrap justify-end gap-2">
											{#if playlist.status === 'Active'}
												<button
													type="button"
													data-testid="playlist-edit"
													disabled={busy}
													onclick={() => startEdit(playlist)}
													class={buttonClass}
												>
													<Pencil class="size-4" aria-hidden="true" />
													{$_('advertisingPlans.playlists.actions.edit')}
												</button>
												<ConfirmButton
													label={$_('advertisingPlans.playlists.actions.retire')}
													confirmLabel={$_('advertisingPlans.playlists.actions.confirmRetire')}
													testId="playlist-retire"
													disabled={busy}
													onConfirm={() => setStatus(playlist, true)}
												/>
											{:else}
												<button
													type="button"
													data-testid="playlist-restore"
													disabled={busy}
													onclick={() => setStatus(playlist, false)}
													class={buttonClass}
												>
													<RotateCcw class="size-4" aria-hidden="true" />
													{$_('advertisingPlans.playlists.actions.restore')}
												</button>
											{/if}
										</span>
									{/if}
								</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		{/if}

		{#if canManage}
			{#key formKey}
				<PlaylistForm
					{editing}
					{library}
					{slides}
					{slidesReadable}
					onSubmit={submit}
					onCancel={stopEdit}
				/>
			{/key}
		{/if}
	</div>
</section>
