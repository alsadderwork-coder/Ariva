<script lang="ts">
	import { Copy, KeyRound } from '@lucide/svelte';
	import { _ } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';

	interface Props {
		code: string;
		credential: string;
		onDone: () => void;
		/**
		 * The words of the reveal, under one i18n prefix with title, once, copy, copied, copyFailed and done; the device
		 * credential's by default (a user's temporary password uses users.temporaryPassword).
		 */
		words?: string;
	}

	let { code, credential, onDone, words = 'devices.credential' }: Props = $props();

	let box = $state<HTMLElement>();

	async function copy(): Promise<void> {
		try {
			await navigator.clipboard.writeText(credential);
			toast.success($_(`${words}.copied`));
		} catch {
			const selection = window.getSelection();
			if (box && selection) {
				const range = document.createRange();
				range.selectNodeContents(box);
				selection.removeAllRanges();
				selection.addRange(range);
			}
			toast.error($_(`${words}.copyFailed`));
		}
	}
</script>

<!-- CWE-287: the credential exists only in this component's props for as long as it is open; the device view never
     holds it, and closing drops it (the page clears its copy). -->
<section
	data-testid="credential-reveal"
	aria-labelledby="credential-title"
	class="flex flex-col gap-3 rounded-xl border border-status-warning-border bg-status-warning p-4 text-status-warning-foreground"
>
	<div class="flex items-center gap-2">
		<KeyRound class="size-5" aria-hidden="true" />
		<h2 id="credential-title" class="text-base font-semibold">
			{$_(`${words}.title`)}: <span class="font-mono">{code}</span>
		</h2>
	</div>
	<p class="text-sm">{$_(`${words}.once`)}</p>
	<code
		bind:this={box}
		data-testid="credential-value"
		dir="ltr"
		class="rounded-md bg-card px-3 py-2 font-mono text-sm break-all text-foreground select-all"
		>{credential}</code
	>
	<div class="flex flex-wrap gap-2">
		<button
			type="button"
			data-testid="copy-credential"
			onclick={copy}
			class="inline-flex h-9 items-center gap-2 rounded-md border bg-card px-3 text-sm font-medium text-foreground hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			<Copy class="size-4" aria-hidden="true" />
			{$_(`${words}.copy`)}
		</button>
		<button
			type="button"
			data-testid="credential-done"
			onclick={onDone}
			class="inline-flex h-9 items-center rounded-md bg-button-primary px-3 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none dark:text-white"
		>
			{$_(`${words}.done`)}
		</button>
	</div>
</section>
