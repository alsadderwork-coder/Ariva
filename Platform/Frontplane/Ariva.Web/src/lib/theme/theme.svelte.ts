// Light, dark or system mode for the Ariva brand scheme (tokens in src/app.css, same as Aman.Web).
// static/theme-init.js applies the saved mode before the first paint; keep its key and classes in step.

export type ThemeMode = 'light' | 'dark' | 'system';

const STORAGE_KEY = 'ariva-theme-mode';
const SCHEME_CLASS = 'ariva';

function readSavedMode(): ThemeMode {
	try {
		const saved = localStorage.getItem(STORAGE_KEY);
		return saved === 'light' || saved === 'dark' || saved === 'system' ? saved : 'system';
	} catch {
		return 'system';
	}
}

function systemPrefersDark(): boolean {
	return typeof window !== 'undefined' && window.matchMedia('(prefers-color-scheme: dark)').matches;
}

class ThemeState {
	mode = $state<ThemeMode>('system');
	resolved = $derived<'light' | 'dark'>(
		this.mode === 'system' ? (systemPrefersDark() ? 'dark' : 'light') : this.mode
	);

	/** Reads the saved mode; call once from the root layout. */
	init(): void {
		this.mode = readSavedMode();
		this.apply();
	}

	set(mode: ThemeMode): void {
		this.mode = mode;
		try {
			localStorage.setItem(STORAGE_KEY, mode);
		} catch {
			// Storage can be blocked; the choice still applies for this page view.
		}
		this.apply();
	}

	/** Toggles between light and dark, leaving "system" once the user chooses. */
	toggle(): void {
		this.set(this.resolved === 'dark' ? 'light' : 'dark');
	}

	private apply(): void {
		const root = document.documentElement;
		root.classList.add(SCHEME_CLASS);
		root.classList.toggle('dark', this.resolved === 'dark');
		root.classList.toggle('light', this.resolved === 'light');
	}
}

export const theme = new ThemeState();
