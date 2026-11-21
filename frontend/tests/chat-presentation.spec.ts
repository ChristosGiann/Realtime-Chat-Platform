import { expect, test } from '@playwright/test'
import { receiptMocks } from './receipt-mocks'

const owner = '11111111-1111-1111-1111-111111111111'
const other = '22222222-2222-2222-2222-222222222222'
const group = '44444444-4444-4444-4444-444444444444'
const summary = { id: group, type: 'Group', name: 'Design review', createdAt: '2026-01-01T00:00:00Z', myRole: 'Owner', memberCount: 2 }
const message = (id: string, senderId: string, body: string, createdAt: string) => ({ id, clientMessageId: id, conversationId: group,
  senderId, body, createdAt, sender: { userId: senderId, displayName: senderId === owner ? 'Alice' : 'Bob', avatarUrl: null } })

async function setup(page: import('@playwright/test').Page, items: ReturnType<typeof message>[]) {
  await receiptMocks(page, owner)
  await page.route('**/api/auth/me', route => route.fulfill({ json: { id: owner, userName: 'alice', email: 'alice@example.test', displayName: 'Alice' } }))
  await page.route('**/api/conversations?*', route => route.fulfill({ json: { items: [summary], nextOffset: null } }))
  await page.route(`**/api/conversations/${group}?*`, route => route.fulfill({ json: { ...summary, members: [], nextOffset: null } }))
  await page.route(`**/api/conversations/${group}/messages?*`, route => route.fulfill({ json: { items: [...items].reverse(), nextCursor: null } }))
  await page.route(`**/api/conversations/${group}/read-state`, route => route.fulfill({ json: { lastReadMessageId: null, lastReadAt: null, unreadCount: 2 } }))
}

test('outgoing and incoming bubbles retain plain text, metadata and independent history scroll', async ({ page }, testInfo) => {
  const longBody = `Πρώτη γραμμή\n${'unbroken'.repeat(60)}`
  const messages = [
    message('55555555-5555-5555-5555-555555555551', other, longBody, '2026-01-01T00:00:00Z'),
    message('55555555-5555-5555-5555-555555555552', other, 'Επόμενο', '2026-01-01T00:01:00Z'),
    message('55555555-5555-5555-5555-555555555553', owner, 'Απάντηση', '2026-01-01T00:02:00Z'),
  ]
  await setup(page, messages)
  await page.goto(`/#/conversations/${group}`)
  const history = page.getByRole('list', { name: 'Ιστορικό μηνυμάτων' })
  await expect(history.getByRole('listitem')).toHaveCount(3)
  const incoming = history.locator(`[data-message-id="${messages[0].id}"]`)
  const grouped = history.locator(`[data-message-id="${messages[1].id}"]`)
  const outgoing = history.locator(`[data-message-id="${messages[2].id}"]`)
  await expect(incoming).toHaveClass(/message-item--incoming/)
  await expect(grouped).toHaveClass(/message-item--grouped/)
  await expect(outgoing).toHaveClass(/message-item--outgoing/)
  await expect(incoming.getByText('Bob', { exact: true })).toBeVisible()
  await expect(grouped.getByText('Bob', { exact: true })).toHaveClass(/visually-hidden/)
  await expect(incoming.getByText(longBody, { exact: true })).toBeVisible()
  await expect(incoming.locator('time')).toHaveAttribute('dateTime', messages[0].createdAt)
  await expect(outgoing.getByLabel('Κατάσταση μηνύματος')).toContainText('Delivered 0/1 · Read 0/1')
  const left = await incoming.boundingBox(), right = await outgoing.boundingBox()
  expect(left && right && right.x > left.x).toBe(true)
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  expect(await incoming.locator('.message-body').evaluate(element => getComputedStyle(element).whiteSpace)).toBe('pre-wrap')
  await expect(page.getByText('Μη αναγνωσμένα: 2')).toBeVisible()
  await page.locator('.message-history').evaluate(element => { element.scrollTop = element.scrollHeight })
  await page.screenshot({ path: testInfo.outputPath('chat-presentation-desktop.png') })
})

test('empty chat, composer validation and retry guidance remain clear', async ({ page }) => {
  await setup(page, [])
  await page.goto(`/#/conversations/${group}`)
  await expect(page.getByRole('heading', { name: 'Εδώ ξεκινά η συζήτηση' })).toBeVisible()
  await expect(page.getByText('Δεν υπάρχουν ακόμη μηνύματα.', { exact: true })).toBeVisible()
  const composer = page.getByRole('textbox', { name: 'Νέο μήνυμα' })
  await composer.fill('   ')
  await page.getByRole('button', { name: 'Αποστολή', exact: true }).click()
  await expect(composer).toHaveAttribute('aria-invalid', 'true')
  await expect(page.getByRole('alert')).toContainText('Γράψε μήνυμα 1–4000')
  await composer.fill('Έτοιμο')
  await expect(composer).toHaveAttribute('aria-invalid', 'false')
})
