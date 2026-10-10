<script lang="ts">
	import { untrack } from 'svelte';
	import { _ } from 'svelte-i18n';
	import { cn } from '$lib/utils';
	import { auth } from '$lib/core/auth.svelte';
	import {
		correctDesk,
		deskStates,
		isDeskRoleRefusal,
		isKeyReused,
		isOwnCampaignRefusal,
		isRetryable,
		maxReasonLength,
		newKey,
		type DeskObservation,
		type DeskState
	} from '$lib/core/validation';
	import { deskStateIcon, deskStateTone } from './deskStates';
	import { field, primaryButton, secondaryButton } from './ui';

	interface Props {
		siteCode: string;
		observation: DeskObservation;
		/** The desk (checkpoint and code) and the minute, as the grid shows them. */
		deskText: string;
		minuteText: string;
		onSaved: (observation: DeskObservation) => void;
		onCancel: () => void;
		/** The state changed on the server (its key was used for another correction): the panel says so and reloads. */
		onStale: (message: string) => void;
	}

	let { siteCode, observation, deskText, minuteText, onSaved, onCancel, onStale }: Props = $props();

	type Body = { state: DeskState; reason: string };

	// The form corrects the state it was opened on; it is remounted (keyed) for another one. Its calls are bound to the
	// account that opened it, so nothing is sent or recovered under another account (ARV-104c security review M1).
	const start = untrack(() => observation);
	const owner = untrack(() => auth.subject) ?? '';
	let picked = $state<DeskState | null>(null);
	let reason = $state('');
	let problem = $state('');
	let saving = $state(false);
	/**
	 * The body sent with the current key while its outcome is unknown (no answer, a timeout, 408, 429 or 5xx). The inputs
	 * are frozen until it is settled, so the key is only ever sent again with this same body: Send again repeats it as it
	 * is, and Ariva returns the stored revision if the first send was recorded. A definite answer (saved, or refused)
	 * unfreezes the form, and the next body gets a new key (as the count correction, ARV-104c security review M2).
	 */
	let unsettled = $state<Body | null>(null);
	let key = newKey();
	const frozen = $derived(saving || unsettled !== null);
	const shown = $derived(unsettled?.state ?? picked);

	function read(): Body | null {
		const text = reason.trim();
		if (!picked || picked === start.state || text.length < 1 || text.length > maxReasonLength)
			return null;
		return { state: picked, reason: text };
	}

	async function save(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		if (saving) return;
		const body = unsettled ?? read();
		if (!body) {
			problem = $_('validation.desks.correction.invalid');
			return;
		}
		unsettled = body;
		saving = true;
		const result = await correctDesk(siteCode, start.campaignId, start.id, body, key, owner);
		saving = false;
		if (!result.hasErrors && result.data) {
			unsettled = null;
			key = newKey();
			onSaved(result.data);
			return;
		}
		const message = result.errorMessages.join(' ');
		if (isRetryable(result)) {
			// Ariva may have recorded it: keep the body and the key, and say so.
			problem = $_('validation.correction.unknown');
			return;
		}
		// A definite refusal: nothing was recorded with this key, so the next body gets a new one.
		unsettled = null;
		key = newKey();
		if (isKeyReused(result.status, message)) {
			onStale($_('validation.desks.correction.keyReused'));
			return;
		}
		// Recognised refusals in plain words; any other (another 403 included) with Ariva's own text.
		if (isDeskRoleRefusal(result.status, message)) {
			problem = $_('validation.desks.deskRole');
			return;
		}
		if (isOwnCampaignRefusal(result.status, message)) {
			problem = $_('validation.desks.ownCampaign');
			return;
		}
		problem = $_('validation.correction.failed', { values: { reason: message } });
	}
</script>

<form
	data-testid="desk-correction-form"
	onsubmit={save}
	aria-labelledby="desk-correction-title"
	class="flex flex-col gap-3 rounded-lg border bg-surface-2 p-4"
>
	<h3 id="desk-correction-title" class="text-base font-semibold break-all">
		{$_('validation.desks.correction.title', { values: { desk: deskText, time: minuteText } })}
	</h3>
	<p class="text-sm text-secondary-foreground" data-testid="desk-correction-recorded">
		{$_('validation.desks.correction.recorded', {
			values: {
				state: $_(`validation.desks.states.${start.state}`),
				revision: start.revision
			}
		})}
	</p>
	<fieldset class="flex flex-col gap-2">
		<legend class="mb-1 text-sm font-medium">{$_('validation.desks.correction.state')}</legend>
		<div class="grid grid-cols-2 gap-2 sm:grid-cols-4">
			{#each deskStates as option (option)}
				{@const Icon = deskStateIcon[option]}
				<label
					class={cn(
						'flex min-h-14 cursor-pointer items-center justify-center gap-2 rounded-lg border px-2 text-sm font-medium has-[:disabled]:cursor-not-allowed has-[:focus-visible]:ring-2 has-[:focus-visible]:ring-ring',
						shown === option ? cn(deskStateTone[option], 'border-2') : 'bg-card text-foreground'
					)}
				>
					<input
						type="radio"
						name="desk-correction-state"
						value={option}
						data-testid="desk-correction-state"
						disabled={frozen || option === start.state}
						checked={shown === option}
						onchange={() => (picked = option)}
						class="sr-only"
					/>
					<Icon class="size-5" aria-hidden="true" />
					{$_(`validation.desks.states.${option}`)}
				</label>
			{/each}
		</div>
	</fieldset>
	<div class="flex flex-col gap-1">
		<label for="desk-correction-reason" class="text-sm font-medium"
			>{$_('validation.correction.reason')}</label
		>
		<input
			id="desk-correction-reason"
			data-testid="desk-correction-reason"
			autocomplete="off"
			maxlength={maxReasonLength}
			readonly={frozen}
			aria-describedby="desk-correction-reason-hint"
			bind:value={reason}
			class={field}
		/>
		<p id="desk-correction-reason-hint" class="text-sm text-muted-foreground">
			{$_('validation.desks.correction.reasonHint')}
		</p>
	</div>
	{#if problem}
		<p
			role="alert"
			data-testid="desk-correction-problem"
			class="text-sm text-status-danger-foreground"
		>
			{problem}
		</p>
	{/if}
	<div class="flex flex-wrap gap-2">
		<button
			type="submit"
			data-testid="desk-correction-save"
			disabled={saving}
			class={primaryButton}
		>
			{$_(unsettled && !saving ? 'validation.correction.sendAgain' : 'validation.correction.save')}
		</button>
		<button type="button" onclick={onCancel} class={secondaryButton}>
			{$_('validation.correction.cancel')}
		</button>
	</div>
</form>
