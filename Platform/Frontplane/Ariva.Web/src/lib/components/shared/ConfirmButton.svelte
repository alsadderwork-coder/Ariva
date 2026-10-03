<script lang="ts">
	import { Trash2 } from '@lucide/svelte';
	import { _ } from 'svelte-i18n';

	interface Props {
		label: string;
		confirmLabel: string;
		disabled?: boolean;
		testId?: string;
		onConfirm: () => void | Promise<void>;
	}

	let { label, confirmLabel, disabled = false, testId = 'delete', onConfirm }: Props = $props();
	let asking = $state(false);
</script>

<!-- A destructive action asks on the page (no browser dialog). -->
{#if asking}
	<span class="inline-flex flex-wrap gap-2">
		<button
			type="button"
			data-testid="confirm-{testId}"
			{disabled}
			onclick={async () => {
				await onConfirm();
				asking = false;
			}}
			class="inline-flex h-9 items-center gap-2 rounded-md border border-status-danger-border bg-status-danger px-3 text-sm font-medium text-status-danger-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			<Trash2 class="size-4" aria-hidden="true" />
			{confirmLabel}
		</button>
		<button
			type="button"
			onclick={() => (asking = false)}
			class="inline-flex h-9 items-center rounded-md border bg-card px-3 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			{$_('topology.actions.cancel')}
		</button>
	</span>
{:else}
	<button
		type="button"
		data-testid={testId}
		{disabled}
		onclick={() => (asking = true)}
		class="inline-flex h-9 items-center gap-2 rounded-md border bg-card px-3 text-sm font-medium text-status-danger-foreground hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
	>
		<Trash2 class="size-4" aria-hidden="true" />
		{label}
	</button>
{/if}
