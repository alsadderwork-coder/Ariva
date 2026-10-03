<script lang="ts">
	import { goto } from '$app/navigation';
	import { onMount } from 'svelte';
	import { KeyRound } from '@lucide/svelte';
	import { _ } from 'svelte-i18n';
	import CodeField from '$lib/components/auth/CodeField.svelte';
	import QrCode from '$lib/components/auth/QrCode.svelte';
	import RecoveryCodes from '$lib/components/auth/RecoveryCodes.svelte';
	import { auth, authPost, problemText, type TokenAnswer } from '$lib/core/auth.svelte';
	import { cn } from '$lib/utils';

	type Step = 'loading' | 'password' | 'totp' | 'recovery';
	interface Enrolment {
		secret: string;
		otpAuthUri: string;
	}
	interface Confirmed {
		token: TokenAnswer;
		recoveryCodes: string[];
	}

	let step = $state<Step>('loading');
	let currentPassword = $state('');
	let newPassword = $state('');
	let repeatPassword = $state('');
	let enrolment = $state<Enrolment | null>(null);
	let code = $state('');
	let recoveryCodes = $state<string[]>([]);
	let saved = $state(false);
	let errors = $state<string[]>([]);
	let busy = $state(false);

	const steps: { id: Exclude<Step, 'loading'>; label: string }[] = $derived([
		{ id: 'password', label: $_('auth.setup.stepPassword') },
		{ id: 'totp', label: $_('auth.setup.stepTotp') },
		{ id: 'recovery', label: $_('auth.setup.stepRecovery') }
	]);
	// The manual key in groups of four, as authenticator apps show it.
	const manualKey = $derived(enrolment?.secret.match(/.{1,4}/g)?.join(' ') ?? '');

	/** The next step for the user as the server sees them; home when nothing is left. */
	async function advance(): Promise<void> {
		const user = await auth.loadUser();
		if (!user) {
			await auth.sessionEnded('/');
			return;
		}
		errors = [];
		if (user.mustChangePassword) {
			step = 'password';
		} else if (!user.totpEnrolled) {
			step = 'totp';
			await enrol();
		} else {
			await goto('/', { replaceState: true });
		}
	}

	onMount(() => {
		void auth.start().then((signedIn) => {
			if (!signedIn) void goto('/login', { replaceState: true });
			else void advance();
		});
	});

	async function changePassword(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		if (busy) return;
		if (newPassword !== repeatPassword) {
			errors = [$_('auth.setup.mismatch')];
			return;
		}
		busy = true;
		const answer = await authPost<TokenAnswer>('change-password', { currentPassword, newPassword });
		busy = false;
		if (!answer.ok) {
			errors =
				answer.problem.status === 429 ? [$_('auth.login.tooMany')] : problemText(answer.problem);
			if (!errors.length) errors = [$_('auth.login.unavailable')];
			return;
		}
		auth.accept(answer.data);
		currentPassword = newPassword = repeatPassword = '';
		await advance();
	}

	async function enrol(): Promise<void> {
		const answer = await authPost<Enrolment>('totp/enroll');
		if (answer.ok) {
			enrolment = answer.data;
		} else if (answer.problem.status === 409) {
			await goto('/', { replaceState: true });
		} else {
			errors = [$_('auth.login.unavailable')];
		}
	}

	async function confirm(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		if (busy || !code.trim()) return;
		busy = true;
		const answer = await authPost<Confirmed>('totp/confirm', { code: code.trim() });
		busy = false;
		if (!answer.ok) {
			errors = [
				answer.problem.status === 429 ? $_('auth.login.tooMany') : $_('auth.setup.codeRefused')
			];
			code = '';
			return;
		}
		auth.accept(answer.data.token);
		enrolment = null;
		code = '';
		errors = [];
		recoveryCodes = answer.data.recoveryCodes;
		step = 'recovery';
	}

	async function finish(): Promise<void> {
		recoveryCodes = [];
		await auth.loadUser();
		await goto('/', { replaceState: true });
	}
</script>

<svelte:head>
	<title>{$_('auth.setup.title')} · {$_('app.title')}</title>
</svelte:head>

<section
	class="w-full max-w-lg rounded-xl border bg-card p-6 shadow-sm md:p-8"
	aria-labelledby="setup-title"
>
	<div class="mb-6 flex items-center gap-3">
		<div
			class="flex size-11 shrink-0 items-center justify-center rounded-[9px] bg-primary/10 text-primary dark:bg-button-primary/20 dark:text-foreground"
		>
			<KeyRound class="size-5.5" aria-hidden="true" />
		</div>
		<div>
			<h1 id="setup-title" class="text-[28px] leading-none font-semibold tracking-[-0.14px]">
				{$_('auth.setup.title')}
			</h1>
			<p class="mt-1 text-[13px] text-secondary-foreground">{$_('auth.setup.description')}</p>
		</div>
	</div>

	<ol class="mb-6 flex gap-2 text-xs font-medium" aria-label={$_('auth.setup.steps')}>
		{#each steps as item, index (item.id)}
			<li
				aria-current={step === item.id ? 'step' : undefined}
				class={cn(
					'flex flex-1 items-center gap-2 rounded-md border px-2 py-1.5 text-muted-foreground',
					step === item.id &&
						'border-primary bg-primary/10 text-foreground dark:border-foreground/40'
				)}
			>
				<span class="tabular-nums">{index + 1}</span>
				<span class="truncate">{item.label}</span>
			</li>
		{/each}
	</ol>

	{#if errors.length}
		<ul
			role="alert"
			data-testid="setup-error"
			class="mb-4 flex flex-col gap-1 rounded-md border border-status-danger-border bg-status-danger px-3 py-2 text-sm text-status-danger-foreground"
		>
			{#each errors as message, index (index)}
				<li>{message}</li>
			{/each}
		</ul>
	{/if}

	{#if step === 'loading'}
		<p class="text-sm text-muted-foreground" role="status">{$_('auth.loading')}</p>
	{:else if step === 'password'}
		<form class="flex flex-col gap-4" onsubmit={changePassword} data-testid="password-form">
			<div class="flex flex-col gap-1.5">
				<label for="setup-current" class="text-sm font-medium"
					>{$_('auth.setup.currentPassword')}</label
				>
				<input
					id="setup-current"
					type="password"
					bind:value={currentPassword}
					autocomplete="current-password"
					maxlength="512"
					required
					class="h-10 rounded-md border border-input bg-background px-3 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
				/>
			</div>
			<div class="flex flex-col gap-1.5">
				<label for="setup-new" class="text-sm font-medium">{$_('auth.setup.newPassword')}</label>
				<input
					id="setup-new"
					type="password"
					bind:value={newPassword}
					autocomplete="new-password"
					minlength="12"
					maxlength="128"
					required
					aria-describedby="setup-rules"
					class="h-10 rounded-md border border-input bg-background px-3 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
				/>
				<p id="setup-rules" class="text-xs text-muted-foreground">
					{$_('auth.setup.passwordRules')}
				</p>
			</div>
			<div class="flex flex-col gap-1.5">
				<label for="setup-repeat" class="text-sm font-medium"
					>{$_('auth.setup.repeatPassword')}</label
				>
				<input
					id="setup-repeat"
					type="password"
					bind:value={repeatPassword}
					autocomplete="new-password"
					maxlength="128"
					required
					class="h-10 rounded-md border border-input bg-background px-3 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
				/>
			</div>
			<button
				type="submit"
				disabled={busy || !currentPassword || !newPassword || !repeatPassword}
				class="inline-flex h-10 items-center justify-center rounded-md bg-button-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60 dark:text-white"
			>
				{$_('auth.setup.savePassword')}
			</button>
		</form>
	{:else if step === 'totp'}
		<div class="flex flex-col gap-4">
			<p class="text-sm text-secondary-foreground">{$_('auth.setup.totpIntro')}</p>
			{#if enrolment}
				<div class="flex flex-col items-center gap-3 sm:flex-row sm:items-start">
					<QrCode value={enrolment.otpAuthUri} label={$_('auth.setup.qrLabel')} />
					<div class="flex min-w-0 flex-col gap-1">
						<span class="text-xs font-semibold tracking-wide text-tertiary uppercase"
							>{$_('auth.setup.manualKey')}</span
						>
						<code
							data-testid="totp-secret"
							dir="ltr"
							class="rounded-md bg-surface-2 px-2 py-1 font-mono text-sm break-all select-all"
							>{manualKey}</code
						>
					</div>
				</div>
				<form class="flex flex-col gap-4" onsubmit={confirm} data-testid="totp-form">
					<CodeField
						id="setup-code"
						bind:value={code}
						allowRecovery={false}
						label={$_('auth.setup.confirmCode')}
						autofocus={false}
					/>
					<div class="flex flex-wrap gap-2">
						<button
							type="submit"
							disabled={busy || code.trim().length !== 6}
							class="inline-flex h-10 items-center justify-center rounded-md bg-button-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60 dark:text-white"
						>
							{$_('auth.setup.confirm')}
						</button>
						{#if auth.user && !auth.user.pending}
							<!-- Only when the deployment does not require an authenticator (Auth:TotpRequired false). -->
							<button
								type="button"
								onclick={() => goto('/', { replaceState: true })}
								class="inline-flex h-10 items-center rounded-md border bg-card px-4 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
							>
								{$_('auth.setup.skip')}
							</button>
						{/if}
					</div>
				</form>
			{/if}
		</div>
	{:else if step === 'recovery'}
		<div class="flex flex-col gap-4">
			<RecoveryCodes codes={recoveryCodes} />
			<label class="flex items-center gap-2 text-sm">
				<input type="checkbox" bind:checked={saved} class="size-4 rounded border-input" />
				{$_('auth.setup.saved')}
			</label>
			<button
				type="button"
				data-testid="setup-finish"
				disabled={!saved}
				onclick={finish}
				class="inline-flex h-10 items-center justify-center rounded-md bg-button-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60 dark:text-white"
			>
				{$_('auth.setup.continue')}
			</button>
		</div>
	{/if}
</section>
