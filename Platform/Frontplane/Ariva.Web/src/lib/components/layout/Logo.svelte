<script lang="ts">
	import { cn } from '$lib/utils';

	let { class: className = '' }: { class?: string } = $props();
</script>

<!-- Same construction as Aman.Web's Logo: a rounded square in the sidebar primary with a light glyph. -->
<svg
	width="32"
	height="32"
	viewBox="0 0 32 32"
	xmlns="http://www.w3.org/2000/svg"
	class={cn('fill-sidebar-primary', className)}
	aria-hidden="true"
>
	<rect width="32" height="32" rx="8" />
	<path
		d="M10.4 22L14.496 10.8H16.64L20.736 22H18.704L15.552 13.04L12.4 22H10.4ZM12.224 19.36L12.736 17.84H18.256L18.768 19.36H12.224Z"
		class="fill-sidebar-primary-foreground"
	/>
	<circle cx="24" cy="8" r="2.5" class="fill-sidebar-primary-foreground" />
</svg>
