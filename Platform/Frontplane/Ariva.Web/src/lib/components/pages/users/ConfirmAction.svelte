<script lang="ts">
	import type { Component } from 'svelte';
	import { _ } from 'svelte-i18n';
	import { cn } from '$lib/utils';

	interface Props {
		label: string;
		confirmLabel: string;
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		icon: Component<any>;
		testId: string;
		disabled?: boolean;
		/** True for an action that takes something away (disable, reset): the confirmation is drawn as a warning. */
		warn?: boolean;
		onConfirm: () => void | Promise<void>;
	}

	let {
		label,
		confirmLabel,
		icon: Icon,
		testId,
		disabled = false,
		warn = true,
		onConfirm
	}: Props = $props();
	let asking = $state(false);
	let busy = $state(false);
</script>

<!-- An account action asks on the page (no browser dialog); the server still checks it, and a critical one asks for a
     fresh second factor through the step-up dialog. -->
{#if asking}
	<span class="inline-flex flex-wrap gap-1.5">
		<button
			type="button"
			data-testid="confirm-{testId}"
			disabled={busy}
			onclick={async () => {
				busy = true;
				await onConfirm();
				busy = false;
				asking = false;
			}}
			class={cn(
				'inline-flex h-9 items-center gap-1.5 rounded-md border px-2.5 text-sm font-medium focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60',
				warn
					? 'border-status-warning-border bg-status-warning text-status-warning-foreground'
					: 'bg-card hover:bg-accent'
			)}
		>
			<Icon class="size-4" aria-hidden="true" />
			{confirmLabel}
		</button>
		<button
			type="button"
			onclick={() => (asking = false)}
			class="inline-flex h-9 items-center rounded-md border bg-card px-2.5 text-sm hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			{$_('users.actions.cancel')}
		</button>
	</span>
{:else}
	<button
		type="button"
		data-testid={testId}
		{disabled}
		onclick={() => (asking = true)}
		class="inline-flex h-9 items-center gap-1.5 rounded-md border bg-card px-2.5 text-sm hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:cursor-not-allowed disabled:opacity-50"
	>
		<Icon class="size-4" aria-hidden="true" />
		{label}
	</button>
{/if}
