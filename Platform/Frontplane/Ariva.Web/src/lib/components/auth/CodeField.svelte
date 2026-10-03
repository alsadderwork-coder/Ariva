<script lang="ts">
	import { _ } from 'svelte-i18n';

	interface Props {
		id: string;
		value: string;
		/** True when the user types a recovery code instead of the authenticator code. */
		recovery?: boolean;
		/** Offer the switch to a recovery code (sign-in and step-up do; confirming an enrolment does not). */
		allowRecovery?: boolean;
		label?: string;
		error?: string;
		autofocus?: boolean;
	}

	let {
		id,
		value = $bindable(''),
		recovery = $bindable(false),
		allowRecovery = true,
		label,
		error = '',
		autofocus = true
	}: Props = $props();

	let input = $state<HTMLInputElement>();

	$effect(() => {
		if (autofocus) input?.focus();
	});

	function toggle(): void {
		recovery = !recovery;
		value = '';
		input?.focus();
	}
</script>

<!-- A TOTP code (6 digits, one-time-code autofill) or a recovery code; errors are announced and tied to the field. -->
<div class="flex flex-col gap-1.5">
	<label for={id} class="text-sm font-medium">
		{label ?? (recovery ? $_('auth.login.recoveryCode') : $_('auth.login.code'))}
	</label>
	{#if recovery}
		<input
			bind:this={input}
			{id}
			name="recoveryCode"
			bind:value
			autocomplete="off"
			autocapitalize="characters"
			spellcheck="false"
			maxlength="32"
			required
			aria-invalid={error ? 'true' : undefined}
			aria-describedby="{id}-hint{error ? ` ${id}-error` : ''}"
			class="h-10 rounded-md border border-input bg-background px-3 font-mono text-sm tracking-wider focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		/>
		<p id="{id}-hint" class="text-xs text-muted-foreground">{$_('auth.login.recoveryHint')}</p>
	{:else}
		<input
			bind:this={input}
			{id}
			name="code"
			bind:value
			inputmode="numeric"
			autocomplete="one-time-code"
			pattern="[0-9]{'{'}6{'}'}"
			maxlength="6"
			required
			aria-invalid={error ? 'true' : undefined}
			aria-describedby="{id}-hint{error ? ` ${id}-error` : ''}"
			class="h-10 rounded-md border border-input bg-background px-3 font-mono text-base tracking-[0.3em] tabular-nums focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		/>
		<p id="{id}-hint" class="text-xs text-muted-foreground">{$_('auth.login.codeHint')}</p>
	{/if}
	{#if error}
		<p id="{id}-error" role="alert" class="text-sm text-status-danger-foreground">{error}</p>
	{/if}
	{#if allowRecovery}
		<button
			type="button"
			onclick={toggle}
			class="self-start text-xs font-medium text-primary underline-offset-4 hover:underline focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none dark:text-foreground"
		>
			{recovery ? $_('auth.login.useCode') : $_('auth.login.useRecovery')}
		</button>
	{/if}
</div>
