<script lang="ts">
	import { untrack } from 'svelte';
	import { _ } from 'svelte-i18n';
	import { auth } from '$lib/core/auth.svelte';
	import {
		correctCount,
		isKeyReused,
		isRetryable,
		maxCrossings,
		maxReasonLength,
		newKey,
		type ManualCount
	} from '$lib/core/validation';
	import { field, primaryButton, secondaryButton } from './ui';

	interface Props {
		siteCode: string;
		count: ManualCount;
		binLabel: string;
		onSaved: (count: ManualCount) => void;
		onCancel: () => void;
		/** The count changed on the server (its key was used for another correction): the panel says so and reloads. */
		onStale: (message: string) => void;
	}

	let { siteCode, count, binLabel, onSaved, onCancel, onStale }: Props = $props();

	type Body = { crossingsIn: number; crossingsOut: number; reason: string };

	// The form starts from the count it corrects; it is remounted (keyed) for another one.
	const start = untrack(() => count);
	const owner = untrack(() => auth.subject) ?? '';
	let crossingsIn = $state(String(start.crossingsIn));
	let crossingsOut = $state(String(start.crossingsOut));
	let reason = $state('');
	let problem = $state('');
	let saving = $state(false);
	/**
	 * The body sent with the current key while its outcome is unknown (no answer, a timeout, 408, 429 or 5xx). The inputs
	 * are frozen until it is settled, so the key is only ever sent again with this same body (security review M2): Send
	 * again repeats it as it is, and Ariva returns the stored revision if the first send was recorded. A definite answer
	 * (saved, or refused) unfreezes the form, and the next body gets a new key.
	 */
	let unsettled = $state<Body | null>(null);
	let key = newKey();
	const frozen = $derived(saving || unsettled !== null);

	const whole = (value: string): number | null => {
		const trimmed = value.trim();
		if (!/^[0-9]{1,5}$/.test(trimmed)) return null;
		const n = Number(trimmed);
		return n <= maxCrossings ? n : null;
	};

	function read(): Body | null {
		const inValue = whole(crossingsIn);
		const outValue = whole(crossingsOut);
		const text = reason.trim();
		if (inValue === null || outValue === null || text.length < 1 || text.length > maxReasonLength)
			return null;
		return { crossingsIn: inValue, crossingsOut: outValue, reason: text };
	}

	async function save(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		if (saving) return;
		const body = unsettled ?? read();
		if (!body) {
			problem = $_('validation.correction.invalid');
			return;
		}
		unsettled = body;
		saving = true;
		const result = await correctCount(siteCode, count.campaignId, count.id, body, key, owner);
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
			onStale($_('validation.correction.keyReused'));
			return;
		}
		problem = $_('validation.correction.failed', { values: { reason: message } });
	}
</script>

<form
	data-testid="correction-form"
	onsubmit={save}
	aria-labelledby="correction-title"
	class="flex flex-col gap-3 rounded-lg border bg-surface-2 p-4"
>
	<h3 id="correction-title" class="text-base font-semibold">
		{$_('validation.correction.title', { values: { bin: binLabel } })}
	</h3>
	<div class="grid gap-3 sm:grid-cols-2">
		<div class="flex flex-col gap-1">
			<label for="correction-in" class="text-sm font-medium">{$_('validation.correction.in')}</label
			>
			<input
				id="correction-in"
				data-testid="correction-in"
				inputmode="numeric"
				autocomplete="off"
				maxlength="5"
				readonly={frozen}
				bind:value={crossingsIn}
				class={field}
			/>
		</div>
		<div class="flex flex-col gap-1">
			<label for="correction-out" class="text-sm font-medium"
				>{$_('validation.correction.out')}</label
			>
			<input
				id="correction-out"
				data-testid="correction-out"
				inputmode="numeric"
				autocomplete="off"
				maxlength="5"
				readonly={frozen}
				bind:value={crossingsOut}
				class={field}
			/>
		</div>
	</div>
	<div class="flex flex-col gap-1">
		<label for="correction-reason" class="text-sm font-medium"
			>{$_('validation.correction.reason')}</label
		>
		<input
			id="correction-reason"
			data-testid="correction-reason"
			autocomplete="off"
			maxlength={maxReasonLength}
			readonly={frozen}
			aria-describedby="correction-reason-hint"
			bind:value={reason}
			class={field}
		/>
		<p id="correction-reason-hint" class="text-sm text-muted-foreground">
			{$_('validation.correction.reasonHint')}
		</p>
	</div>
	{#if problem}
		<p role="alert" data-testid="correction-problem" class="text-sm text-status-danger-foreground">
			{problem}
		</p>
	{/if}
	<div class="flex flex-wrap gap-2">
		<button type="submit" data-testid="correction-save" disabled={saving} class={primaryButton}>
			{$_(unsettled && !saving ? 'validation.correction.sendAgain' : 'validation.correction.save')}
		</button>
		<button type="button" onclick={onCancel} class={secondaryButton}>
			{$_('validation.correction.cancel')}
		</button>
	</div>
</form>
