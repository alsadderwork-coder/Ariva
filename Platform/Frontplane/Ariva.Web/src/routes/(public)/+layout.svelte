<script lang="ts">
	import type { Snippet } from 'svelte';
	import PublicFrame from '$lib/components/auth/PublicFrame.svelte';

	let { children }: { children: Snippet } = $props();
</script>

<PublicFrame>
	{@render children()}
</PublicFrame>
