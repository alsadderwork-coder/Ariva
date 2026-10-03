<script lang="ts">
	import { _ } from 'svelte-i18n';
	import { auth, authPost, stepUp, type TokenAnswer } from '$lib/core/auth.svelte';
	import CodeField from './CodeField.svelte';

	let dialog = $state<HTMLDialogElement>();
	let code = $state('');
	let recovery = $state(false);
	let error = $state('');
	let busy = $state(false);

	// The dialog follows the step-up request: open while an action waits for a fresh second factor.
	$effect(() => {
		if (!dialog) return;
		if (stepUp.open && !dialog.open) {
			code = '';
			error = '';
			recovery = false;
			dialog.showModal();
		} else if (!stepUp.open && dialog.open) {
			dialog.close();
		}
	});

	async function confirm(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		if (!code.trim() || busy) return;
		busy = true;
		error = '';
		try {
			const answer = await authPost<TokenAnswer>(
				'step-up',
				recovery ? { recoveryCode: code.trim() } : { code: code.trim() }
			);
			if (answer.ok) {
				auth.accept(answer.data);
				stepUp.finish(true);
				return;
			}
			error =
				answer.problem.status === 429
					? $_('auth.login.tooMany')
					: answer.problem.status === 0
						? $_('auth.login.unavailable')
						: $_('auth.stepUp.refused');
			code = '';
		} catch {
			error = $_('auth.login.unavailable');
		} finally {
			busy = false;
		}
	}

	function cancel(): void {
		stepUp.finish(false);
	}
</script>

<!-- RFC 9470 step-up (ARV-051): a native modal dialog, so focus stays inside it and Escape cancels. -->
<dialog
	bind:this={dialog}
	data-testid="step-up-dialog"
	aria-labelledby="step-up-title"
	aria-describedby="step-up-description"
	oncancel={(event) => {
		event.preventDefault();
		cancel();
	}}
	class="m-auto w-[min(26rem,calc(100vw-2rem))] rounded-xl border bg-card p-6 text-card-foreground shadow-xl backdrop:bg-black/40"
>
	<form class="flex flex-col gap-4" onsubmit={confirm}>
		<div>
			<h2 id="step-up-title" class="text-xl font-semibold">{$_('auth.stepUp.title')}</h2>
			<p id="step-up-description" class="mt-1 text-sm text-secondary-foreground">
				{$_('auth.stepUp.description')}
			</p>
		</div>
		<CodeField id="step-up-code" bind:value={code} bind:recovery {error} />
		<div class="flex justify-end gap-2">
			<button
				type="button"
				onclick={cancel}
				class="inline-flex h-10 items-center rounded-md border bg-card px-4 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
				>{$_('auth.stepUp.cancel')}</button
			>
			<button
				type="submit"
				disabled={busy}
				class="inline-flex h-10 items-center rounded-md bg-button-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60 dark:text-white"
				>{$_('auth.stepUp.confirm')}</button
			>
		</div>
	</form>
</dialog>
