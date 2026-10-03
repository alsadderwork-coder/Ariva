<script lang="ts" module>
	export type Selected = { kind: 'zone' | 'line'; id: string } | null;
</script>

<script lang="ts">
	import { _ } from 'svelte-i18n';
	import type { Line, Point, Zone } from '$lib/core/zones';
	import { round } from '$lib/core/zones';
	import { cn } from '$lib/utils';

	type Drag =
		| { kind: 'vertex'; zoneId: string; index: number }
		| { kind: 'line'; lineId: string; end: 'start' | 'end' }
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
		onCommit
	}: Props = $props();

	let svg = $state<SVGSVGElement>();
	let drag = $state<Drag>(null);

	const size = $derived(Math.max(width, depth));
	const handle = $derived(size / 110);
	const label = $derived(size / 55);

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

	function clamp(value: number, max: number): number {
		return round(Math.min(Math.max(value, 0), max));
	}

	/** Screen to floor coordinates (metres), kept inside the level. */
	function toFloor(event: PointerEvent): Point | null {
		const matrix = svg?.getScreenCTM();
		if (!matrix) return null;
		const p = new DOMPoint(event.clientX, event.clientY).matrixTransform(matrix.inverse());
		return { x: clamp(p.x, width), y: clamp(p.y, depth) };
	}

	function start(event: PointerEvent, next: NonNullable<Drag>): void {
		if (!editable) return;
		event.preventDefault();
		event.stopPropagation();
		(event.currentTarget as Element).setPointerCapture(event.pointerId);
		drag = next;
	}

	function move(event: PointerEvent): void {
		if (!drag) return;
		const point = toFloor(event);
		if (!point) return;
		if (drag.kind === 'vertex') onMoveVertex(drag.zoneId, drag.index, point);
		else onMoveLineEnd(drag.lineId, drag.end, point);
	}

	function end(): void {
		if (!drag) return;
		const done = drag;
		drag = null;
		onCommit(
			done.kind === 'vertex' ? { kind: 'zone', id: done.zoneId } : { kind: 'line', id: done.lineId }
		);
	}

	/** Arrow keys move a handle by 0.1 m (1 m with Shift); Enter saves the shape. */
	function nudge(
		event: KeyboardEvent,
		at: Point,
		apply: (point: Point) => void,
		commit: () => void
	): void {
		const step = event.shiftKey ? 1 : 0.1;
		const moves: Record<string, [number, number]> = {
			ArrowLeft: [-step, 0],
			ArrowRight: [step, 0],
			ArrowUp: [0, -step],
			ArrowDown: [0, step]
		};
		if (event.key === 'Enter') {
			event.preventDefault();
			commit();
		} else if (moves[event.key]) {
			event.preventDefault();
			const [dx, dy] = moves[event.key];
			apply({ x: clamp(at.x + dx, width), y: clamp(at.y + dy, depth) });
		}
	}

	function centre(points: Point[]): Point {
		return {
			x: points.reduce((s, p) => s + p.x, 0) / points.length,
			y: points.reduce((s, p) => s + p.y, 0) / points.length
		};
	}
</script>

<!-- The level in metres: the plan underneath, zones by kind, lines by role. Each shape carries its name as text
     (a title and a label), never as markup. The vertex table beside it is the keyboard alternative. -->
<svg
	bind:this={svg}
	data-testid="zone-canvas"
	role="group"
	aria-label={$_('zones.canvas')}
	viewBox="0 0 {width} {depth}"
	class="w-full touch-none rounded-lg border bg-surface-2 select-none"
	onpointermove={move}
	onpointerup={end}
	onpointercancel={end}
>
	<rect x="0" y="0" {width} height={depth} class="fill-card" />
	{#if plan}
		<image
			href={plan.url}
			x={plan.x}
			y={plan.y}
			width={plan.width}
			height={plan.height}
			preserveAspectRatio="none"
			opacity="0.85"
		/>
	{/if}
	{#each zones as zone (zone.id)}
		{@const points = pointsOf(zone)}
		{@const isSelected = selected?.kind === 'zone' && selected.id === zone.id}
		{@const middle = centre(points)}
		<g data-testid="zone-shape" data-zone={zone.name}>
			<polygon
				points={points.map((p) => `${p.x},${p.y}`).join(' ')}
				class={cn(zoneTone[zone.kind], 'cursor-pointer', isSelected && 'stroke-foreground')}
				stroke-width={isSelected ? 3 : 1.5}
				vector-effect="non-scaling-stroke"
				role="button"
				tabindex="-1"
				aria-label={zone.name}
				onpointerdown={() => onSelect({ kind: 'zone', id: zone.id })}
			>
				<title>{zone.name}</title>
			</polygon>
			<text
				x={middle.x}
				y={middle.y}
				font-size={label}
				text-anchor="middle"
				dominant-baseline="middle"
				class="pointer-events-none fill-foreground">{zone.name}</text
			>
		</g>
	{/each}
	{#each lines as line (line.id)}
		{@const [a, b] = endsOf(line)}
		{@const isSelected = selected?.kind === 'line' && selected.id === line.id}
		<g data-testid="line-shape" data-line={line.name}>
			<line
				x1={a.x}
				y1={a.y}
				x2={b.x}
				y2={b.y}
				class={cn(lineTone[line.role], 'cursor-pointer')}
				stroke-width={isSelected ? 5 : 3}
				stroke-linecap="round"
				vector-effect="non-scaling-stroke"
				role="button"
				tabindex="-1"
				aria-label={line.name}
				onpointerdown={() => onSelect({ kind: 'line', id: line.id })}
			>
				<title>{line.name}</title>
			</line>
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
					r={handle}
					role="button"
					tabindex="0"
					aria-label={$_('zones.vertexHandle', {
						values: { n: index + 1, name: zone.name, x: point.x, y: point.y }
					})}
					class="cursor-move fill-card stroke-foreground focus-visible:fill-primary focus-visible:outline-none"
					stroke-width="2"
					vector-effect="non-scaling-stroke"
					onpointerdown={(event) => start(event, { kind: 'vertex', zoneId: zone.id, index })}
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
					r={handle}
					role="button"
					tabindex="0"
					aria-label={$_(`zones.lineHandle.${which}`, {
						values: { name: line.name, x: point.x, y: point.y }
					})}
					class="cursor-move fill-card stroke-foreground focus-visible:fill-primary focus-visible:outline-none"
					stroke-width="2"
					vector-effect="non-scaling-stroke"
					onpointerdown={(event) => start(event, { kind: 'line', lineId: line.id, end: which })}
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
