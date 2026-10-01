// Applies the saved light or dark mode before the first paint, so the page never flashes the wrong theme.
// An external file because the content security policy allows no inline script except SvelteKit's own bootstrap.
// Keep the key and class names in step with src/lib/theme/theme.svelte.ts.
(function () {
	var root = document.documentElement;
	var mode = 'system';
	try {
		var saved = window.localStorage.getItem('ariva-theme-mode');
		if (saved === 'light' || saved === 'dark' || saved === 'system') mode = saved;
	} catch (e) {
		// Storage can be blocked; the system preference still applies.
	}
	var dark =
		mode === 'dark' ||
		(mode === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches);
	root.classList.add('ariva', dark ? 'dark' : 'light');
})();
