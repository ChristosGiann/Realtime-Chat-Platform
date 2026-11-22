import { expect, test } from '@playwright/test'
import type { Page } from '@playwright/test'
import { receiptMocks } from './receipt-mocks'

const owner = '11111111-1111-1111-1111-111111111111'
const member = '22222222-2222-2222-2222-222222222222'
const group = '44444444-4444-4444-4444-444444444444'
const message = '55555555-5555-5555-5555-555555555555'
const summary = { fromReadMessageId: null, throughMessageId: message, throughCreatedAt: '2026-11-20T10:00:00Z',
  unreadMessagesSummarized: 1, overview: 'Η ομάδα συζήτησε το πλάνο.', keyPoints: ['Απόφαση για Παρασκευή.'],
  actionItems: ['Επιβεβαίωση ώρας.'], unresolvedQuestions: ['Ποιος θα παρουσιάσει;'] }

async function setup(page: Page, type: 'Group' | 'Direct', unread: () => number) {
  await receiptMocks(page, owner)
  await page.route('**/api/auth/me', route => route.fulfill({ json: { id: owner, userName: 'alice', email: 'alice@example.test', displayName: 'Alice' } }))
  await page.route('**/api/auth/csrf', route => route.fulfill({ json: { requestToken: 'test-token' } }))
  const details = { id: group, type, name: 'Product design', createdAt: '2026-01-01T00:00:00Z', myRole: type === 'Group' ? 'Owner' : 'Member', memberCount: 2 }
  await page.route('**/api/conversations?*', route => route.fulfill({ json: { items: [details], nextOffset: null } }))
  await page.route(`**/api/conversations/${group}?*`, route => route.fulfill({ json: { ...details, members: [
    { userId: owner, role: details.myRole, profile: { userId: owner, displayName: 'Alice' } },
    { userId: member, role: 'Member', profile: { userId: member, displayName: 'Bob' } },
  ], nextOffset: null } }))
  await page.route(`**/api/conversations/${group}/messages?*`, route => route.fulfill({ json: { items: [{
    id: message, clientMessageId: message, conversationId: group, senderId: member, body: 'Θα τα πούμε Παρασκευή.',
    createdAt: '2026-11-20T10:00:00Z', sender: { userId: member, displayName: 'Bob', avatarUrl: null },
  }], nextCursor: null } }))
  await page.route(`**/api/conversations/${group}/read-state`, route => route.fulfill({ json: {
    lastReadMessageId: null, lastReadAt: null, unreadCount: unread(),
  } }))
  await page.route('**/api/users?*', route => route.fulfill({ json: { items: [], nextCursor: null } }))
}

test('group summary is optional, structured and never marks history read', async ({ page }) => {
  let unread = 1, markReadCalls = 0
  await setup(page, 'Group', () => unread)
  await page.route(`**/api/conversations/${group}/read-state`, route => {
    if (route.request().method() === 'PUT') markReadCalls++
    return route.fulfill({ json: { lastReadMessageId: null, lastReadAt: null, unreadCount: unread } })
  })
  let release!: () => void
  const gate = new Promise<void>(resolve => { release = resolve })
  await page.route(`**/api/conversations/${group}/unread-summary`, async route => { await gate; await route.fulfill({ json: summary }) })
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto(`/#/conversations/${group}`)
  const button = page.getByRole('button', { name: '✨ Περίληψη όσων έχασες' })
  await expect(button).toBeVisible()
  await button.focus(); await page.keyboard.press('Enter')
  await expect(button).toBeDisabled()
  await expect(page.getByText('Δημιουργία περίληψης…')).toBeVisible()
  await expect(page.getByRole('textbox', { name: 'Νέο μήνυμα' })).toBeEnabled()
  release()
  const card = page.getByRole('region', { name: 'Περίληψη μη αναγνωσμένων' })
  await expect(card).toContainText('Η ομάδα συζήτησε το πλάνο.')
  await expect(card).toContainText('Απόφαση για Παρασκευή.')
  await expect(card).toContainText('Επιβεβαίωση ώρας.')
  await expect(card).toContainText('Ποιος θα παρουσιάσει;')
  await expect(card).toContainText(message)
  expect(markReadCalls).toBe(0)
  unread = 5
  await page.getByRole('button', { name: 'Ανανέωση μηνυμάτων' }).click()
  await expect(card).toContainText('+4 νεότερα μηνύματα')
  expect(markReadCalls).toBe(0)
})

test('direct and zero-unread conversations hide the action', async ({ page }) => {
  let unread = 1
  await setup(page, 'Direct', () => unread)
  await page.goto(`/#/conversations/${group}`)
  await expect(page.getByText('Μη αναγνωσμένα: 1')).toBeVisible()
  await expect(page.getByRole('button', { name: '✨ Περίληψη όσων έχασες' })).toHaveCount(0)
  unread = 0
  await setup(page, 'Group', () => unread)
  await page.reload()
  await expect(page.getByText('Μη αναγνωσμένα: 0')).toBeVisible()
  await expect(page.getByRole('button', { name: '✨ Περίληψη όσων έχασες' })).toHaveCount(0)
})

test('failure and retry remain local to the summary on mobile and desktop', async ({ page }) => {
  await setup(page, 'Group', () => 1)
  let attempts = 0
  await page.route(`**/api/conversations/${group}/unread-summary`, route => {
    attempts++
    return route.fulfill(attempts === 1 ? { status: 503, json: { title: 'Model unavailable' } } : { json: summary })
  })
  for (const width of [320, 430, 1440]) {
    await page.setViewportSize({ width, height: 844 })
    await page.goto(`/#/conversations/${group}`)
    const button = page.getByRole('button', { name: '✨ Περίληψη όσων έχασες' })
    await expect(button).toBeVisible()
    await button.click()
    if (width === 320) {
      await expect.poll(() => attempts).toBe(1)
      await expect(page.getByRole('region', { name: 'Περίληψη μη αναγνωσμένων' }).getByRole('alert')).toContainText('δεν είναι διαθέσιμη')
      await expect(page.getByRole('textbox', { name: 'Νέο μήνυμα' })).toBeEnabled()
      await button.click()
    }
    await expect(page.getByRole('region', { name: 'Περίληψη μη αναγνωσμένων' })).toContainText('Η ομάδα συζήτησε')
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  }
})

test('long Greek and Latin summary text wraps at narrow widths and respects reduced motion', async ({ page }) => {
  await setup(page, 'Group', () => 1)
  await page.emulateMedia({ reducedMotion: 'reduce' })
  await page.route(`**/api/conversations/${group}/unread-summary`, route => route.fulfill({ json: {
    ...summary, overview: 'Ελληνικά Latin ' + 'unbroken'.repeat(100),
  } }))
  for (const width of [320, 390, 430, 700, 701, 1440]) {
    await page.setViewportSize({ width, height: 844 })
    await page.goto(`/#/conversations/${group}`)
    const button = page.getByRole('button', { name: '✨ Περίληψη όσων έχασες' })
    await expect(button).toBeVisible()
    await button.click()
    await expect(page.getByRole('region', { name: 'Περίληψη μη αναγνωσμένων' })).toContainText('Ελληνικά Latin')
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
    const duration = await button.evaluate(element => getComputedStyle(element).transitionDuration)
    expect(Number.parseFloat(duration) || 0).toBeLessThanOrEqual(0.001)
  }
})

test('stale unread and revoked membership never display an obsolete summary', async ({ page }) => {
  await setup(page, 'Group', () => 1)
  let status = 204
  await page.route(`**/api/conversations/${group}/unread-summary`, route => route.fulfill(status === 204
    ? { status: 204, body: '' } : { status: 404, json: { title: 'Conversation not found.' } }))
  await page.goto(`/#/conversations/${group}`)
  const button = page.getByRole('button', { name: '✨ Περίληψη όσων έχασες' })
  await button.click()
  await expect(page.getByRole('region', { name: 'Περίληψη μη αναγνωσμένων' })).toContainText('Δεν υπάρχουν πλέον')
  status = 404
  await button.click()
  await expect(page.getByRole('region', { name: 'Περίληψη μη αναγνωσμένων' })).toHaveCount(0)
  await expect(page.getByRole('textbox', { name: 'Νέο μήνυμα' })).toHaveCount(0)
})
