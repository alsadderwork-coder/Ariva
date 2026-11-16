<script lang="ts" module>
	export type Selected = { kind: 'zone' | 'line'; id: string } | null;
	/** An offset in metres. */
	export type Offset = { dx: number; dy: number };
</script>

<script lang="ts">
	import { _ } from 'svelte-i18n';
	import PlanGrid from '$lib/components/shared/PlanGrid.svelte';
	import PlanScale from '$lib/components/shared/PlanScale.svelte';
	import PlanTools from '$lib/components/shared/PlanTools.svelte';
	import { PlanView, boundsOf, type Bounds } from '$lib/components/shared/plan-view.svelte';
	import type { Line, Point, Zone } from '$lib/core/zones';
	import { round } from '$lib/core/zones';
	import { cn } from '$lib/utils';

	type Drag =
		| { kind: 'vertex'; zoneId: string; index: number }
		| { kind: 'line'; lineId: string; end: 'start' | 'end' }
		| {
				kind: 'zone';
				zoneId: string;
				origin: Point;
				bounds: Bounds;
				at: Point;
				started: boolean;
				applied: Offset;
		  }
		| {
				kind: 'lineBody';
				lineId: string;
				origin: Point;
				ends: [Point, Point];
				bounds: Bounds;
				at: Point;
				started: boolean;
		  }
		| null;
	type Gesture =
		| { kind: 'pan'; last: Point; travelled: number; onFloor: boolean }
		| { kind: 'pinch'; distance: number; middle: Point }
		| null;

	interface Props {
		width: number;
		depth: number;
		zones: Zone[];
		lines: Line[];
		pointsOf: (zone: Zone) => Point[];
		endsOf: (line: Line) => [Point, Point];
		selected: Selected;
		editable: boolean;
		/** The floor plan as an object URL, placed at its origin and scale (metres). */
		plan?: { url: string; x: number; y: number; width: number; height: number } | null;
		onSelect: (selected: Selected) => void;
		onMoveVertex: (zoneId: string, index: number, point: Point) => void;
		onMoveLineEnd: (lineId: string, end: 'start' | 'end', point: Point) => void;
		/**
		 * A whole zone moved by one more step, with its lines (ARV-298): the screen adds it to the zone's pending move.
		 * False refuses the move (the zone has other unsaved changes), and the drag stops.
		 */
		onMoveZone: (zoneId: string, step: Offset) => boolean;
		/** A whole line moved: both ends. */
		onMoveLine: (lineId: string, ends: [Point, Point]) => void;
		/** A drag or a keyboard move finished: save the shape. */
		onCommit: (selected: NonNullable<Selected>) => void;
	}

	let {
		width,
		depth,
		zones,
		lines,
		pointsOf,
		endsOf,
		selected,
		editable,
		plan = null,
		onSelect,
		onMoveVertex,
		onMoveLineEnd,
		onMoveZone,
		onMoveLine,
		onCommit
	}: Props = $props();

	/** A pointer that travels less than this many pixels clicks rather than drags. */
	const SLOP = 4;

	let wrapper = $state<HTMLDivElement>();
	let svg = $state<SVGSVGElement>();
	let drag = $state<Drag>(null);
	let gesture = $state<Gesture>(null);
	let handTool = $state(false);
	let cursor = $state<Point | null>(null);
	/** The pointers on the plan (touch pinch); not reactive, read only in handlers. */
	const pointers = new Map<number, Point>();

	// The view: zoom, pan and fit around the level (shared with the live floor plan, ARV-299a).
	const view = new PlanView(() => ({ width, depth }));
	/** The selected zone is drawn last, on top: zones often overlap (a new one starts in the middle of the level). */
	const ordered = $derived(
		selected?.kind === 'zone'
			? [...zones.filter((z) => z.id !== selected.id), ...zones.filter((z) => z.id === selected.id)]
			: zones
	);

	const zoneTone: Record<string, string> = {
		Queue: 'fill-status-info/45 stroke-status-info-solid',
		Service: 'fill-status-success/45 stroke-status-success-solid',
		Staff: 'fill-status-neutral/60 stroke-status-neutral-foreground',
		Overflow: 'fill-status-warning/45 stroke-status-warning-solid'
	};
	const lineTone: Record<string, string> = {
		Entry: 'stroke-status-success-solid',
		Exit: 'stroke-status-danger-solid',
		Count: 'stroke-status-info-solid',
		OverflowEntry: 'stroke-status-warning-solid'
	};

	// The wheel zooms around the pointer (Shift and the wheel pan sideways); the page does not scroll under the editor.
	$effect(() => {
		const element = svg;
		if (!element) return;
		return view.listen(element);
	});

	/** Zooms to the selected shape with room around it. */
	function focusSelection(): void {
		const points = selectedPoints();
		if (points.length > 0) view.focus(boundsOf(points));
	}

	function selectedPoints(): Point[] {
		if (selected?.kind === 'zone') {
			const zone = zones.find((z) => z.id === selected.id);
			return zone ? pointsOf(zone) : [];
		}
		if (selected?.kind === 'line') {
			const line = lines.find((l) => l.id === selected.id);
			return line ? endsOf(line) : [];
		}
		return [];
	}

	/** A zone with its own lines: what moves when the whole zone moves. */
	function zoneBounds(zone: Zone): Bounds {
		return boundsOf([
			...pointsOf(zone),
			...lines.filter((l) => l.zoneId === zone.id).flatMap((l) => endsOf(l))
		]);
	}

	/** An offset that keeps every point of the shape inside the level. */
	function within(bounds: Bounds, dx: number, dy: number): Offset {
		return {
			dx: round(Math.min(Math.max(dx, -bounds.minX), width - bounds.maxX)),
			dy: round(Math.min(Math.max(dy, -bounds.minY), depth - bounds.maxY))
		};
	}

	/** Drags move in steps of 10 cm, as the arrow keys do. */
	function snap(value: number): number {
		return Math.round(value * 10) / 10;
	}

	function clamp(value: number, max: number): number {
		return round(Math.min(Math.max(value, 0), max));
	}

	/** Screen to floor coordinates in metres, not kept inside the level. */
	function floorAt(event: PointerEvent | MouseEvent): Point | null {
		const matrix = svg?.getScreenCTM();
		if (!matrix) return null;
		const p = new DOMPoint(event.clientX, event.clientY).matrixTransform(matrix.inverse());
		return { x: p.x, y: p.y };
	}

	/** Screen to floor coordinates, kept inside the level. */
	function toFloor(event: PointerEvent): Point | null {
		const p = floorAt(event);
		return p ? { x: clamp(p.x, width), y: clamp(p.y, depth) } : null;
	}

	function capture(event: PointerEvent): void {
		try {
			svg?.setPointerCapture(event.pointerId);
		} catch {
			// A pointer that is already gone cannot be captured; its events end the gesture anyway.
		}
	}

	/** A corner or a line end: dragged on its own. */
	function startHandle(event: PointerEvent, next: Drag): void {
		if (!editable) return;
		event.preventDefault();
		event.stopPropagation();
		capture(event);
		gesture = null;
		drag = next;
	}

	/** A zone pressed: selected, and dragged whole when the draft is editable and the hand tool is off. */
	function pressZone(event: PointerEvent, zone: Zone): void {
		onSelect({ kind: 'zone', id: zone.id });
		if (!editable || handTool || event.button !== 0) return;
		const origin = floorAt(event);
		if (!origin) return;
		event.preventDefault();
		event.stopPropagation();
		capture(event);
		drag = {
			kind: 'zone',
			zoneId: zone.id,
			origin,
			bounds: zoneBounds(zone),
			at: { x: event.clientX, y: event.clientY },
			started: false,
			applied: { dx: 0, dy: 0 }
		};
	}

	function pressLine(event: PointerEvent, line: Line): void {
		onSelect({ kind: 'line', id: line.id });
		if (!editable || handTool || event.button !== 0) return;
		const origin = floorAt(event);
		if (!origin) return;
		event.preventDefault();
		event.stopPropagation();
		capture(event);
		const ends = endsOf(line);
		drag = {
			kind: 'lineBody',
			lineId: line.id,
			origin,
			ends,
			bounds: boundsOf(ends),
			at: { x: event.clientX, y: event.clientY },
			started: false
		};
	}

	/** The floor, the plan or a shape that does not drag: pans, or with two fingers zooms. */
	function down(event: PointerEvent): void {
		if (event.pointerType === 'mouse' && event.button !== 0 && event.button !== 1) return;
		event.preventDefault();
		capture(event);
		pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
		if (pointers.size === 2) {
			const [a, b] = [...pointers.values()];
			drag = null;
			gesture = {
				kind: 'pinch',
				distance: Math.hypot(a.x - b.x, a.y - b.y),
				middle: { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 }
			};
		} else if (!drag) {
			gesture = {
				kind: 'pan',
				last: { x: event.clientX, y: event.clientY },
				travelled: 0,
				onFloor:
					event.target === event.currentTarget ||
					(event.target as Element).hasAttribute('data-floor')
			};
		}
	}

	function move(event: PointerEvent): void {
		const p = floorAt(event);
		cursor =
			p && p.x >= 0 && p.y >= 0 && p.x <= width && p.y <= depth
				? { x: round(p.x), y: round(p.y) }
				: null;
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
		if (gesture?.kind === 'pan') {
			const dx = event.clientX - gesture.last.x;
			const dy = event.clientY - gesture.last.y;
			view.panBy(dx, dy);
			gesture = {
				...gesture,
				last: { x: event.clientX, y: event.clientY },
				travelled: gesture.travelled + Math.abs(dx) + Math.abs(dy)
			};
			return;
		}
		if (!drag || !p) return;
		if (drag.kind === 'vertex' || drag.kind === 'line') {
			const point = toFloor(event);
			if (!point) return;
			if (drag.kind === 'vertex') onMoveVertex(drag.zoneId, drag.index, point);
			else onMoveLineEnd(drag.lineId, drag.end, point);
			return;
		}
		if (!drag.started && Math.hypot(event.clientX - drag.at.x, event.clientY - drag.at.y) < SLOP)
			return;
		const total = within(drag.bounds, snap(p.x - drag.origin.x), snap(p.y - drag.origin.y));
		if (drag.kind === 'zone') {
			const step = { dx: round(total.dx - drag.applied.dx), dy: round(total.dy - drag.applied.dy) };
			if (step.dx === 0 && step.dy === 0) {
				drag = { ...drag, started: true };
				return;
			}
			if (!onMoveZone(drag.zoneId, step)) {
				drag = null;
				return;
			}
			drag = { ...drag, started: true, applied: total };
		} else {
			const [a, b] = drag.ends;
			onMoveLine(drag.lineId, [
				{ x: round(a.x + total.dx), y: round(a.y + total.dy) },
				{ x: round(b.x + total.dx), y: round(b.y + total.dy) }
			]);
			drag = { ...drag, started: true };
		}
	}

	function up(event: PointerEvent): void {
		pointers.delete(event.pointerId);
		if (gesture?.kind === 'pinch') {
			// One finger left: it pans from where it is.
			const [rest] = [...pointers.values()];
			gesture = rest ? { kind: 'pan', last: rest, travelled: SLOP, onFloor: false } : null;
			return;
		}
		if (gesture?.kind === 'pan') {
			// A click on the empty floor clears the selection.
			if (gesture.travelled < SLOP && gesture.onFloor && event.type === 'pointerup') onSelect(null);
			gesture = null;
			return;
		}
		if (!drag) return;
		const done = drag;
		drag = null;
		if (done.kind === 'vertex') onCommit({ kind: 'zone', id: done.zoneId });
		else if (done.kind === 'line') onCommit({ kind: 'line', id: done.lineId });
		else if (done.kind === 'zone' && done.started) onCommit({ kind: 'zone', id: done.zoneId });
		else if (done.kind === 'lineBody' && done.started) onCommit({ kind: 'line', id: done.lineId });
	}

	const arrows: Record<string, [number, number]> = {
		ArrowLeft: [-1, 0],
		ArrowRight: [1, 0],
		ArrowUp: [0, -1],
		ArrowDown: [0, 1]
	};

	/** Arrow keys move a handle by 0.1 m (1 m with Shift); Enter saves the shape. */
	function nudge(
		event: KeyboardEvent,
		at: Point,
		apply: (point: Point) => void,
		commit: () => void
	): void {
		const step = event.shiftKey ? 1 : 0.1;
		if (event.key === 'Enter') {
			event.preventDefault();
			commit();
		} else if (arrows[event.key]) {
			event.preventDefault();
			event.stopPropagation();
			const [dx, dy] = arrows[event.key];
			apply({ x: clamp(at.x + dx * step, width), y: clamp(at.y + dy * step, depth) });
		}
	}

	/** Arrow keys move the selected zone with its lines, or the selected line, by 0.1 m (1 m with Shift); Enter saves. */
	function nudgeShape(event: KeyboardEvent, target: NonNullable<Selected>): void {
		// A read-only plan never moves a shape, by key either (the server refuses it too).
		if (!editable) return;
		const step = event.shiftKey ? 1 : 0.1;
		if (event.key === 'Enter') {
			event.preventDefault();
			onCommit(target);
			return;
		}
		if (!arrows[event.key]) return;
		event.preventDefault();
		event.stopPropagation();
		const [x, y] = arrows[event.key];
		if (target.kind === 'zone') {
			const zone = zones.find((z) => z.id === target.id);
			if (!zone) return;
			const offset = within(zoneBounds(zone), x * step, y * step);
			if (offset.dx !== 0 || offset.dy !== 0) onMoveZone(zone.id, offset);
		} else {
			const line = lines.find((l) => l.id === target.id);
			if (!line) return;
			const ends = endsOf(line);
			const offset = within(boundsOf(ends), x * step, y * step);
			onMoveLine(line.id, [
				{ x: round(ends[0].x + offset.dx), y: round(ends[0].y + offset.dy) },
				{ x: round(ends[1].x + offset.dx), y: round(ends[1].y + offset.dy) }
			]);
		}
	}

	function centre(points: Point[]): Point {
		return {
			x: points.reduce((s, p) => s + p.x, 0) / points.length,
			y: points.reduce((s, p) => s + p.y, 0) / points.length
		};
	}
</script>

<!-- The level in metres: the plan underneath, a grid, zones by kind, lines by role. Each shape carries its name as text
     (a title and a label), never as markup. The vertex table beside it is the keyboard alternative; the tools zoom,
     pan and fit the view (ARV-298). -->
<!-- svelte-ignore a11y_no_static_element_interactions -->
<div
	bind:this={wrapper}
	bind:clientWidth={view.cw}
	bind:clientHeight={view.ch}
	data-testid="zone-editor"
	class={cn(
		'relative overflow-hidden border bg-surface-2',
		view.fullscreen ? 'h-screen w-screen rounded-none' : 'h-[min(70vh,44rem)] min-h-80 rounded-xl'
	)}
	onkeydown={(event) => view.keys(event)}
>
	<svg
		bind:this={svg}
		data-testid="zone-canvas"
		data-zoom={view.zoom}
		role="group"
		aria-label={$_('zones.canvas')}
		viewBox="{view.box.x} {view.box.y} {view.box.w} {view.box.h}"
		preserveAspectRatio="xMidYMid meet"
		class={cn(
			'absolute inset-0 size-full touch-none select-none',
			gesture?.kind === 'pan'
				? 'cursor-grabbing'
				: handTool || !editable
					? 'cursor-grab'
					: 'cursor-default'
		)}
		onpointerdown={down}
		onpointermove={move}
		onpointerup={up}
		onpointercancel={up}
		onpointerleave={() => (cursor = null)}
	>
		<rect
			x="0"
			y="0"
			{width}
			height={depth}
			class="fill-card stroke-border"
			stroke-width="1"
			vector-effect="non-scaling-stroke"
			data-floor=""
		/>
		{#if plan}
			<image
				href={plan.url}
				x={plan.x}
				y={plan.y}
				width={plan.width}
				height={plan.height}
				preserveAspectRatio="none"
				opacity="0.85"
				data-floor=""
			/>
		{/if}
		<PlanGrid {view} />
		{#each ordered as zone (zone.id)}
			{@const points = pointsOf(zone)}
			{@const isSelected = selected?.kind === 'zone' && selected.id === zone.id}
			{@const middle = centre(points)}
			<g data-testid="zone-shape" data-zone={zone.name}>
				<polygon
					points={points.map((p) => `${p.x},${p.y}`).join(' ')}
					class={cn(
						zoneTone[zone.kind],
						editable && !handTool ? 'cursor-move' : 'cursor-pointer',
						isSelected && 'stroke-foreground',
						'focus-visible:outline-none'
					)}
					stroke-width={isSelected ? 3 : 1.5}
					vector-effect="non-scaling-stroke"
					role="button"
					tabindex={isSelected && editable ? 0 : -1}
					aria-label={isSelected && editable
						? $_('zones.moveZone', { values: { name: zone.name } })
						: zone.name}
					onpointerdown={(event) => pressZone(event, zone)}
					onkeydown={(event) => nudgeShape(event, { kind: 'zone', id: zone.id })}
				>
					<title>{zone.name}</title>
				</polygon>
				<text
					x={middle.x}
					y={middle.y}
					font-size={12 * view.pixel}
					text-anchor="middle"
					dominant-baseline="middle"
					class="pointer-events-none fill-foreground font-medium">{zone.name}</text
				>
			</g>
		{/each}
		{#each lines as line (line.id)}
			{@const [a, b] = endsOf(line)}
			{@const isSelected = selected?.kind === 'line' && selected.id === line.id}
			<g data-testid="line-shape" data-line={line.name}>
				<!-- A wider, invisible stroke under the line: easier to grab. -->
				<line
					x1={a.x}
					y1={a.y}
					x2={b.x}
					y2={b.y}
					class={cn(
						'stroke-transparent',
						editable && !handTool ? 'cursor-move' : 'cursor-pointer',
						'focus-visible:outline-none'
					)}
					stroke-width="14"
					stroke-linecap="round"
					vector-effect="non-scaling-stroke"
					role="button"
					tabindex={isSelected && editable ? 0 : -1}
					aria-label={isSelected && editable
						? $_('zones.moveLine', { values: { name: line.name } })
						: line.name}
					onpointerdown={(event) => pressLine(event, line)}
					onkeydown={(event) => nudgeShape(event, { kind: 'line', id: line.id })}
				>
					<title>{line.name}</title>
				</line>
				<line
					x1={a.x}
					y1={a.y}
					x2={b.x}
					y2={b.y}
					class={cn(lineTone[line.role], 'pointer-events-none')}
					stroke-width={isSelected ? 5 : 3}
					stroke-linecap="round"
					vector-effect="non-scaling-stroke"
				/>
			</g>
		{/each}
		{#if editable && selected?.kind === 'zone'}
			{@const zone = zones.find((z) => z.id === selected.id)}
			{#if zone}
				{#each pointsOf(zone) as point, index (index)}
					<circle
						data-testid="vertex-handle"
						cx={point.x}
						cy={point.y}
						r={7 * view.pixel}
						role="button"
						tabindex="0"
						aria-label={$_('zones.vertexHandle', {
							values: { n: index + 1, name: zone.name, x: point.x, y: point.y }
						})}
						class="cursor-crosshair fill-card stroke-foreground focus-visible:fill-primary focus-visible:outline-none"
						stroke-width="2"
						vector-effect="non-scaling-stroke"
						onpointerdown={(event) =>
							startHandle(event, { kind: 'vertex', zoneId: zone.id, index })}
						onkeydown={(event) =>
							nudge(
								event,
								point,
								(p) => onMoveVertex(zone.id, index, p),
								() => onCommit({ kind: 'zone', id: zone.id })
							)}
					/>
				{/each}
			{/if}
		{/if}
		{#if editable && selected?.kind === 'line'}
			{@const line = lines.find((l) => l.id === selected.id)}
			{#if line}
				{#each endsOf(line) as point, index (index)}
					{@const which = index === 0 ? 'start' : 'end'}
					<circle
						data-testid="line-handle"
						cx={point.x}
						cy={point.y}
						r={7 * view.pixel}
						role="button"
						tabindex="0"
						aria-label={$_(`zones.lineHandle.${which}`, {
							values: { name: line.name, x: point.x, y: point.y }
						})}
						class="cursor-crosshair fill-card stroke-foreground focus-visible:fill-primary focus-visible:outline-none"
						stroke-width="2"
						vector-effect="non-scaling-stroke"
						onpointerdown={(event) =>
							startHandle(event, { kind: 'line', lineId: line.id, end: which })}
						onkeydown={(event) =>
							nudge(
								event,
								point,
								(p) => onMoveLineEnd(line.id, which, p),
								() => onCommit({ kind: 'line', id: line.id })
							)}
					/>
				{/each}
			{/if}
		{/if}
	</svg>

	<PlanTools
		{view}
		label={$_('zones.tools.toolbar')}
		target={wrapper}
		testId="zone-tools"
		focus={{ disabled: !selected, run: focusSelection }}
		bind:hand={handTool}
	/>

	<p
		class="pointer-events-none absolute start-3 bottom-3 hidden max-w-[60%] rounded-md bg-card/90 px-2 py-1 text-xs text-muted-foreground shadow-sm md:block"
	>
		{editable ? $_('zones.tools.hint') : $_('zones.tools.hintReadOnly')}
	</p>
	<PlanScale {view} {cursor} />
</div>
