<script lang="ts">
	import '@fontsource-variable/dm-sans';
	import '@fontsource-variable/noto-sans-arabic';
	import '../app.css';
	import type { Snippet } from 'svelte';
	import { locale } from 'svelte-i18n';
	import { Toaster } from 'svelte-sonner';
	import { directionFor, setupI18n } from '$lib/i18n';
	import { theme } from '$lib/theme/theme.svelte';

	let { children }: { children: Snippet } = $props();

	setupI18n();
	theme.init();

	// Keep <html lang> and <html dir> in step with the active locale so Arabic renders right to left.
	$effect(() => {
		document.documentElement.lang = $locale ?? 'en';
		document.documentElement.dir = directionFor($locale);
	});

	// Aman.Web places toasts top right, mirrored to top left in Arabic.
	const toastPosition = $derived(directionFor($locale) === 'rtl' ? 'top-left' : 'top-right');
</script>

<Toaster
	theme={theme.resolved}
	richColors
	closeButton
	position={toastPosition}
	dir={directionFor($locale)}
/>

<!-- The signed-in screens are under (modules), with the shell; sign-in and first-login are under (public). -->
{@render children()}
