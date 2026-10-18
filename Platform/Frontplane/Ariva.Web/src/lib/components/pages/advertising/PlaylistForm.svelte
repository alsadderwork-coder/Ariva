<script lang="ts">
	import { ArrowDown, ArrowUp, Plus, X } from '@lucide/svelte';
	import { onMount, untrack } from 'svelte';
	import { _ } from 'svelte-i18n';
	import * as media from '$lib/core/advertising-media';
	import type { AdvertisingMedia, MediaStatus } from '$lib/core/advertising-media';
	import * as plans from '$lib/core/advertising-plans';
	import type { ItemKind, Playlist, PlaylistRequest } from '$lib/core/advertising-plans';
	import * as displays from '$lib/core/displays';
	import type { Slide } from '$lib/core/slides';

	/**
	 * A playlist's name and its items in playing order (ARV-143a), built from the site's advertisement library (retired
	 * ones are not offered) and its active instruction slides, each named once. An image or a slide has its seconds on
	 * screen; a video plays for its own length, which the server sets. Save sends the whole list; the server checks every
	 * item again. Every word is shown as text.
	 */
	interface Props {
		/** The playlist being changed, or null for a new one. */
		editing: Playlist | null;
		library: AdvertisingMedia[];
		slides: Slide[];
		/** False when the user may not read the site's instruction slides: none are offered. */
		slidesReadable: boolean;
		onSubmit: (request: PlaylistRequest) => Promise<string[]>;
		onCancel: () => void;
	}

	let { editing, library, slides, slidesReadable, onSubmit, onCancel }: Props = $props();

	interface Entry {
		kind: ItemKind;
		id: string;
		label: string;
		seconds: number | string;
		weight: number | string;
		video: boolean;
		/** A video's length in seconds (null for an image or a slide). */
		length: number | null;
		playable: boolean;
		mediaStatus: MediaStatus | null;
	}

	const start = untrack(() => editing);
	let name = $state(start?.name ?? '');
	let entries = $state<Entry[]>(
		(start?.items ?? []).map((item) => {
			const video = item.kind === 'Media' && plans.isVideo(item.contentType);
			return {
				kind: item.kind,
				id: item.id,
				label: item.label ?? '',
				seconds: video ? plans.limits.defaultSeconds : item.seconds,
				weight: item.weight,
				video,
				length: video ? item.seconds : null,
				playable: item.playable,
				mediaStatus: item.mediaStatus
			};
		})
	);
	let mediaChoice = $state('');
	let slideChoice = $state('');
	let problems = $state<string[]>([]);
	let busy = $state(false);
	let nameInput = $state<HTMLInputElement | undefined>();

	const full = $derived(entries.length >= plans.limits.maxItems);
	const chosen = $derived(new Set(entries.map((e) => `${e.kind}:${e.id}`)));
	const offeredMedia = $derived(
		library.filter((m) => m.status !== 'Retired' && !chosen.has(`Media:${m.id}`))
	);
	const offeredSlides = $derived(
		slides.filter((s) => s.status === 'Active' && !chosen.has(`Slide:${s.id}`))
	);

	onMount(() => {
		// A playlist taken for a change brings its form into view with the name ready to edit.
		if (start && nameInput) {
			nameInput.scrollIntoView({ block: 'center' });
			nameInput.focus();
		}
	});

	/** A slide's title in the first language it has, in the board's order. */
	function slideTitle(slide: Slide): string {
		const language = displays.boardLanguages.find((l) => Object.hasOwn(slide.texts, l));
		return language ? slide.texts[language].title : '';
	}

	/** A video's length in whole seconds, rounded up as the server sets its slot. */
	function lengthOf(item: AdvertisingMedia): number | null {
		return item.durationMilliseconds === null ? null : Math.ceil(item.durationMilliseconds / 1000);
	}

	function mediaOption(item: AdvertisingMedia): string {
		const label = `${item.advertiser}: ${item.campaign}`;
		const status = $_(`advertisingMedia.status.${item.status}`);
		return media.isVideoType(item.contentType)
			? $_('advertisingPlans.playlists.form.videoOption', {
					values: { label, seconds: lengthOf(item) ?? 0, status }
				})
			: $_('advertisingPlans.playlists.form.mediaOption', { values: { label, status } });
	}

	function addMedia(): void {
		const item = offeredMedia.find((m) => m.id === mediaChoice);
		if (!item || full) return;
		const video = plans.isVideo(item.contentType);
		entries.push({
			kind: 'Media',
			id: item.id,
			label: `${item.advertiser}: ${item.campaign}`,
			seconds: plans.limits.defaultSeconds,
			weight: 1,
			video,
			length: video ? lengthOf(item) : null,
			playable: item.playable,
			mediaStatus: item.status
		});
		mediaChoice = '';
	}

	function addSlide(): void {
		const slide = offeredSlides.find((s) => s.id === slideChoice);
		if (!slide || full) return;
		entries.push({
			kind: 'Slide',
			id: slide.id,
			label: slideTitle(slide),
			seconds: slide.durationSeconds,
			weight: 1,
			video: false,
			length: null,
			playable: slide.status === 'Active',
			mediaStatus: null
		});
		slideChoice = '';
	}

	function move(index: number, by: -1 | 1): void {
		const target = index + by;
		if (target < 0 || target >= entries.length) return;
		const next = [...entries];
		[next[index], next[target]] = [next[target], next[index]];
		entries = next;
	}

	function remove(index: number): void {
		entries = [...entries.slice(0, index), ...entries.slice(index + 1)];
	}

	function notPlaying(entry: Entry): string {
		if (entry.kind === 'Slide') return $_('advertisingPlans.playlists.notPlaying.Slide');
		return entry.mediaStatus === 'Retired'
			? $_('advertisingPlans.playlists.notPlaying.Retired')
			: $_('advertisingPlans.playlists.notPlaying.Media');
	}

	function kindLabel(entry: Entry): string {
		if (entry.kind === 'Slide') return $_('advertisingPlans.playlists.kinds.Slide');
		return entry.video
			? $_('advertisingPlans.playlists.kinds.Video')
			: $_('advertisingPlans.playlists.kinds.Media');
	}

	async function submit(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		if (busy || entries.length === 0) return;
		busy = true;
		problems = await onSubmit({
			name: name.trim(),
			items: entries.map((e) =>
				e.video
					? { kind: e.kind, id: e.id, weight: Number(e.weight) }
					: { kind: e.kind, id: e.id, seconds: Number(e.seconds), weight: Number(e.weight) }
			)
		});
		busy = false;
	}

	const inputClass =
		'h-9 min-w-0 rounded-md border border-input bg-background px-2 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none';
	const smallButton =
		'inline-flex size-8 items-center justify-center rounded-md border bg-card hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-40';
	const buttonClass =
		'inline-flex h-9 items-center gap-1.5 rounded-md border bg-card px-2.5 text-sm hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60';
</script>

<!-- Every word is text: the labels come from the library and the slides, and are never read as markup. -->
<form
	class="flex flex-col gap-4 rounded-lg border p-4"
	data-testid="playlist-form"
	data-mode={editing ? 'edit' : 'create'}
	aria-labelledby="playlist-form-title"
	onsubmit={submit}
>
	<h3 id="playlist-form-title" class="text-sm font-semibold break-words">
		{editing
			? $_('advertisingPlans.playlists.form.editTitle', { values: { name: editing.name } })
			: $_('advertisingPlans.playlists.form.createTitle')}
	</h3>

	<div class="flex max-w-xl flex-col gap-1">
		<label for="advertising-playlist-name" class="text-xs font-medium"
			>{$_('advertisingPlans.playlists.form.name')}</label
		>
		<input
			id="advertising-playlist-name"
			data-testid="playlist-name"
			bind:this={nameInput}
			bind:value={name}
			required
			maxlength={plans.limits.maxName}
			autocomplete="off"
			class={inputClass}
		/>
	</div>

	<fieldset class="flex flex-col gap-2">
		<legend class="text-xs font-medium">{$_('advertisingPlans.playlists.form.items')}</legend>
		<p id="playlist-items-hint" class="text-xs text-muted-foreground">
			{$_('advertisingPlans.playlists.form.hint')}
		</p>
		{#if entries.length === 0}
			<p class="text-sm text-muted-foreground" data-testid="playlist-no-items">
				{$_('advertisingPlans.playlists.form.noItems')}
			</p>
		{:else}
			<ol class="flex flex-col divide-y rounded-md border" data-testid="playlist-items">
				{#each entries as entry, index (`${entry.kind}:${entry.id}`)}
					{@const position = index + 1}
					{@const suffix = $_('advertisingPlans.playlists.form.positionSuffix', {
						values: { position }
					})}
					<li
						class="flex flex-wrap items-center gap-3 px-3 py-2"
						data-testid="playlist-item"
						data-kind={entry.kind}
						data-id={entry.id}
					>
						<span class="w-6 font-mono text-xs tabular-nums">{position}</span>
						<div class="flex min-w-0 flex-1 flex-col">
							<span class="font-medium break-words" data-testid="playlist-item-label"
								>{entry.label || $_('advertisingPlans.playlists.unknownLabel')}</span
							>
							<span class="text-xs text-muted-foreground">{kindLabel(entry)}</span>
							{#if !entry.playable}
								<span class="text-xs text-muted-foreground" data-testid="item-not-playing"
									>{notPlaying(entry)}</span
								>
							{/if}
						</div>
						{#if entry.video}
							<span class="text-xs text-muted-foreground tabular-nums" data-testid="item-length"
								>{$_('advertisingPlans.playlists.form.videoLength', {
									values: { seconds: entry.length ?? 0 }
								})}</span
							>
						{:else}
							<label class="flex flex-col gap-0.5 text-xs font-medium">
								<span
									>{$_('advertisingPlans.playlists.form.seconds')}<span class="sr-only"
										>{suffix}</span
									></span
								>
								<input
									type="number"
									data-testid="item-seconds"
									bind:value={entry.seconds}
									required
									min={plans.limits.minSeconds}
									max={plans.limits.maxSeconds}
									step="1"
									class="{inputClass} w-20 tabular-nums"
								/>
							</label>
						{/if}
						<label class="flex flex-col gap-0.5 text-xs font-medium">
							<span
								>{$_('advertisingPlans.playlists.form.weight')}<span class="sr-only">{suffix}</span
								></span
							>
							<input
								type="number"
								data-testid="item-weight"
								bind:value={entry.weight}
								required
								min={plans.limits.minWeight}
								max={plans.limits.maxWeight}
								step="1"
								class="{inputClass} w-16 tabular-nums"
							/>
						</label>
						<span class="flex gap-1">
							<button
								type="button"
								data-testid="item-up"
								disabled={index === 0}
								aria-label={$_('advertisingPlans.playlists.form.up', { values: { position } })}
								onclick={() => move(index, -1)}
								class={smallButton}
							>
								<ArrowUp class="size-3.5" aria-hidden="true" />
							</button>
							<button
								type="button"
								data-testid="item-down"
								disabled={index === entries.length - 1}
								aria-label={$_('advertisingPlans.playlists.form.down', { values: { position } })}
								onclick={() => move(index, 1)}
								class={smallButton}
							>
								<ArrowDown class="size-3.5" aria-hidden="true" />
							</button>
							<button
								type="button"
								data-testid="item-remove"
								aria-label={$_('advertisingPlans.playlists.form.remove', { values: { position } })}
								onclick={() => remove(index)}
								class={smallButton}
							>
								<X class="size-3.5" aria-hidden="true" />
							</button>
						</span>
					</li>
				{/each}
			</ol>
		{/if}
		{#if full}
			<p class="text-xs text-status-warning-foreground" data-testid="playlist-full">
				{$_('advertisingPlans.playlists.form.full')}
			</p>
		{/if}

		<div class="grid gap-3 md:grid-cols-2">
			<div class="flex min-w-0 flex-col gap-1">
				<label for="advertising-playlist-media" class="text-xs font-medium"
					>{$_('advertisingPlans.playlists.form.media')}</label
				>
				<span class="flex min-w-0 gap-2">
					<select
						id="advertising-playlist-media"
						data-testid="playlist-media-choice"
						bind:value={mediaChoice}
						disabled={offeredMedia.length === 0}
						aria-describedby={offeredMedia.length === 0
							? 'advertising-playlist-media-none'
							: undefined}
						class="{inputClass} flex-1"
					>
						<option value="">{$_('advertisingPlans.playlists.form.chooseMedia')}</option>
						{#each offeredMedia as item (item.id)}
							<option value={item.id}>{mediaOption(item)}</option>
						{/each}
					</select>
					<button
						type="button"
						data-testid="playlist-add-media"
						disabled={!mediaChoice || full}
						onclick={addMedia}
						class={buttonClass}
					>
						<Plus class="size-4" aria-hidden="true" />
						{$_('advertisingPlans.playlists.form.add')}
					</button>
				</span>
				{#if offeredMedia.length === 0}
					<p id="advertising-playlist-media-none" class="text-xs text-muted-foreground">
						{$_('advertisingPlans.playlists.form.noMedia')}
					</p>
				{/if}
			</div>
			<div class="flex min-w-0 flex-col gap-1">
				<label for="advertising-playlist-slide" class="text-xs font-medium"
					>{$_('advertisingPlans.playlists.form.slide')}</label
				>
				<span class="flex min-w-0 gap-2">
					<select
						id="advertising-playlist-slide"
						data-testid="playlist-slide-choice"
						bind:value={slideChoice}
						disabled={offeredSlides.length === 0}
						aria-describedby={offeredSlides.length === 0
							? 'advertising-playlist-slide-none'
							: undefined}
						class="{inputClass} flex-1"
					>
						<option value="">{$_('advertisingPlans.playlists.form.chooseSlide')}</option>
						{#each offeredSlides as slide (slide.id)}
							<option value={slide.id}>{slideTitle(slide)}</option>
						{/each}
					</select>
					<button
						type="button"
						data-testid="playlist-add-slide"
						disabled={!slideChoice || full}
						onclick={addSlide}
						class={buttonClass}
					>
						<Plus class="size-4" aria-hidden="true" />
						{$_('advertisingPlans.playlists.form.add')}
					</button>
				</span>
				{#if offeredSlides.length === 0}
					<p id="advertising-playlist-slide-none" class="text-xs text-muted-foreground">
						{slidesReadable
							? $_('advertisingPlans.playlists.form.noSlides')
							: $_('advertisingPlans.playlists.form.slidesHidden')}
					</p>
				{/if}
			</div>
		</div>
	</fieldset>

	{#if problems.length > 0}
		<ul
			class="list-disc ps-5 text-xs text-destructive"
			role="alert"
			data-testid="playlist-problems"
		>
			{#each problems as problem, index (index)}
				<li>{problem}</li>
			{/each}
		</ul>
	{/if}

	<div class="flex flex-wrap gap-2">
		<button
			type="submit"
			data-testid="playlist-save"
			disabled={busy || entries.length === 0}
			class="inline-flex h-9 items-center rounded-md bg-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
		>
			{editing
				? $_('advertisingPlans.playlists.form.save')
				: $_('advertisingPlans.playlists.form.create')}
		</button>
		{#if editing}
			<button type="button" data-testid="playlist-cancel" onclick={onCancel} class={buttonClass}>
				{$_('advertisingPlans.playlists.form.cancel')}
			</button>
		{/if}
	</div>
</form>
