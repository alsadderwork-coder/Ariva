import { clsx, type ClassValue } from 'clsx';
import { twMerge } from 'tailwind-merge';

/** Joins class names and resolves Tailwind conflicts (the shadcn-svelte helper Aman.Web uses). */
export function cn(...inputs: ClassValue[]): string {
	return twMerge(clsx(inputs));
}
