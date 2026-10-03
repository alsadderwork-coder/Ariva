<script lang="ts">
	import { _ } from 'svelte-i18n';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import type { User } from '$lib/core/users';

	let { user }: { user: User } = $props();
</script>

<!-- Every state is words with its colour, never colour alone. -->
<span class="flex flex-wrap gap-1" data-testid="user-status">
	{#if user.isDisabled}
		<StatusBadge tone="danger" label={$_('users.status.disabled')} />
	{:else}
		<StatusBadge tone="success" label={$_('users.status.active')} />
	{/if}
	{#if user.isLocked}<StatusBadge tone="warning" label={$_('users.status.locked')} />{/if}
	{#if user.mustChangePassword}<StatusBadge
			tone="info"
			label={$_('users.status.mustChange')}
		/>{/if}
	{#if !user.totpEnrolled}<StatusBadge tone="neutral" label={$_('users.status.noTotp')} />{/if}
</span>
