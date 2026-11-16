<script lang="ts">
	import { cn } from '$lib/utils';

	let { class: className = '' }: { class?: string } = $props();
</script>

<!-- A placeholder block while content loads (ARV-299a). Decorative: the status text beside it says what is loading. The
     pulse stops for people who ask for less motion. -->
<span
	aria-hidden="true"
	class={cn('block h-3.5 animate-pulse rounded-md bg-muted motion-reduce:animate-none', className)}
></span>
