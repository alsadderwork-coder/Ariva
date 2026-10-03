<script lang="ts">
	import { CircleCheck, ShieldCheck } from '@lucide/svelte';
	import { _ } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import * as zones from '$lib/core/zones';
	import type { Validation } from '$lib/core/zones';

	interface Props {
		profileId: string;
		canPublish: boolean;
		/** Shapes moved on the plan but not saved: what is shown is not what would be published. */
		unsaved: boolean;
		/** Bumped by the page after every change, so a stale check is not published. */
		revision: number;
		onPublished: (id: string) => void | Promise<void>;
	}

	let { profileId, canPublish, unsaved, revision, onPublished }: Props = $props();

	let validation = $state<Validation | null>(null);
	let checkedAt = $state(-1);
	let busy = $state(false);

	const current = $derived(validation !== null && checkedAt === revision);

	async function check(): Promise<void> {
		busy = true;
		const result = await zones.validate(profileId);
		busy = false;
		if (result.hasErrors || !result.data) {
			toast.error(
				$_('topology.messages.failed', { values: { reason: result.errorMessages[0] ?? '' } })
			);
			return;
		}
		validation = result.data;
		checkedAt = revision;
	}

	// The geometry hash the user reviewed goes with the request; a draft changed since is refused by the server.
	async function publish(): Promise<void> {
		if (!validation || busy) return;
		busy = true;
		const result = await zones.publish(profileId, validation.geometryHash);
		busy = false;
		if (result.hasErrors || !result.data) {
			toast.error(
				$_('topology.messages.failed', { values: { reason: result.errorMessages[0] ?? '' } })
			);
			return;
		}
		toast.success($_('zones.published', { values: { version: result.data.version ?? '' } }));
		await onPublished(result.data.id);
	}
</script>

<section
	data-testid="publish-panel"
	aria-labelledby="publish-title"
	class="flex flex-col gap-3 rounded-xl border bg-card p-4"
>
	<h2 id="publish-title" class="text-base font-semibold">{$_('zones.publishTitle')}</h2>
	<p class="text-xs text-muted-foreground">{$_('zones.publishHint')}</p>
	<button
		type="button"
		data-testid="validate"
		disabled={busy}
		onclick={check}
		class="inline-flex h-9 items-center justify-center gap-2 rounded-md border bg-card px-3 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
	>
		<CircleCheck class="size-4" aria-hidden="true" />
		{$_('zones.check')}
	</button>
	{#if validation && current}
		{#if validation.problems.length}
			<ul
				role="alert"
				data-testid="validation-problems"
				class="flex flex-col gap-1 rounded-md border border-status-warning-border bg-status-warning px-3 py-2 text-sm text-status-warning-foreground"
			>
				{#each validation.problems as problem, index (index)}
					<li>{problem}</li>
				{/each}
			</ul>
		{:else}
			<p
				data-testid="validation-ok"
				class="rounded-md border border-status-success-border bg-status-success px-3 py-2 text-sm text-status-success-foreground"
			>
				{$_('zones.ready')}
			</p>
		{/if}
		{#if canPublish && validation.publishable && unsaved}
			<p data-testid="unsaved" class="text-xs text-status-warning-foreground">
				{$_('zones.unsaved')}
			</p>
		{:else if canPublish && validation.publishable}
			<button
				type="button"
				data-testid="publish"
				disabled={busy}
				onclick={publish}
				class="inline-flex h-9 items-center justify-center gap-2 rounded-md bg-button-primary px-3 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60 dark:text-white"
			>
				<ShieldCheck class="size-4" aria-hidden="true" />
				{$_('zones.publish')}
			</button>
			<p class="text-xs text-muted-foreground">{$_('zones.publishStepUp')}</p>
		{/if}
	{:else if validation}
		<p class="text-xs text-muted-foreground">{$_('zones.checkAgain')}</p>
	{/if}
</section>
