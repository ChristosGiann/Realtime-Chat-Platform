import { expect, test } from '@playwright/test'

test('Apothiki branding and responsive palette remain accessible', async ({ page }) => {
  await page.route('**/api/auth/me', route => route.fulfill({ status: 401, json: {} }))
  await page.goto('/#/login')
  await expect(page).toHaveTitle('Apothiki')
  await expect(page.getByRole('link', { name: 'Apothiki' })).toBeVisible()
  await expect(page.locator('.auth-panel .eyebrow')).toHaveText('Apothiki')
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark')
  await page.setViewportSize({ width: 390, height: 844 })
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('light')
  await page.getByRole('link', { name: 'Apothiki' }).focus()
  expect(await page.getByRole('link', { name: 'Apothiki' }).evaluate(element => getComputedStyle(element).outlineStyle)).toBe('solid')
})
