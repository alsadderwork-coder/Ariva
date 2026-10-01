<script lang="ts">
	import { Activity, Languages } from '@lucide/svelte';
	import { _, locale } from 'svelte-i18n';

	// Arabic and English for now; the layout keeps <html lang> and <html dir> in step with the locale.
	function toggleLanguage(): void {
		locale.set($locale?.startsWith('ar') ? 'en' : 'ar');
	}
</script>

<svelte:head>
	<title>{$_('app.title')}</title>
</svelte:head>

<div class="flex min-h-dvh flex-col bg-bg text-ink">
	<header class="flex items-center justify-between border-b border-line px-6 py-4">
		<div class="flex items-center gap-3">
			<span class="size-2.5 rounded-full bg-accent"></span>
			<span class="text-sm font-semibold tracking-[0.3em] uppercase">{$_('app.title')}</span>
		</div>
		<div class="flex items-center gap-4">
			<span class="text-xs text-muted">{$_('app.tagline')}</span>
			<button
				type="button"
				data-testid="language-toggle"
				class="flex items-center gap-2 rounded-md border border-line bg-panel px-3 py-1.5 text-xs text-muted transition-colors hover:border-accent hover:text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent"
				aria-label={$_('app.language.label')}
				onclick={toggleLanguage}
			>
				<Languages class="size-3.5" aria-hidden="true" />
				<span lang={$locale?.startsWith('ar') ? 'en' : 'ar'}>{$_('app.language.toggle')}</span>
			</button>
		</div>
	</header>

	<main class="flex flex-1 items-center justify-center p-6">
		<section class="rounded-lg border border-line bg-panel px-10 py-12 text-center">
			<Activity class="mx-auto mb-4 size-8 text-accent" />
			<h1 class="text-2xl font-semibold">{$_('app.title')}</h1>
			<p class="mt-2 text-sm text-muted">{$_('app.tagline')}</p>
			<div class="mt-6 flex justify-center gap-2" aria-hidden="true">
				<span class="h-1 w-8 rounded-full bg-good"></span>
				<span class="h-1 w-8 rounded-full bg-warn"></span>
				<span class="h-1 w-8 rounded-full bg-crit"></span>
			</div>
		</section>
	</main>
</div>
