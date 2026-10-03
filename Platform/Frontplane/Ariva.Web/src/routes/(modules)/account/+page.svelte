<script lang="ts">
	import { ShieldCheck } from '@lucide/svelte';
	import { _ } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import CodeField from '$lib/components/auth/CodeField.svelte';
	import RecoveryCodes from '$lib/components/auth/RecoveryCodes.svelte';
	import SimplePageHeader from '$lib/components/shared/SimplePageHeader.svelte';
	import { Api } from '$lib/core/Api';
	import { auth, authPost, problemText, type TokenAnswer } from '$lib/core/auth.svelte';

	let currentPassword = $state('');
	let newPassword = $state('');
	let passwordErrors = $state<string[]>([]);
	let passwordBusy = $state(false);

	let code = $state('');
	let codes = $state<string[]>([]);
	let recoveryError = $state('');
	let recoveryBusy = $state(false);

	async function changePassword(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		if (passwordBusy) return;
		passwordBusy = true;
		const answer = await authPost<TokenAnswer>('change-password', { currentPassword, newPassword });
		passwordBusy = false;
		if (!answer.ok) {
			passwordErrors =
				answer.problem.status === 429 ? [$_('auth.login.tooMany')] : problemText(answer.problem);
			if (!passwordErrors.length) passwordErrors = [$_('auth.login.unavailable')];
			return;
		}
		auth.accept(answer.data);
		currentPassword = newPassword = '';
		passwordErrors = [];
		toast.success($_('account.password.done'));
	}

	// A critical action (RequiresRecentMfa): Api opens the step-up dialog on 401 mfa_required and sends it once more.
	async function regenerate(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		if (recoveryBusy || code.trim().length !== 6) return;
		recoveryBusy = true;
		recoveryError = '';
		const result = await Api.post<{ recoveryCodes: string[] }>('/api/auth/totp/recovery-codes', {
			code: code.trim()
		});
		recoveryBusy = false;
		code = '';
		if (result.hasErrors || !result.data) {
			recoveryError = $_('account.failed', { values: { reason: result.errorMessages[0] ?? '' } });
			return;
		}
		codes = result.data.recoveryCodes;
	}
</script>

<svelte:head>
	<title>{$_('account.title')} · {$_('app.title')}</title>
</svelte:head>

<SimplePageHeader
	icon={ShieldCheck}
	title={$_('account.title')}
	description={$_('account.description')}
/>

<div class="grid gap-4 xl:grid-cols-2">
	<section class="rounded-xl border bg-card p-5" aria-labelledby="account-password">
		<h2 id="account-password" class="mb-4 text-xl font-semibold">{$_('account.password.title')}</h2>
		<form class="flex max-w-md flex-col gap-4" onsubmit={changePassword}>
			<div class="flex flex-col gap-1.5">
				<label for="account-current" class="text-sm font-medium">{$_('auth.login.password')}</label>
				<input
					id="account-current"
					type="password"
					bind:value={currentPassword}
					autocomplete="current-password"
					maxlength="512"
					required
					class="h-10 rounded-md border border-input bg-background px-3 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
				/>
			</div>
			<div class="flex flex-col gap-1.5">
				<label for="account-new" class="text-sm font-medium">{$_('auth.setup.newPassword')}</label>
				<input
					id="account-new"
					type="password"
					bind:value={newPassword}
					autocomplete="new-password"
					minlength="12"
					maxlength="128"
					required
					aria-describedby="account-rules"
					class="h-10 rounded-md border border-input bg-background px-3 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
				/>
				<p id="account-rules" class="text-xs text-muted-foreground">
					{$_('auth.setup.passwordRules')}
				</p>
			</div>
			{#if passwordErrors.length}
				<ul
					role="alert"
					class="flex flex-col gap-1 rounded-md border border-status-danger-border bg-status-danger px-3 py-2 text-sm text-status-danger-foreground"
				>
					{#each passwordErrors as message, index (index)}
						<li>{message}</li>
					{/each}
				</ul>
			{/if}
			<button
				type="submit"
				disabled={passwordBusy || !currentPassword || !newPassword}
				class="inline-flex h-10 items-center justify-center self-start rounded-md bg-button-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60 dark:text-white"
			>
				{$_('account.password.submit')}
			</button>
		</form>
	</section>

	<section class="rounded-xl border bg-card p-5" aria-labelledby="account-totp">
		<h2 id="account-totp" class="mb-2 text-xl font-semibold">{$_('account.totp.title')}</h2>
		{#if auth.user?.totpEnrolled}
			<p class="mb-5 text-sm text-secondary-foreground">{$_('account.totp.enrolled')}</p>
			<h3 class="mb-1 text-base font-semibold">{$_('account.recovery.title')}</h3>
			{#if codes.length}
				<RecoveryCodes {codes} />
			{:else}
				<p class="mb-4 text-sm text-secondary-foreground">{$_('account.recovery.description')}</p>
				<form
					class="flex max-w-md flex-col gap-4"
					onsubmit={regenerate}
					data-testid="recovery-form"
				>
					<CodeField
						id="account-code"
						bind:value={code}
						allowRecovery={false}
						label={$_('account.recovery.code')}
						error={recoveryError}
						autofocus={false}
					/>
					<button
						type="submit"
						disabled={recoveryBusy || code.trim().length !== 6}
						class="inline-flex h-10 items-center justify-center self-start rounded-md bg-button-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60 dark:text-white"
					>
						{$_('account.recovery.submit')}
					</button>
				</form>
			{/if}
		{:else}
			<p class="mb-4 text-sm text-secondary-foreground">{$_('account.totp.notEnrolled')}</p>
			<a
				href="/setup"
				class="inline-flex h-10 items-center rounded-md bg-button-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none dark:text-white"
			>
				{$_('account.totp.enrol')}
			</a>
		{/if}
	</section>
</div>
