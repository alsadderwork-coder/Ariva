<script lang="ts">
	import { onDestroy, onMount } from 'svelte';
	import { nextBand, type Band } from '$lib/core/bands';
	import * as displays from '$lib/core/displays';
	import type { Board } from '$lib/core/displays';
	import ar from '$lib/i18n/board/ar.json';
	import en from '$lib/i18n/board/en.json';
	import pt from '$lib/i18n/board/pt.json';
	import sw from '$lib/i18n/board/sw.json';

	/**
	 * The passenger display player (ARV-058): a full-screen 16:9 board with no shell and no user session. It takes its
	 * display's code from the address and the credential from the fragment (#key=...), keeps the credential for this tab
	 * only and removes it from the address bar, and asks for its board every 10 seconds with the credential in a header.
	 * Each entry shows its band in every language of the display, held within the hysteresis; stale or missing data
	 * shows the display's neutral message, never an old number. Labels and messages are text, never markup.
	 */
	const dictionaries: Record<string, Record<string, string>> = { en, ar, pt, sw };
	const rtl = new Set(['ar']);
	const pollMs = 10_000;
	const storageKey = (code: string) => `ariva.display.${code}`;

	let code = $state('');
	let credential = '';
	let board = $state<Board | null>(null);
	let bands = $state<(Band | null)[]>([]);
	let refused = $state(false);
	let lastOk = $state(0);
	let now = $state(Date.now());
	let timer: ReturnType<typeof setInterval> | undefined;
	let clock: ReturnType<typeof setInterval> | undefined;

	const languages = $derived(board?.languages ?? ['en']);
	/** The whole board is stale when the last good answer is older than the display's stale threshold. */
	const boardStale = $derived(!board || now - lastOk > (board?.staleSeconds ?? 150) * 1000);

	function words(
		language: string,
		key: string,
		values: Record<string, string | number> = {}
	): string {
		const template = dictionaries[language]?.[key] ?? dictionaries.en[key] ?? key;
		return template.replace(/\{(\w+)\}/g, (_, name: string) => String(values[name] ?? ''));
	}

	function bandText(language: string, band: Band): string {
		return band.low === 0
			? words(language, 'under', { high: band.high })
			: words(language, 'band', { low: band.low, high: band.high });
	}

	async function poll(): Promise<void> {
		if (!code || !credential) return;
		const answer = await displays.board(code, credential);
		now = Date.now();
		if ('refused' in answer) {
			refused = true;
			return;
		}
		// A failed request keeps the last board until it goes stale.
		if ('failed' in answer) return;
		refused = false;
		const next = answer.board;
		bands = next.entries.map((entry, index) => {
			const fresh =
				entry.nowcastMinutes != null &&
				entry.ageSeconds != null &&
				entry.ageSeconds <= next.staleSeconds;
			if (!fresh) return null;
			return nextBand(
				bands[index] ?? null,
				entry.nowcastMinutes!,
				next.bandMinutes,
				next.hysteresisMinutes,
				entry.degraded
			);
		});
		board = next;
		lastOk = now;
	}

	onMount(() => {
		const params = new URLSearchParams(location.search);
		code = params.get('code') ?? '';
		const fragment = new URLSearchParams(location.hash.slice(1));
		const fromAddress = fragment.get('key');
		try {
			if (fromAddress) sessionStorage.setItem(storageKey(code), fromAddress);
			credential = fromAddress ?? sessionStorage.getItem(storageKey(code)) ?? '';
		} catch {
			credential = fromAddress ?? '';
		}
		// The credential leaves the address bar (and the history) at once.
		if (location.hash) history.replaceState(null, '', `${location.pathname}${location.search}`);
		if (!credential || !displays.codePattern.test(code)) {
			refused = true;
			return;
		}
		void poll();
		timer = setInterval(() => void poll(), pollMs);
		clock = setInterval(() => (now = Date.now()), 1000);
	});

	onDestroy(() => {
		clearInterval(timer);
		clearInterval(clock);
	});

	/**
	 * Fits the board to the screen: the type starts at its full size for the viewport and shrinks until every row fits,
	 * however many queues and languages the display has. Set through the CSSOM (the CSP allows no style attribute).
	 */
	function fit(node: HTMLElement, _watch: unknown) {
		function apply(): void {
			const full = Math.min(window.innerWidth * 0.016, window.innerHeight * 0.0285);
			let size = full;
			node.style.setProperty('font-size', `${size}px`);
			for (let i = 0; i < 6 && node.scrollHeight > window.innerHeight + 1; i++) {
				size *= Math.max(0.5, (window.innerHeight / node.scrollHeight) * 0.98);
				node.style.setProperty('font-size', `${size}px`);
			}
		}
		apply();
		window.addEventListener('resize', apply);
		return {
			update: apply,
			destroy: () => window.removeEventListener('resize', apply)
		};
	}
</script>

<svelte:head>
	<title>{board?.name ?? 'Ariva'}</title>
	<meta name="robots" content="noindex" />
</svelte:head>

<!-- 16:9 at any size: the type scales with the viewport and the number of rows (use:fit); portrait boards stack the same rows. -->
<main
	data-testid="board"
	data-orientation={board?.orientation ?? 'Landscape'}
	data-state={refused ? 'refused' : boardStale ? 'stale' : 'live'}
	use:fit={[board, bands, refused, boardStale]}
	class="flex h-screen w-screen flex-col overflow-hidden bg-[#0b1220] text-white"
>
	{#if refused}
		<div class="flex flex-1 items-center justify-center p-[4em] text-center">
			<div class="flex flex-col gap-[1em]">
				{#each ['en', 'ar'] as language (language)}
					<p
						dir={rtl.has(language) ? 'rtl' : 'ltr'}
						lang={language}
						class="text-[2.2em] font-semibold"
						data-testid="board-refused"
					>
						{words(language, 'unavailable')}
					</p>
				{/each}
			</div>
		</div>
	{:else if !board}
		<div class="flex flex-1 items-center justify-center text-[2em] text-white/70">
			{words('en', 'connecting')}
		</div>
	{:else}
		<header
			class="flex items-end justify-between gap-[2em] border-b border-white/15 px-[3em] py-[1.5em]"
		>
			<div class="flex flex-wrap gap-x-[2em] gap-y-[0.3em]">
				{#each languages as language (language)}
					<h1
						dir={rtl.has(language) ? 'rtl' : 'ltr'}
						lang={language}
						class="text-[2.6em] font-bold"
					>
						{words(language, 'title')}
					</h1>
				{/each}
			</div>
			<p class="text-[1.2em] text-white/60 tabular-nums" dir="ltr" data-testid="board-updated">
				{new Date(lastOk).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' })}
			</p>
		</header>
		{#if board.illustrative === true}
			<!-- ARV-139a: an illustrative demo site's board says so in English and Arabic, whatever its languages. -->
			<div
				role="note"
				data-testid="board-illustrative"
				class="flex flex-wrap justify-center gap-x-[2em] gap-y-[0.2em] border-b border-amber-300/40 bg-amber-400/15 px-[3em] py-[0.6em]"
			>
				{#each ['en', 'ar'] as language (language)}
					<p
						dir={rtl.has(language) ? 'rtl' : 'ltr'}
						lang={language}
						class="text-[1.3em] font-semibold text-amber-200"
						data-testid="board-illustrative-text"
					>
						{words(language, 'illustrative')}
					</p>
				{/each}
			</div>
		{/if}
		<ul class="flex flex-1 flex-col justify-evenly gap-[1em] px-[3em] py-[2em]">
			{#each board.entries as entry, index (index)}
				{@const band = boardStale ? null : bands[index]}
				<li
					data-testid="board-entry"
					data-band={band ? `${band.low}-${band.high}` : 'none'}
					class="grid grid-cols-[minmax(0,1fr)_minmax(0,1.1fr)] items-center gap-[2em] rounded-[0.6em] bg-white/[0.06] px-[2em] py-[1em]"
				>
					<div class="flex flex-col gap-[0.2em]">
						{#each languages as language (language)}
							<span
								dir={rtl.has(language) ? 'rtl' : 'ltr'}
								lang={language}
								class="text-[2.1em] leading-tight font-semibold"
								data-testid="board-label">{entry.labels[language] ?? ''}</span
							>
						{/each}
					</div>
					<div class="flex flex-col gap-[0.2em]">
						{#each languages as language (language)}
							<span
								dir={rtl.has(language) ? 'rtl' : 'ltr'}
								lang={language}
								data-testid={band ? 'board-band' : 'board-fallback'}
								class={band
									? 'text-[2.4em] leading-tight font-bold text-[#7dd3fc] tabular-nums'
									: 'text-[1.7em] leading-tight text-white/80'}
								>{band ? bandText(language, band) : (board.fallback[language] ?? '')}</span
							>
						{/each}
					</div>
				</li>
			{/each}
		</ul>
	{/if}
</main>
