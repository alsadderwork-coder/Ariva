/** Touch-sized controls of the observer tablet (ARV-104c): at least 48px high, so a gloved or hurried tap lands. */

const focus = 'focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none';

export const primaryButton = `inline-flex min-h-12 items-center justify-center gap-2 rounded-lg bg-button-primary px-5 text-base font-medium text-primary-foreground hover:opacity-90 disabled:opacity-60 dark:text-white ${focus}`;

export const secondaryButton = `inline-flex min-h-12 items-center justify-center gap-2 rounded-lg border bg-card px-4 text-base font-medium text-foreground hover:bg-accent disabled:opacity-60 ${focus}`;

export const field = `h-12 min-w-0 rounded-lg border border-input bg-background px-3 text-base read-only:bg-muted ${focus}`;
