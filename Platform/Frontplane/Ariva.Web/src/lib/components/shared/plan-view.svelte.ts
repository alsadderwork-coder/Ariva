import type { Point } from '$lib/core/zones';

/** A box on the floor, in metres. */
export type Bounds = { minX: number; maxX: number; minY: number; maxY: number };
type Kept = { key: string; zoom: number; cx: number; cy: number };

export const MIN_ZOOM = 0.5;
export const MAX_ZOOM = 40;
/** Round lengths in metres, for the grid and the scale bar. */
const STEPS = [0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000];
/** How long the "hold Ctrl to zoom" hint stays after a plain wheel over a plan that needs it, in milliseconds. */
const HINT_MS = 1500;

export function boundsOf(points: Point[]): Bounds {
	return {
		minX: Math.min(...points.map((p) => p.x)),
		maxX: Math.max(...points.map((p) => p.x)),
		minY: Math.min(...points.map((p) => p.y)),
		maxY: Math.max(...points.map((p) => p.y))
	};
}

function clampZoom(zoom: number): number {
	return Math.min(Math.max(zoom, MIN_ZOOM), MAX_ZOOM);
}

/**
 * The view of one level of a floor plan in metres (ARV-298; shared with the live floor plan by ARV-299a): a zoom
 * relative to the fitted level and the floor point at the middle of the canvas. A view kept for another level (other
 * width or depth) is dropped, so every level opens fitted. The canvas binds its size to `cw` and `ch`.
 */
export class PlanView {
	/** The canvas width in screen pixels. */
	cw = $state(0);
	/** The canvas height in screen pixels. */
	ch = $state(0);
	/** The canvas is in full screen (set by PlanTools from the document's fullscreenchange). */
	fullscreen = $state(false);
	/** The browser offers full screen here. */
	canFullscreen = $state(false);
	/** A plain wheel went past a plan that zooms only with Ctrl or Cmd: say how, for a moment. */
	modifierHint = $state(false);

	#kept = $state<Kept | null>(null);
	#level: () => { width: number; depth: number };
	#pad: number;
	#hintTimer: ReturnType<typeof setTimeout> | undefined;

	/** `level` reads the level's size in metres; `pad` is the room around the fitted level in screen pixels. */
	constructor(level: () => { width: number; depth: number }, pad = 28) {
		this.#level = level;
		this.#pad = pad;
	}

	get width(): number {
		return this.#level().width;
	}

	get depth(): number {
		return this.#level().depth;
	}

	#key = $derived(`${this.width}x${this.depth}`);
	#current = $derived(
		this.#kept && this.#kept.key === this.#key
			? this.#kept
			: { key: this.#key, zoom: 1, cx: this.width / 2, cy: this.depth / 2 }
	);

	/** The zoom relative to the fitted level: 1 is fitted. */
	zoom = $derived(this.#current.zoom);
	zoomPercent = $derived(Math.round(this.#current.zoom * 100));
	canZoomIn = $derived(this.#current.zoom < MAX_ZOOM);
	canZoomOut = $derived(this.#current.zoom > MIN_ZOOM);
	/** Screen pixels per metre when the level is fitted. */
	fitScale = $derived.by(() => {
		if (this.cw <= 0 || this.ch <= 0) return 0;
		const pad = 2 * this.#pad;
		return Math.max(Math.min((this.cw - pad) / this.width, (this.ch - pad) / this.depth), 1e-6);
	});
	/** Screen pixels per metre. */
	scale = $derived(this.fitScale * this.#current.zoom);
	/** The floor in view, in metres: the SVG viewBox. */
	box = $derived(
		this.scale > 0
			? {
					x: this.#current.cx - this.cw / 2 / this.scale,
					y: this.#current.cy - this.ch / 2 / this.scale,
					w: this.cw / this.scale,
					h: this.ch / this.scale
				}
			: { x: 0, y: 0, w: this.width, h: this.depth }
	);
	/** One screen pixel in metres: handles and labels keep their size on screen at any zoom. */
	pixel = $derived(this.scale > 0 ? 1 / this.scale : Math.max(this.width, this.depth) / 800);

	/** Grid lines inside the level and the view, about every 20 pixels; every fifth is stronger. */
	grid = $derived.by(() => {
		if (this.scale <= 0) return { minor: [] as number[][], major: [] as number[][] };
		const step = STEPS.find((s) => s * this.scale >= 20) ?? 1000;
		const minor: number[][] = [];
		const major: number[][] = [];
		const { box, width, depth } = this;
		const x0 = Math.max(0, Math.ceil(box.x / step) * step);
		const x1 = Math.min(width, box.x + box.w);
		const y0 = Math.max(0, Math.ceil(box.y / step) * step);
		const y1 = Math.min(depth, box.y + box.h);
		const top = Math.max(0, box.y);
		const left = Math.max(0, box.x);
		for (let x = x0, i = 0; x <= x1 && i < 400; x += step, i++)
			(Math.round(x / step) % 5 === 0 ? major : minor).push([x, top, x, y1]);
		for (let y = y0, i = 0; y <= y1 && i < 400; y += step, i++)
			(Math.round(y / step) % 5 === 0 ? major : minor).push([left, y, x1, y]);
		return { minor, major };
	});

	/** A scale bar of a round length between 60 and 150 pixels. */
	bar = $derived.by(() => {
		if (this.scale <= 0) return null;
		const metres = STEPS.find((s) => s * this.scale >= 60) ?? 1000;
		return { metres, pixels: metres * this.scale };
	});

	#set(zoom: number, cx: number, cy: number): void {
		// A box without points (Infinity) or a size of zero never becomes the view: the view stays where it is.
		if (![zoom, cx, cy].every(Number.isFinite)) return;
		this.#kept = {
			key: this.#key,
			zoom,
			cx: Math.min(Math.max(cx, 0), this.width),
			cy: Math.min(Math.max(cy, 0), this.depth)
		};
	}

	/** Zooms by a factor around a point of the canvas (pixels from its top left corner); that floor point stays put. */
	zoomAt(x: number, y: number, factor: number): void {
		if (this.scale <= 0) return;
		const zoom = clampZoom(this.#current.zoom * factor);
		const next = this.fitScale * zoom;
		const floorX = this.box.x + x / this.scale;
		const floorY = this.box.y + y / this.scale;
		this.#set(zoom, floorX - (x - this.cw / 2) / next, floorY - (y - this.ch / 2) / next);
	}

	zoomBy(factor: number): void {
		this.zoomAt(this.cw / 2, this.ch / 2, factor);
	}

	/** Moves the view with the pointer, by screen pixels. */
	panBy(dx: number, dy: number): void {
		if (this.scale <= 0) return;
		this.#set(
			this.#current.zoom,
			this.#current.cx - dx / this.scale,
			this.#current.cy - dy / this.scale
		);
	}

	fit(): void {
		this.#kept = null;
	}

	/** Zooms to a box on the floor with room around it. */
	focus(bounds: Bounds): void {
		if (this.fitScale <= 0) return;
		const w = Math.max(bounds.maxX - bounds.minX, 2);
		const h = Math.max(bounds.maxY - bounds.minY, 2);
		const zoom = clampZoom(
			Math.min((this.cw - 4 * this.#pad) / w, (this.ch - 4 * this.#pad) / h) / this.fitScale
		);
		this.#set(zoom, (bounds.minX + bounds.maxX) / 2, (bounds.minY + bounds.maxY) / 2);
	}

	/**
	 * The wheel zooms around the pointer; Shift and the wheel pan sideways. Added by hand, as a wheel listener Svelte adds
	 * is passive and the page must not scroll while the plan zooms. When `needsModifier` answers true, a plain wheel is
	 * left to the page, which scrolls, and only Ctrl or Cmd with the wheel zooms (a trackpad pinch arrives that way too).
	 * Returns the function that removes the listener.
	 */
	listen(element: SVGSVGElement, needsModifier: () => boolean = () => false): () => void {
		const wheel = (event: WheelEvent) => {
			const modified = event.ctrlKey || event.metaKey;
			if (needsModifier() && !modified) {
				this.#hint();
				return;
			}
			event.preventDefault();
			const rect = element.getBoundingClientRect();
			const amount = event.deltaMode === 1 ? event.deltaY * 16 : event.deltaY;
			if (event.shiftKey) this.panBy(-(event.deltaX || amount), 0);
			else
				this.zoomAt(
					event.clientX - rect.left,
					event.clientY - rect.top,
					Math.exp(-amount * 0.0015)
				);
		};
		element.addEventListener('wheel', wheel, { passive: false });
		return () => {
			element.removeEventListener('wheel', wheel);
			clearTimeout(this.#hintTimer);
		};
	}

	#hint(): void {
		this.modifierHint = true;
		clearTimeout(this.#hintTimer);
		this.#hintTimer = setTimeout(() => (this.modifierHint = false), HINT_MS);
	}

	/** + and - zoom, 0 fits the level, while the focus is on the plan or its tools. True when the key was used. */
	keys(event: KeyboardEvent): boolean {
		const target = event.target as HTMLElement;
		if (
			['INPUT', 'SELECT', 'TEXTAREA'].includes(target.tagName) ||
			event.ctrlKey ||
			event.metaKey ||
			event.altKey
		)
			return false;
		if (event.key === '+' || event.key === '=') this.zoomBy(1.25);
		else if (event.key === '-') this.zoomBy(0.8);
		else if (event.key === '0') this.fit();
		else return false;
		event.preventDefault();
		return true;
	}
}
