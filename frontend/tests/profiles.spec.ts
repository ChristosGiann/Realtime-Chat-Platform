import { expect, test } from '@playwright/test'
import type { Page } from '@playwright/test'
const id = '11111111-1111-1111-1111-111111111111'
const profile = { userId: id, displayName: 'Alice', bio: 'Hello', avatarUrl: null }
async function session(page: Page) {
  await page.route('**/api/auth/me', route => route.fulfill({ json: { id, userName: 'alice', email: 'private@example.test', ...profile } }))
  await page.route('**/api/auth/csrf', route => route.fulfill({ json: { requestToken: 'profile-token' } }))
}
test('edit profile sends only editable fields with CSRF and updates current user', async ({ page }) => {
  await session(page)
  await page.route('**/api/profiles/me', route => {
    expect(route.request().headers()['x-csrf-token']).toBe('profile-token')
    expect(route.request().postDataJSON()).toEqual({ displayName: 'Updated', bio: null, avatarUrl: null })
    return route.fulfill({ json: { ...profile, displayName: 'Updated', bio: null } })
  })
  await page.goto('/#/profile')
  await page.getByLabel('Εμφανιζόμενο όνομα', { exact: true }).fill('Updated')
  await page.getByLabel('Βιογραφικό').fill('')
  await page.getByRole('button', { name: 'Αποθήκευση', exact: true }).click()
  await expect(page.getByRole('status')).toHaveText('Το προφίλ αποθηκεύτηκε.')
  await page.getByRole('link', { name: 'Realtime Chat', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Καλώς ήρθες, Updated' })).toBeVisible()
})
test('profile validation is attached to the relevant field', async ({ page }) => {
  await session(page)
  await page.route('**/api/profiles/me', route => route.fulfill({ status: 400, json: { errors: { AvatarUrl: ['HTTPS required.'] } } }))
  await page.goto('/#/profile')
  await page.getByLabel('URL εικόνας προφίλ (HTTPS)').fill('http://example.test/avatar')
  await page.getByRole('button', { name: 'Αποθήκευση', exact: true }).click()
  await expect(page.getByLabel('URL εικόνας προφίλ (HTTPS)')).toHaveAttribute('aria-invalid', 'true')
  await expect(page.getByText('HTTPS required.')).toBeVisible()
})
test('public profile remains public during session outage and renders text safely', async ({ page }) => {
  await page.route('**/api/auth/me', route => route.fulfill({ status: 500, json: {} }))
  await page.route(`**/api/profiles/${id}`, route => route.fulfill({ json: { ...profile, bio: '<script>alert(1)</script>', avatarUrl: 'javascript:alert(1)' } }))
  await page.goto(`/#/profiles/${id}`)
  await expect(page.getByRole('heading', { name: 'Alice' })).toBeVisible()
  await expect(page.getByText('<script>alert(1)</script>', { exact: true })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Άνοιγμα εικόνας προφίλ' })).toHaveCount(0)
  await expect(page.getByText('private@example.test')).toHaveCount(0)
})
test('search appends pages and resets cursor for a new query', async ({ page }) => {
  await session(page)
  await page.route('**/api/users?*', route => {
    const parameters = new URL(route.request().url()).searchParams
    if (parameters.get('query') === 'Nobody') {
      expect(parameters.has('cursor')).toBe(false)
      return route.fulfill({ json: { items: [], nextCursor: null } })
    }
    expect(parameters.get('query')).toBe('A%')
    if (parameters.has('cursor')) {
      expect(parameters.get('cursor')).toBe('opaque&token=')
      return route.fulfill({ json: { items: [{ ...profile, userId: '22222222-2222-2222-2222-222222222222', displayName: 'Second' }], nextCursor: null } })
    }
    return route.fulfill({ json: { items: [profile], nextCursor: 'opaque&token=' } })
  })
  await page.goto('/#/users')
  await page.getByLabel('Όνομα χρήστη ή εμφανιζόμενο όνομα').fill('A%')
  await page.getByRole('button', { name: 'Αναζήτηση', exact: true }).click()
  await expect(page.getByRole('link', { name: 'Alice', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Περισσότερα αποτελέσματα' }).click()
  await expect(page.getByRole('link', { name: 'Second', exact: true })).toBeVisible()
  await expect(page.locator('.results li')).toHaveCount(2)
  await expect(page.getByRole('button', { name: 'Περισσότερα αποτελέσματα' })).toHaveCount(0)
  await page.getByLabel('Όνομα χρήστη ή εμφανιζόμενο όνομα').fill('Nobody')
  await page.getByRole('button', { name: 'Αναζήτηση', exact: true }).click()
  await expect(page.getByRole('status')).toHaveText('Δεν βρέθηκαν χρήστες.')
  await expect(page.locator('.results li')).toHaveCount(0)
})
test('expired session on search removes protected UI', async ({ page }) => {
  await session(page)
  await page.route('**/api/users?*', route => route.fulfill({ status: 401, json: {} }))
  await page.goto('/#/users')
  await page.getByLabel('Όνομα χρήστη ή εμφανιζόμενο όνομα').fill('Alice')
  await page.getByRole('button', { name: 'Αναζήτηση', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Σύνδεση', exact: true })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Το προφίλ μου', exact: true })).toHaveCount(0)
})
test('missing profile and failed search have visible errors and retry', async ({ page }) => {
  await session(page)
  await page.route(`**/api/profiles/${id}`, route => route.fulfill({ status: 404, json: {} }))
  await page.goto(`/#/profiles/${id}`)
  await expect(page.getByRole('alert')).toHaveText('Το προφίλ δεν βρέθηκε.')
  await page.route('**/api/users?*', route => route.abort())
  await page.getByRole('link', { name: 'Αναζήτηση', exact: true }).click()
  await page.getByRole('button', { name: 'Αναζήτηση', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('Δεν ήταν δυνατή')
  await page.unroute('**/api/users?*')
  await page.route('**/api/users?*', route => route.fulfill({ json: { items: [], nextCursor: null } }))
  await page.getByRole('button', { name: 'Αναζήτηση', exact: true }).click()
  await expect(page.getByRole('status')).toHaveText('Πληκτρολόγησε ένα όνομα για αναζήτηση.')
})
