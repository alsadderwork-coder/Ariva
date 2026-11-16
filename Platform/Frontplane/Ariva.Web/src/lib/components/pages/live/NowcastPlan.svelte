<script lang="ts">
	import { _ } from 'svelte-i18n';
	import PlanScale from '$lib/components/shared/PlanScale.svelte';
	import PlanTools from '$lib/components/shared/PlanTools.svelte';
	import { PlanView, boundsOf } from '$lib/components/shared/plan-view.svelte';
	import type { ZoneSnapshot } from '$lib/core/Live.svelte';
	import type { Point } from '$lib/core/zones';
	import { cn } from '$lib/utils';
	import { waitStatus, type WaitStatus } from './waits';

	type Gesture =
		| { kind: 'pan'; last: Point; travelled: number; zone: string | null }
		| { kind: 'pinch'; distance: number; middle: Point }
		| null;

	interface Props {
		width: number;
		depth: number;
		zones: { key: string; name: string; points: Point[] }[];
		/** The latest snapshot of each zone, by zone key. */
		snapshots: ReadonlyMap<string, ZoneSnapshot>;
		selected: string | null;
		plan?: { url: string; x: number; y: number; width: number; height: number } | null;
		now: number;
		onSelect: (key: string) => void;
	}

	let { width, depth, zones, snapshots, selected, plan = null, now, onSelect }: Props = $props();

	/** A pointer that travels less than this many pixels selects the zone under it rather than panning. */
	const SLOP = 4;

	// The zones editor's view (ARV-298), shared by ARV-299a: zoom, pan, fit and zoom to the selected zone, with a
	// little room around the fitted level.
	const view = new PlanView(() => ({ width, depth }), 12);
	let wrapper = $state<HTMLDivElement>();
	let svg = $state<SVGSVGElement>();
	let gesture = $state<Gesture>(null);
	/** The pointers on the plan (touch pinch); not reactive, read only in handlers. */
	const pointers = new Map<number, Point>();

	const fill: Record<WaitStatus, string> = {
		withinTarget: 'fill-status-success/50 stroke-status-success-solid',
		nearTarget: 'fill-status-warning/55 stroke-status-warning-solid',
		overTarget: 'fill-status-danger/55 stroke-status-danger-solid',
		degraded: 'fill-status-neutral/60 stroke-status-neutral-foreground',
		noEstimate: 'fill-status-neutral/45 stroke-status-neutral-foreground',
		noData: 'fill-status-neutral/30 stroke-status-neutral-foreground',
		notMeasured: 'fill-status-neutral/15 stroke-status-neutral-foreground'
	};

	/** The selected zone is drawn last, on top, so its outline is never hidden by a neighbour. */
	const ordered = $derived(
		selected
			? [...zones.filter((z) => z.key !== selected), ...zones.filter((z) => z.key === selected)]
			: zones
	);
	const selectedZone = $derived(zones.find((z) => z.key === selected) ?? null);

	// On the live screen a plain wheel scrolls the page, as a dashboard should; Ctrl or Cmd and the wheel (and a trackpad
	// pinch) zoom. In full screen there is no page to scroll, so the wheel zooms.
	$effect(() => {
		const element = svg;
		if (!element) return;
		return view.listen(element, () => !view.fullscreen);
	});

	function centre(points: Point[]): Point {
		return {
			x: points.reduce((s, p) => s + p.x, 0) / points.length,
			y: points.reduce((s, p) => s + p.y, 0) / points.length
		};
	}

	function capture(event: PointerEvent): void {
		try {
			svg?.setPointerCapture(event.pointerId);
		} catch {
			// A pointer that is already gone cannot be captured; its events end the gesture anyway.
		}
	}

	/** A press pans the view, or with two fingers zooms; a press that barely moves selects the zone under it. */
	function down(event: PointerEvent): void {
		if (event.pointerType === 'mouse' && event.button !== 0 && event.button !== 1) return;
		if (event.pointerType === 'mouse') event.preventDefault();
		capture(event);
		pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
		if (pointers.size === 2) {
			const [a, b] = [...pointers.values()];
			gesture = {
				kind: 'pinch',
				distance: Math.hypot(a.x - b.x, a.y - b.y),
				middle: { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 }
			};
			return;
		}
		gesture = {
			kind: 'pan',
			last: { x: event.clientX, y: event.clientY },
			travelled: 0,
			// Only the primary button (or a finger) selects; the middle button only pans.
			zone: event.button === 0 ? (event.target as Element).getAttribute('data-zone-key') : null
		};
	}

	function move(event: PointerEvent): void {
		if (pointers.has(event.pointerId))
			pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
		if (gesture?.kind === 'pinch' && pointers.size >= 2 && svg) {
			const [a, b] = [...pointers.values()];
			const distance = Math.hypot(a.x - b.x, a.y - b.y);
			const middle = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
			const rect = svg.getBoundingClientRect();
			view.panBy(middle.x - gesture.middle.x, middle.y - gesture.middle.y);
			if (gesture.distance > 0)
				view.zoomAt(middle.x - rect.left, middle.y - rect.top, distance / gesture.distance);
			gesture = { kind: 'pinch', distance, middle };
			return;
		}
		if (gesture?.kind !== 'pan') return;
		const dx = event.clientX - gesture.last.x;
		const dy = event.clientY - gesture.last.y;
		const travelled = gesture.travelled + Math.abs(dx) + Math.abs(dy);
		// The view starts to move once the pointer has left the slop, so a click never nudges it.
		if (travelled >= SLOP) view.panBy(dx, dy);
		gesture = {
			...gesture,
			last: travelled >= SLOP ? { x: event.clientX, y: event.clientY } : gesture.last,
			travelled
		};
	}

	function up(event: PointerEvent): void {
		pointers.delete(event.pointerId);
		if (gesture?.kind === 'pinch') {
			// One finger left: it pans from where it is, and never selects.
			const [rest] = [...pointers.values()];
			gesture = rest ? { kind: 'pan', last: rest, travelled: SLOP, zone: null } : null;
			return;
		}
		if (gesture?.kind === 'pan' && gesture.travelled < SLOP && event.type === 'pointerup') {
			// The key read from the page becomes the selection only when it names a zone of this plan.
			const pressed = gesture.zone;
			const zone = pressed ? zones.find((z) => z.key === pressed) : undefined;
			if (zone) onSelect(zone.key);
		}
		gesture = null;
	}
</script>

<!-- The live floor plan on the zones editor's view (ARV-299a): each zone shows its name and wait as text, and the zones
     table beside it carries the same figures and is the keyboard way to select a zone. The tools zoom, fit, zoom to the
     selected zone and go full screen; + and - zoom and 0 fits while the focus is on them. -->
<!-- svelte-ignore a11y_no_static_element_interactions -->
<div
	bind:this={wrapper}
	data-testid="nowcast-view"
	class={cn(
		'flex flex-col overflow-hidden border bg-surface-2',
		view.fullscreen ? 'h-screen w-screen rounded-none' : 'w-full rounded-lg'
	)}
	onkeydown={(event) => view.keys(event)}
>
	<!-- The tools and the scale sit in a bar above the plan, so they never cover a zone of a fitted level. -->
	<div
		class="flex min-h-11 flex-wrap items-center justify-between gap-2 border-b bg-card px-1.5 py-1"
	>
		<PlanTools
			{view}
			label={$_('liveOperations.planTools')}
			target={wrapper}
			testId="nowcast-tools"
			docked
			focus={{
				disabled: !selectedZone || selectedZone.points.length === 0,
				run: () => selectedZone && view.focus(boundsOf(selectedZone.points))
			}}
		/>
		<PlanScale {view} docked />
	</div>
	<div
		bind:clientWidth={view.cw}
		bind:clientHeight={view.ch}
		class={cn('relative w-full', view.fullscreen ? 'min-h-0 flex-1' : 'max-h-[70vh] min-h-56')}
		style:aspect-ratio={view.fullscreen ? undefined : `${width} / ${depth}`}
	>
		<svg
			bind:this={svg}
			data-testid="nowcast-plan"
			data-zoom={view.zoom}
			role="group"
			aria-label={$_('liveOperations.planLabel')}
			viewBox="{view.box.x} {view.box.y} {view.box.w} {view.box.h}"
			preserveAspectRatio="xMidYMid meet"
			class={cn(
				'absolute inset-0 size-full select-none',
				// A touch on the fitted plan scrolls the page; zoomed in or in full screen, one finger pans the plan.
				view.zoom > 1 || view.fullscreen ? 'touch-none' : 'touch-pan-y',
				gesture?.kind === 'pan' && gesture.travelled >= SLOP ? 'cursor-grabbing' : 'cursor-grab'
			)}
			onpointerdown={down}
			onpointermove={move}
			onpointerup={up}
			onpointercancel={up}
		>
			<rect
				x="0"
				y="0"
				{width}
				height={depth}
				class="fill-card stroke-border"
				stroke-width="1"
				vector-effect="non-scaling-stroke"
			/>
			{#if plan}
				<image
					href={plan.url}
					x={plan.x}
					y={plan.y}
					width={plan.width}
					height={plan.height}
					preserveAspectRatio="none"
					opacity="0.7"
				/>
			{/if}
			{#each ordered as zone (zone.key)}
				{@const snapshot = snapshots.get(zone.key)}
				{@const status = waitStatus(snapshot, now)}
				{@const middle = centre(zone.points)}
				{@const extent = boundsOf(zone.points)}
				{@const wide = (extent.maxX - extent.minX) * view.scale}
				{@const tall = (extent.maxY - extent.minY) * view.scale}
				<!-- A zone shows its name and wait when it is large enough on screen to hold them, its wait alone when it holds
			     one line, and neither when smaller: zooming in reveals them; the title and the table always carry them. -->
				{@const both = view.scale <= 0 || (tall >= 34 && wide >= 32)}
				{@const waitOnly = !both && tall >= 18 && wide >= 22}
				<g data-testid="plan-zone" data-zone={zone.name} data-status={status}>
					<polygon
						points={zone.points.map((p) => `${p.x},${p.y}`).join(' ')}
						class={cn(fill[status], 'cursor-pointer', selected === zone.key && 'stroke-foreground')}
						stroke-dasharray={status === 'notMeasured' ? '6 4' : undefined}
						stroke-width={selected === zone.key ? 3 : 1.5}
						vector-effect="non-scaling-stroke"
						role="button"
						tabindex="-1"
						aria-label={zone.name}
						aria-pressed={selected === zone.key}
						data-zone-key={zone.key}
					>
						<title
							>{status === 'notMeasured'
								? `${zone.name}: ${$_('liveOperations.notMeasuredHint')}`
								: zone.name}</title
						>
					</polygon>
					<!-- Labels keep their size on screen at any zoom (ARV-299a). -->
					<text
						x={middle.x}
						y={middle.y - 5 * view.pixel}
						font-size={12 * view.pixel}
						text-anchor="middle"
						class={cn('pointer-events-none fill-foreground', !both && 'hidden')}>{zone.name}</text
					>
					<text
						x={middle.x}
						y={middle.y + (both ? 13 : 6) * view.pixel}
						font-size={16 * view.pixel}
						text-anchor="middle"
						class={cn(
							'pointer-events-none fill-foreground font-semibold tabular-nums',
							(status === 'notMeasured' || (!both && !waitOnly)) && 'hidden'
						)}
					>
						{snapshot?.nowcastMinutes != null
							? `${Math.round(snapshot.nowcastMinutes)}\u2032`
							: '\u00b7'}
					</text>
					{#if status === 'notMeasured'}
						<!-- ARV-259e2: a zone without measurement says so in place of its wait. -->
						<text
							data-testid="plan-not-measured"
							x={middle.x}
							y={middle.y + (both ? 11 : 4) * view.pixel}
							font-size={12 * view.pixel}
							text-anchor="middle"
							class={cn(
								'pointer-events-none fill-muted-foreground',
								!both && !waitOnly && 'hidden'
							)}>{$_('liveOperations.status.notMeasured')}</text
						>
					{/if}
				</g>
			{/each}
		</svg>

		{#if view.modifierHint}
			<p
				data-testid="plan-modifier-hint"
				class="pointer-events-none absolute inset-x-0 top-1/2 mx-auto w-fit max-w-[80%] -translate-y-1/2 rounded-md bg-foreground/85 px-3 py-1.5 text-center text-sm text-background shadow-sm"
				aria-hidden="true"
			>
				{$_('planView.modifierHint')}
			</p>
		{/if}
	</div>
</div>
<p class="text-xs text-muted-foreground">{$_('liveOperations.planHint')}</p>
