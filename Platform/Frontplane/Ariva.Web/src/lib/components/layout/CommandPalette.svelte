<script lang="ts">
	import { goto } from '$app/navigation';
	import { CornerDownLeft, Languages, Moon, Search, ShieldCheck, Sun } from '@lucide/svelte';
	import type { Component } from 'svelte';
	import { _, locale } from 'svelte-i18n';
	import { auth } from '$lib/core/auth.svelte';
	import { visibleItems } from '$lib/navigation';
	import { theme } from '$lib/theme/theme.svelte';
	import { cn } from '$lib/utils';

	interface Entry {
		id: string;
		label: string;
		group: string;
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		icon: Component<any>;
		run: () => void | Promise<void>;
	}

	let dialog = $state<HTMLDialogElement>();
	let input = $state<HTMLInputElement>();
	let query = $state('');
	let active = $state(0);

	// The screens the signed-in user's permissions cover, as in the sidebar (the server still checks every call), and a
	// few switches. Labels come from the dictionaries, so the palette searches in the language on screen.
	const entries = $derived.by((): Entry[] => {
		const screens = visibleItems((permission) => auth.can(permission))
			.filter((item) => item.ready)
			.map((item) => ({
				id: `screen-${item.id}`,
				label: $_(`navigation.items.${item.id}`),
				group: $_(`navigation.groups.${item.group}`),
				icon: item.icon,
				run: () => goto(item.href)
			}));
		const actions: Entry[] = [
			{
				id: 'action-account',
				label: $_('account.title'),
				group: $_('palette.actions'),
				icon: ShieldCheck,
				run: () => goto('/account')
			},
			{
				id: 'action-theme',
				label: theme.resolved === 'dark' ? $_('shell.theme.toLight') : $_('shell.theme.toDark'),
				group: $_('palette.actions'),
				icon: theme.resolved === 'dark' ? Sun : Moon,
				run: () => theme.toggle()
			},
			{
				id: 'action-language',
				label: $_('app.language.label'),
				group: $_('palette.actions'),
				icon: Languages,
				run: () => {
					locale.set($locale?.startsWith('ar') ? 'en' : 'ar');
				}
			}
		];
		return [...screens, ...actions];
	});

	/** Every word typed is in the label or the group, in any letter case. */
	const shown = $derived.by(() => {
		const words = query.trim().toLocaleLowerCase().split(/\s+/).filter(Boolean);
		if (words.length === 0) return entries;
		return entries.filter((entry) => {
			const text = `${entry.label} ${entry.group}`.toLocaleLowerCase();
			return words.every((word) => text.includes(word));
		});
	});
	const groups = $derived([...new Set(shown.map((entry) => entry.group))]);
	const current = $derived(shown[Math.min(active, shown.length - 1)]);

	/** Opens the palette with an empty search (Ctrl+K or Cmd+K, or the header's search button). */
	export function open(): void {
		// Never over another modal dialog (a step-up asking for a code, a confirmation): finish or close that first.
		if (!dialog || dialog.open || document.querySelector('dialog[open]')) return;
		query = '';
		active = 0;
		dialog.showModal();
		input?.focus();
	}

	function close(): void {
		dialog?.close();
	}

	async function run(entry: Entry | undefined): Promise<void> {
		if (!entry) return;
		close();
		await entry.run();
	}

	function keys(event: KeyboardEvent): void {
		if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
			event.preventDefault();
			if (shown.length === 0) return;
			const step = event.key === 'ArrowDown' ? 1 : -1;
			active = (Math.min(active, shown.length - 1) + step + shown.length) % shown.length;
			document.getElementById(`palette-${shown[active].id}`)?.scrollIntoView({ block: 'nearest' });
		} else if (event.key === 'Enter') {
			event.preventDefault();
			void run(current);
		}
	}
</script>

<!-- A native modal dialog: the browser keeps the focus inside it and Escape closes it; no inline styles (CSP). The search
     field is a combobox over a listbox of the screens and switches (WAI-ARIA 1.2). -->
<dialog
	bind:this={dialog}
	data-testid="command-palette"
	aria-label={$_('palette.title')}
	class="m-auto mt-[12vh] w-[min(36rem,calc(100vw-2rem))] overflow-hidden rounded-xl border bg-popover p-0 text-popover-foreground shadow-2xl backdrop:bg-black/40 backdrop:backdrop-blur-[2px]"
	onclick={(event) => {
		if (event.target === dialog) close();
	}}
>
	<div class="flex items-center gap-2 border-b px-4">
		<Search class="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
		<input
			bind:this={input}
			bind:value={query}
			oninput={() => (active = 0)}
			onkeydown={keys}
			type="text"
			role="combobox"
			aria-expanded="true"
			aria-controls="palette-list"
			aria-autocomplete="list"
			aria-activedescendant={current ? `palette-${current.id}` : undefined}
			aria-label={$_('palette.search')}
			placeholder={$_('palette.placeholder')}
			autocomplete="off"
			spellcheck="false"
			class="h-12 w-full border-0 bg-transparent px-0 text-sm focus:ring-0 focus:outline-none"
		/>
		<kbd
			class="hidden rounded border bg-muted px-1.5 py-0.5 text-[10px] font-medium text-muted-foreground sm:inline"
			>Esc</kbd
		>
	</div>
	<ul
		id="palette-list"
		role="listbox"
		aria-label={$_('palette.results')}
		class="max-h-[50vh] overflow-y-auto p-2"
	>
		{#each groups as group (group)}
			<li
				role="presentation"
				class="px-2 pt-2 pb-1 text-xs font-semibold tracking-wide text-tertiary uppercase"
			>
				{group}
			</li>
			{#each shown.filter((entry) => entry.group === group) as entry (entry.id)}
				{@const Icon = entry.icon}
				{@const selected = entry === current}
				<li
					id="palette-{entry.id}"
					role="option"
					aria-selected={selected}
					data-testid="palette-option"
					class={cn(
						'flex cursor-pointer items-center gap-3 rounded-md px-2 py-2 text-sm',
						selected ? 'bg-accent text-accent-foreground' : 'hover:bg-muted'
					)}
					onpointermove={() => (active = shown.indexOf(entry))}
					onclick={() => run(entry)}
					onkeydown={() => undefined}
				>
					<Icon class="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
					<span class="flex-1 truncate">{entry.label}</span>
					{#if selected}<CornerDownLeft
							class="size-3.5 text-muted-foreground rtl:-scale-x-100"
							aria-hidden="true"
						/>{/if}
				</li>
			{/each}
		{:else}
			<li role="presentation" class="px-2 py-6 text-center text-sm text-muted-foreground">
				{$_('palette.none')}
			</li>
		{/each}
	</ul>
</dialog>
