import { test } from '@playwright/test';

// ARV-128: a probe for tests/api/smoke-guard.spec.ts only. It uses Playwright's own test, past the smoke guard, so
// playwright.smoke.config.ts refuses to load (a smoke spec imports from tests/smoke/support only).

test('a test past the guard', async () => {});
