<script lang="ts">
	import { Languages, Moon, Sun } from '@lucide/svelte';
	import type { Snippet } from 'svelte';
	import { _, locale } from 'svelte-i18n';
	import Logo from '$lib/components/layout/Logo.svelte';
	import { theme } from '$lib/theme/theme.svelte';

	let { children }: { children: Snippet } = $props();

	function toggleLanguage(): void {
		locale.set($locale?.startsWith('ar') ? 'en' : 'ar');
	}
</script>

<!-- Sign-in and first-login: the product mark, the language and theme switches, and one card in the middle. -->
<div class="flex min-h-dvh flex-col bg-background">
	<header class="flex items-center justify-between gap-3 px-4 py-4 md:px-8">
		<div class="flex items-center gap-3">
			<Logo class="size-8 shrink-0" />
			<div class="grid text-start leading-tight">
				<span class="text-lg font-bold tracking-tight">{$_('app.title')}</span>
				<span class="text-xs font-medium tracking-wider text-muted-foreground uppercase"
					>{$_('auth.brand')}</span
				>
			</div>
		</div>
		<div class="flex items-center gap-2">
			<button
				type="button"
				data-testid="theme-toggle"
				aria-label={theme.resolved === 'dark'
					? $_('shell.theme.toLight')
					: $_('shell.theme.toDark')}
				onclick={() => theme.toggle()}
				class="inline-flex h-10 items-center justify-center rounded-lg border bg-card px-3 text-muted-foreground transition-colors hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
			>
				{#if theme.resolved === 'dark'}
					<Sun class="size-4" aria-hidden="true" />
				{:else}
					<Moon class="size-4" aria-hidden="true" />
				{/if}
			</button>
			<button
				type="button"
				data-testid="language-toggle"
				aria-label={$_('app.language.label')}
				onclick={toggleLanguage}
				class="inline-flex h-10 items-center gap-2 rounded-lg border bg-card px-3 text-sm text-muted-foreground transition-colors hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
			>
				<Languages class="size-4" aria-hidden="true" />
				<span lang={$locale?.startsWith('ar') ? 'en' : 'ar'}>{$_('app.language.toggle')}</span>
			</button>
		</div>
	</header>
	<main id="main" class="flex flex-1 items-start justify-center px-4 pt-6 pb-12 md:pt-16">
		{@render children()}
	</main>
</div>
