import { addMessages, getLocaleFromNavigator, init } from 'svelte-i18n';
import ar from './ar.json';
import en from './en.json';

export const defaultLocale = 'en';
export const supportedLocales: readonly string[] = ['en', 'ar'];

const rightToLeftLocales = new Set(['ar']);
let initialised = false;

/** Registers the bundled dictionaries and picks the browser locale when it is supported. */
export function setupI18n(): void {
	if (initialised) {
		return;
	}

	addMessages('en', en);
	addMessages('ar', ar);

	const preferred = getLocaleFromNavigator()?.split('-')[0];
	init({
		fallbackLocale: defaultLocale,
		initialLocale: preferred && supportedLocales.includes(preferred) ? preferred : defaultLocale
	});

	initialised = true;
}

/** Text direction for a locale code such as "ar" or "en-GB". */
export function directionFor(locale: string | null | undefined): 'rtl' | 'ltr' {
	return locale && rightToLeftLocales.has(locale.split('-')[0]) ? 'rtl' : 'ltr';
}
