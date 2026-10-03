<script lang="ts">
	import { Copy } from '@lucide/svelte';
	import { _ } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';

	let { codes }: { codes: string[] } = $props();

	let list = $state<HTMLElement>();

	async function copy(): Promise<void> {
		try {
			await navigator.clipboard.writeText(codes.join('\n'));
			toast.success($_('auth.setup.copied'));
		} catch {
			// Clipboard refused (permissions, an insecure context): select the codes so the user can copy them.
			const selection = window.getSelection();
			if (list && selection) {
				const range = document.createRange();
				range.selectNodeContents(list);
				selection.removeAllRanges();
				selection.addRange(range);
			}
			toast.error($_('auth.setup.copyFailed'));
		}
	}
</script>

<!-- Shown once (ARV-010c): the server keeps only their hashes. Text interpolation only. -->
<div class="flex flex-col gap-3">
	<p class="text-sm text-secondary-foreground">{$_('auth.setup.recoveryIntro')}</p>
	<ol
		bind:this={list}
		data-testid="recovery-codes"
		aria-label={$_('auth.setup.recoveryList')}
		class="grid grid-cols-2 gap-2 rounded-lg border bg-surface-2 p-4 font-mono text-sm tracking-wider tabular-nums"
	>
		{#each codes as code (code)}
			<li class="select-all">{code}</li>
		{/each}
	</ol>
	<button
		type="button"
		data-testid="copy-recovery-codes"
		onclick={copy}
		class="inline-flex h-10 items-center justify-center gap-2 self-start rounded-md border bg-card px-4 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
	>
		<Copy class="size-4" aria-hidden="true" />
		{$_('auth.setup.copy')}
	</button>
</div>
