<script lang="ts">
	import { Copy, KeyRound } from '@lucide/svelte';
	import { _ } from 'svelte-i18n';
	import type { ImportedCredential } from '$lib/core/csvImports';

	interface Props {
		credentials: ImportedCredential[];
		/** The notes of the commit, already in the reader's language. */
		notes: string[];
		/** Ticked by the user once every credential is copied; the dialog closes only then. */
		confirmed: boolean;
		/** How many closes were asked for before the confirmation: each shows the prompt and focuses the box. */
		prompts: number;
	}

	let { credentials, notes, confirmed = $bindable(), prompts }: Props = $props();

	let table = $state<HTMLElement>();
	let confirmBox = $state<HTMLInputElement>();
	/** The last copy's outcome, shown in the dialog (a toast would sit behind the modal's backdrop). */
	let copied = $state<'copied' | 'failed' | null>(null);

	$effect(() => {
		if (prompts > 0 && !confirmed) confirmBox?.focus();
	});

	/** Selects the given element's text, so the user can copy it when the clipboard refuses. */
	function select(element: HTMLElement | null | undefined): void {
		const selection = window.getSelection();
		if (!element || !selection) return;
		const range = document.createRange();
		range.selectNodeContents(element);
		selection.removeAllRanges();
		selection.addRange(range);
	}

	async function copy(text: string, fallback: HTMLElement | null | undefined): Promise<void> {
		copied = null;
		try {
			await navigator.clipboard.writeText(text);
			copied = 'copied';
		} catch {
			select(fallback);
			copied = 'failed';
		}
	}

	/** Every code and credential as CSV lines (codes are upper case letters, digits and hyphens; credentials ardk_). */
	function copyAll(): Promise<void> {
		const lines = ['code,credential', ...credentials.map((c) => `${c.code},${c.credential}`)];
		return copy(lines.join('\r\n'), table);
	}
</script>

<!-- CWE-532: the credentials exist only in the import dialog's state while it is open; this view never stores, logs or
     sends them anywhere but the clipboard the user asks for, and the dialog drops them when it closes. -->
<section data-testid="import-credentials" class="flex flex-col gap-3">
	<div
		class="flex flex-col gap-2 rounded-xl border border-status-warning-border bg-status-warning p-4 text-status-warning-foreground"
	>
		<div class="flex items-center gap-2">
			<KeyRound class="size-5 shrink-0" aria-hidden="true" />
			<h3 id="import-credentials-title" class="text-base font-semibold">
				{$_('csvImport.credentials.title', { values: { count: credentials.length } })}
			</h3>
		</div>
		<p class="text-sm" data-testid="import-credentials-warning">
			{$_('csvImport.credentials.warning')}
		</p>
		{#each notes as note, index (index)}
			<p class="text-sm">{note}</p>
		{/each}
	</div>

	<div class="flex flex-wrap items-center gap-2">
		<button
			type="button"
			data-testid="import-copy-all"
			onclick={copyAll}
			class="inline-flex h-9 items-center gap-2 rounded-md border bg-card px-3 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			<Copy class="size-4" aria-hidden="true" />
			{$_('csvImport.credentials.copyAll')}
		</button>
		<p role="status" data-testid="import-copy-status" class="text-sm text-muted-foreground">
			{#if copied}{$_(
					`csvImport.credentials.${copied === 'copied' ? 'copied' : 'copyFailed'}`
				)}{/if}
		</p>
	</div>

	<!-- The table scrolls inside the dialog, so its region takes the keyboard focus (WCAG 2.1.1, axe
	     scrollable-region-focusable), named by the heading above it. -->
	<!-- svelte-ignore a11y_no_noninteractive_tabindex -->
	<div
		class="max-h-80 overflow-auto rounded-lg border focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		role="region"
		aria-labelledby="import-credentials-title"
		tabindex="0"
		bind:this={table}
	>
		<table class="w-full text-sm">
			<thead
				class="sticky top-0 bg-surface-2 text-xs font-semibold tracking-wide text-tertiary uppercase"
			>
				<tr>
					<th scope="col" class="px-4 py-2.5 text-start">{$_('csvImport.credentials.device')}</th>
					<th scope="col" class="px-4 py-2.5 text-start"
						>{$_('csvImport.credentials.credential')}</th
					>
					<th scope="col" class="px-4 py-2.5 text-end"
						><span class="sr-only">{$_('csvImport.credentials.copy')}</span></th
					>
				</tr>
			</thead>
			<tbody>
				{#each credentials as item (item.code)}
					<tr data-testid="import-credential" class="odd:bg-surface even:bg-surface-2/50">
						<th scope="row" class="px-4 py-2 text-start font-mono font-medium" dir="ltr"
							>{item.code}</th
						>
						<td class="px-4 py-2">
							<code
								data-testid="import-credential-value"
								dir="ltr"
								class="block rounded-md bg-card px-2 py-1 font-mono text-xs break-all text-foreground select-all"
								>{item.credential}</code
							>
						</td>
						<td class="px-4 py-2 text-end">
							<button
								type="button"
								data-testid="import-copy-credential"
								aria-label={$_('csvImport.credentials.copyFor', { values: { code: item.code } })}
								onclick={(event) =>
									copy(item.credential, event.currentTarget.closest('tr')?.querySelector('code'))}
								class="inline-flex h-8 items-center gap-1.5 rounded-md border bg-card px-2.5 text-xs font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
							>
								<Copy class="size-3.5" aria-hidden="true" />
								{$_('csvImport.credentials.copy')}
							</button>
						</td>
					</tr>
				{/each}
			</tbody>
		</table>
	</div>

	<div class="flex flex-col gap-1">
		<label class="inline-flex items-center gap-2 text-sm font-medium">
			<input
				type="checkbox"
				data-testid="import-confirm-copied"
				bind:this={confirmBox}
				bind:checked={confirmed}
				class="size-4 rounded border-input"
			/>
			{$_('csvImport.credentials.confirm')}
		</label>
		{#if prompts > 0 && !confirmed}
			<p
				role="alert"
				data-testid="import-confirm-first"
				class="text-sm text-status-danger-foreground"
			>
				{$_('csvImport.credentials.confirmFirst')}
			</p>
		{/if}
	</div>
</section>
