<script lang="ts">
	import '../app.css';
	import type { Snippet } from 'svelte';
	import { locale } from 'svelte-i18n';
	import { Toaster } from 'svelte-sonner';
	import { directionFor, setupI18n } from '$lib/i18n';

	let { children }: { children: Snippet } = $props();

	setupI18n();

	// Keep <html lang> and <html dir> in step with the active locale so Arabic renders right to left.
	$effect(() => {
		document.documentElement.lang = $locale ?? 'en';
		document.documentElement.dir = directionFor($locale);
	});
</script>

<Toaster theme="dark" position="top-center" dir={directionFor($locale)} />

{@render children()}
