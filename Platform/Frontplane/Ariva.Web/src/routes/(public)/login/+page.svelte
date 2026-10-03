<script lang="ts">
	import { goto } from '$app/navigation';
	import { onMount } from 'svelte';
	import { page } from '$app/state';
	import { LogIn } from '@lucide/svelte';
	import { _ } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import CodeField from '$lib/components/auth/CodeField.svelte';
	import { auth, authPost, safeNext, type TokenAnswer } from '$lib/core/auth.svelte';

	let userName = $state('');
	let password = $state('');
	let code = $state('');
	let recovery = $state(false);
	let needsCode = $state(false);
	let error = $state('');
	let busy = $state(false);

	// Only a path on this site (no scheme, no //, no backslash): a crafted link cannot send the user elsewhere.
	const next = $derived(safeNext(page.url.searchParams.get('next')));

	/** Where a signed-in user goes: the first-login steps while pending, otherwise the remembered page. */
	async function proceed(): Promise<void> {
		const user = await auth.loadUser();
		if (!user) {
			error = $_('auth.login.unavailable');
			return;
		}
		await goto(user.pending ? '/setup' : next, { replaceState: true });
	}

	// A tab that already has a session (refresh cookie) skips the form; the form shows once that is known.
	let checking = $state(true);
	onMount(() => {
		void auth.start().then((signedIn) => {
			if (signedIn) void proceed();
			else checking = false;
		});
	});

	async function submit(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		if (busy) return;
		busy = true;
		error = '';
		try {
			const body: Record<string, string> = { userName: userName.trim(), password };
			if (needsCode && code.trim()) body[recovery ? 'recoveryCode' : 'code'] = code.trim();
			const answer = await authPost<TokenAnswer>('login', body);
			if (answer.ok) {
				auth.accept(answer.data);
				password = '';
				code = '';
				if (typeof answer.data.recoveryCodesRemaining === 'number') {
					toast.warning(
						$_('auth.login.recoveryLeft', { values: { count: answer.data.recoveryCodesRemaining } })
					);
				}
				await proceed();
				return;
			}
			const problem = answer.problem;
			if (problem.status === 401 && problem.error === 'mfa_required' && !needsCode) {
				needsCode = true;
			} else if (problem.status === 429) {
				error = $_('auth.login.tooMany');
			} else if (problem.status === 401 || problem.status === 400) {
				error =
					needsCode && problem.error === 'mfa_required'
						? $_('auth.login.codeNeeded')
						: $_('auth.login.failed');
				code = '';
			} else {
				error = $_('auth.login.unavailable');
			}
		} catch {
			error = $_('auth.login.unavailable');
		} finally {
			busy = false;
		}
	}
</script>

<svelte:head>
	<title>{$_('auth.login.title')} · {$_('app.title')}</title>
</svelte:head>

<section
	class="w-full max-w-sm rounded-xl border bg-card p-6 shadow-sm md:p-8"
	aria-labelledby="login-title"
>
	<div class="mb-6 flex items-center gap-3">
		<div
			class="flex size-11 shrink-0 items-center justify-center rounded-[9px] bg-primary/10 text-primary dark:bg-button-primary/20 dark:text-foreground"
		>
			<LogIn class="size-5.5 rtl:-scale-x-100" aria-hidden="true" />
		</div>
		<div>
			<h1 id="login-title" class="text-[28px] leading-none font-semibold tracking-[-0.14px]">
				{$_('auth.login.title')}
			</h1>
			<p class="mt-1 text-[13px] text-secondary-foreground">{$_('auth.login.description')}</p>
		</div>
	</div>

	{#if checking}
		<p class="text-sm text-muted-foreground" role="status">{$_('auth.loading')}</p>
	{:else}
		<form class="flex flex-col gap-4" onsubmit={submit} data-testid="login-form" novalidate>
			<div class="flex flex-col gap-1.5">
				<label for="login-user" class="text-sm font-medium">{$_('auth.login.userName')}</label>
				<input
					id="login-user"
					name="username"
					bind:value={userName}
					autocomplete="username"
					autocapitalize="none"
					spellcheck="false"
					maxlength="256"
					required
					readonly={needsCode}
					class="h-10 rounded-md border border-input bg-background px-3 text-sm read-only:bg-muted focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
				/>
			</div>
			<div class="flex flex-col gap-1.5">
				<label for="login-password" class="text-sm font-medium">{$_('auth.login.password')}</label>
				<input
					id="login-password"
					name="password"
					type="password"
					bind:value={password}
					autocomplete="current-password"
					maxlength="512"
					required
					readonly={needsCode}
					class="h-10 rounded-md border border-input bg-background px-3 text-sm read-only:bg-muted focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
				/>
			</div>
			{#if needsCode}
				<CodeField id="login-code" bind:value={code} bind:recovery />
			{/if}
			{#if error}
				<p
					role="alert"
					data-testid="login-error"
					class="rounded-md border border-status-danger-border bg-status-danger px-3 py-2 text-sm text-status-danger-foreground"
				>
					{error}
				</p>
			{/if}
			<button
				type="submit"
				disabled={busy || !userName.trim() || !password}
				class="inline-flex h-10 items-center justify-center rounded-md bg-button-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60 dark:text-white"
			>
				{busy ? $_('auth.login.working') : $_('auth.login.submit')}
			</button>
		</form>
	{/if}
</section>
