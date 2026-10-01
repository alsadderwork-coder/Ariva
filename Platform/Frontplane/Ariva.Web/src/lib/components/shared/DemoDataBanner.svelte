<script lang="ts">
	import { Info } from '@lucide/svelte';

	let { text }: { text: string } = $props();
</script>

<div
	data-testid="demo-banner"
	role="note"
	class="mb-4 flex items-start gap-2 rounded-lg border border-status-info-border bg-status-info px-4 py-3 text-sm text-status-info-foreground"
>
	<Info class="mt-0.5 size-4 shrink-0" aria-hidden="true" />
	<p>{text}</p>
</div>
