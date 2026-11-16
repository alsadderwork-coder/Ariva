<script lang="ts">
	import { Crosshair, Hand, Maximize, Minimize, Minus, Plus, Scan } from '@lucide/svelte';
	import { _ } from 'svelte-i18n';
	import { cn } from '$lib/utils';
	import type { PlanView } from './plan-view.svelte';

	interface Props {
		view: PlanView;
		/** The toolbar's accessible name. */
		label: string;
		/** The element that goes full screen: the plan's wrapper. */
		target: HTMLElement | undefined;
		/** Zoom to the selection: the tool is shown when this is given. */
		focus?: { disabled: boolean; run: () => void } | null;
		/** The hand tool (drag anywhere pans): shown when bound to a boolean. */
		hand?: boolean | null;
		/** Floating over the plan's corner (the zones editor) or docked in a bar above it (the live plan). */
		docked?: boolean;
		testId?: string;
	}

	let {
		view,
		label,
		target,
		focus = null,
		hand = $bindable(null),
		docked = false,
		testId
	}: Props = $props();

	$effect(() => {
		view.canFullscreen = document.fullscreenEnabled === true;
		const changed = () =>
			(view.fullscreen =
				document.fullscreenElement !== null && document.fullscreenElement === target);
		document.addEventListener('fullscreenchange', changed);
		return () => document.removeEventListener('fullscreenchange', changed);
	});

	async function toggleFullscreen(): Promise<void> {
		if (!target) return;
		if (document.fullscreenElement) await document.exitFullscreen().catch(() => undefined);
		else await target.requestFullscreen().catch(() => undefined);
	}

	const toolClass =
		'inline-flex size-8 items-center justify-center rounded-md text-foreground hover:bg-muted focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:pointer-events-none disabled:opacity-40';
</script>

<!-- The view tools of a floor plan (ARV-298, shared by ARV-299a): zoom out, the zoom level, zoom in, fit the level,
     zoom to the selection, the hand tool and full screen. The level itself is text. -->
<div
	role="toolbar"
	aria-label={label}
	data-testid={testId}
	class={cn(
		'flex items-center gap-0.5',
		!docked && 'absolute start-3 top-3 rounded-lg border bg-card/95 p-1 shadow-sm backdrop-blur'
	)}
>
	<button
		type="button"
		data-testid="zoom-out"
		class={toolClass}
		aria-label={$_('planView.zoomOut')}
		title={$_('planView.zoomOut')}
		disabled={!view.canZoomOut}
		onclick={() => view.zoomBy(0.8)}
	>
		<Minus class="size-4" aria-hidden="true" />
	</button>
	<output
		data-testid="zoom-level"
		class="min-w-12 text-center text-xs font-medium tabular-nums"
		aria-live="polite"
		aria-label={$_('planView.zoomLevel')}>{view.zoomPercent}%</output
	>
	<button
		type="button"
		data-testid="zoom-in"
		class={toolClass}
		aria-label={$_('planView.zoomIn')}
		title={$_('planView.zoomIn')}
		disabled={!view.canZoomIn}
		onclick={() => view.zoomBy(1.25)}
	>
		<Plus class="size-4" aria-hidden="true" />
	</button>
	<span class="mx-1 h-5 w-px bg-border" aria-hidden="true"></span>
	<button
		type="button"
		data-testid="zoom-fit"
		class={toolClass}
		aria-label={$_('planView.fit')}
		title={$_('planView.fit')}
		onclick={() => view.fit()}
	>
		<Scan class="size-4" aria-hidden="true" />
	</button>
	{#if focus}
		<button
			type="button"
			data-testid="zoom-selection"
			class={toolClass}
			aria-label={$_('planView.focus')}
			title={$_('planView.focus')}
			disabled={focus.disabled}
			onclick={focus.run}
		>
			<Crosshair class="size-4" aria-hidden="true" />
		</button>
	{/if}
	{#if hand !== null}
		<button
			type="button"
			data-testid="pan-tool"
			class={cn(toolClass, hand && 'bg-primary/10 text-primary')}
			aria-label={$_('planView.pan')}
			aria-pressed={hand}
			title={$_('planView.pan')}
			onclick={() => (hand = !hand)}
		>
			<Hand class="size-4" aria-hidden="true" />
		</button>
	{/if}
	{#if view.canFullscreen}
		<button
			type="button"
			data-testid="zoom-fullscreen"
			class={toolClass}
			aria-label={view.fullscreen ? $_('planView.exitFullscreen') : $_('planView.fullscreen')}
			title={view.fullscreen ? $_('planView.exitFullscreen') : $_('planView.fullscreen')}
			onclick={toggleFullscreen}
		>
			{#if view.fullscreen}
				<Minimize class="size-4" aria-hidden="true" />
			{:else}
				<Maximize class="size-4" aria-hidden="true" />
			{/if}
		</button>
	{/if}
</div>
