import { expect, test } from '@playwright/test'
import type { Page } from '@playwright/test'
const user = { id: '11111111-1111-1111-1111-111111111111', userName: 'alice', email: 'alice@example.test', displayName: 'Alice', bio: null, avatarUrl: null }
async function api(page: Page, loggedIn = false) {
  const state = { loggedIn }
  await page.route('**/api/auth/**', async route => {
    const path = new URL(route.request().url()).pathname
    if (path.endsWith('/me')) return route.fulfill({ status: state.loggedIn ? 200 : 401, json: state.loggedIn ? user : {} })
    if (path.endsWith('/csrf')) return route.fulfill({ json: { requestToken: state.loggedIn ? 'signed-in-token' : 'anonymous-token' } })
    if (path.endsWith('/login')) {
      expect(route.request().headers()['x-csrf-token']).toBe('anonymous-token')
      state.loggedIn = true
      return route.fulfill({ status: 204 })
    }
    if (path.endsWith('/logout')) {
      expect(route.request().headers()['x-csrf-token']).toBe('signed-in-token')
      state.loggedIn = false
      return route.fulfill({ status: 204 })
    }
    return route.fulfill({ status: 201, json: user })
  })
  return state
}
test('anonymous protected page, login, reload and logout', async ({ page }) => {
  await api(page)
  await page.goto('/#/app')
  await expect(page.getByRole('heading', { name: 'Σύνδεση', exact: true })).toBeVisible()
  await page.getByLabel('Email', { exact: true }).fill(user.email)
  await page.getByLabel('Κωδικός', { exact: true }).fill('Test-password123!')
  await page.getByRole('button', { name: 'Σύνδεση', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Καλώς ήρθες, Alice' })).toBeVisible()
  await page.reload()
  await expect(page.getByRole('heading', { name: 'Καλώς ήρθες, Alice' })).toBeVisible()
  await page.getByRole('button', { name: 'Αποσύνδεση', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Σύνδεση', exact: true })).toBeVisible()
  await page.goBack()
  await expect(page.getByText(user.email, { exact: true })).toHaveCount(0)
})
test('registration success does not imply login', async ({ page }) => {
  await api(page)
  await page.goto('/#/register')
  await page.getByLabel('Email', { exact: true }).fill(user.email)
  await page.getByLabel('Όνομα χρήστη', { exact: true }).fill(user.userName)
  await page.getByLabel('Κωδικός', { exact: true }).fill('Test-password123!')
  await page.getByRole('button', { name: 'Δημιουργία λογαριασμού', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Ο λογαριασμός δημιουργήθηκε' })).toBeVisible()
  await page.getByRole('link', { name: 'Μετάβαση στη σύνδεση' }).click()
  await expect(page.getByLabel('Κωδικός', { exact: true })).toHaveValue('')
})
test('invalid login stays generic', async ({ page }) => {
  await api(page)
  await page.route('**/api/auth/login', route => route.fulfill({ status: 401, json: { detail: 'private-secret', title: 'Unknown account' } }))
  await page.goto('/#/login')
  await page.getByLabel('Email', { exact: true }).fill(user.email)
  await page.getByLabel('Κωδικός', { exact: true }).fill('wrong')
  await page.getByRole('button', { name: 'Σύνδεση', exact: true }).click()
  await expect(page.getByRole('alert')).toHaveText('Λανθασμένο email ή κωδικός.')
  await expect(page.getByText('private-secret')).toHaveCount(0)
})
test('validation errors appear next to registration fields', async ({ page }) => {
  await api(page)
  await page.route('**/api/auth/register', route => route.fulfill({ status: 409, json: { errors: { Email: ['Email already in use.'] } } }))
  await page.goto('/#/register')
  await page.getByLabel('Email', { exact: true }).fill(user.email)
  await page.getByLabel('Όνομα χρήστη', { exact: true }).fill(user.userName)
  await page.getByLabel('Κωδικός', { exact: true }).fill('Test-password123!')
  await page.getByRole('button', { name: 'Δημιουργία λογαριασμού', exact: true }).click()
  await expect(page.getByLabel('Email', { exact: true })).toHaveAttribute('aria-invalid', 'true')
  await expect(page.getByText('Email already in use.')).toBeVisible()
})
test('session outage offers retry without showing protected content', async ({ page }) => {
  await api(page, true)
  await page.route('**/api/auth/me', route => route.fulfill({ status: 500, json: { detail: 'private-secret' } }))
  await page.goto('/#/app')
  await expect(page.getByRole('heading', { name: 'Αδυναμία ελέγχου συνεδρίας' })).toBeVisible()
  await expect(page.getByText(user.email, { exact: true })).toHaveCount(0)
  await page.unroute('**/api/auth/me')
  await page.getByRole('button', { name: 'Δοκιμή ξανά' }).click()
  await expect(page.getByRole('heading', { name: 'Καλώς ήρθες, Alice' })).toBeVisible()
})
test('failed logout preserves session and allows retry', async ({ page }) => {
  await api(page, true)
  await page.route('**/api/auth/logout', route => route.abort())
  await page.goto('/#/app')
  await page.getByRole('button', { name: 'Αποσύνδεση', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('Δεν ήταν δυνατή')
  await expect(page.getByRole('heading', { name: 'Καλώς ήρθες, Alice' })).toBeVisible()
})
