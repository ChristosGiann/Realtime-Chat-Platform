import { expect, test } from '@playwright/test'
import type { Page } from '@playwright/test'
import { receiptMocks } from './receipt-mocks'

const owner = '11111111-1111-1111-1111-111111111111'
const other = '22222222-2222-2222-2222-222222222222'
const group = '44444444-4444-4444-4444-444444444444'
const name = 'Σχεδιασμός ' + 'πολύμεγάλοόνομα'.repeat(5)
const summary = { id: group, type: 'Group', name, createdAt: '2026-01-01T00:00:00Z', myRole: 'Owner', memberCount: 2 }
const messages = Array.from({ length: 18 }, (_, index) => ({
  id: `55555555-5555-5555-5555-${String(index).padStart(12, '0')}`, clientMessageId: `66666666-6666-6666-6666-${String(index).padStart(12, '0')}`,
  conversationId: group, senderId: index % 2 ? owner : other, body: 'Μεγάλο μήνυμα ' + 'χωρίςκενά'.repeat(30),
  createdAt: `2026-01-01T00:${String(index).padStart(2, '0')}:00Z`,
  sender: { userId: index % 2 ? owner : other, displayName: index % 2 ? 'Alice' : 'Bob ' + 'μεγάλοόνομα'.repeat(4), avatarUrl: null },
}))

async function setup(page: Page) {
  await receiptMocks(page, owner)
  await page.route('**/api/auth/me', route => route.fulfill({ json: { id: owner, userName: 'alice', email: 'alice@example.test', displayName: 'Alice' } }))
  await page.route('**/api/conversations?*', route => route.fulfill({ json: { items: [summary], nextOffset: null } }))
  await page.route(`**/api/conversations/${group}?*`, route => route.fulfill({ json: { ...summary, members: [
    { userId: owner, role: 'Owner', profile: { userId: owner, displayName: 'Alice' } },
    { userId: other, role: 'Member', profile: null },
  ], nextOffset: null } }))
  await page.route(`**/api/conversations/${group}/messages?*`, route => route.fulfill({ json: { items: [...messages].reverse(), nextCursor: null } }))
  await page.route(`**/api/conversations/${group}/read-state`, route => route.fulfill({ json: { lastReadMessageId: null, lastReadAt: null, unreadCount: 2 } }))
  await page.route('**/api/users?*', route => route.fulfill({ json: { items: [], nextCursor: null } }))
}

async function noOverflow(page: Page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
}

test('mobile list → chat → details is one screen at a time, including deep refresh and back', async ({ page }, testInfo) => {
  await setup(page)
  for (const [width, height] of [[320, 568], [360, 800], [390, 844], [430, 932]]) {
    await page.setViewportSize({ width, height })
    await page.goto('/#/conversations')
    await expect(page.getByRole('complementary', { name: 'Λίστα συνομιλιών' })).toBeVisible()
    await expect(page.getByRole('region', { name: 'Ενεργή συνομιλία' })).toBeHidden()
    await noOverflow(page)
    const row = page.getByRole('link', { name, exact: true })
    expect((await row.boundingBox())!.height).toBeGreaterThanOrEqual(44)
    if (width === 390) await page.screenshot({ path: testInfo.outputPath('mobile-list.png') })
    await row.click()
    await expect(page.getByRole('complementary', { name: 'Λίστα συνομιλιών' })).toBeHidden()
    await expect(page.getByRole('region', { name: 'Ενεργή συνομιλία' })).toBeVisible()
    await expect(page.getByRole('link', { name: 'Πίσω στις συνομιλίες' })).toBeVisible()
    await expect(page.getByRole('textbox', { name: 'Νέο μήνυμα' })).toBeVisible()
    await expect(page.getByText('Μη αναγνωσμένα: 2')).toBeVisible()
    await expect(page.getByRole('list', { name: 'Ιστορικό μηνυμάτων' }).getByRole('listitem')).toHaveCount(18)
    await noOverflow(page)
    expect(await page.locator('.message-history').evaluate(element => element.scrollHeight > element.clientHeight)).toBe(true)
    if (width === 390) await page.screenshot({ path: testInfo.outputPath('mobile-chat.png') })
    await page.getByRole('button', { name: 'Στοιχεία συνομιλίας' }).click()
    await expect(page).toHaveURL(new RegExp(`#/conversations/${group}/details$`))
    await expect(page.getByRole('region', { name: 'Ενεργή συνομιλία' })).toBeHidden()
    await expect(page.getByRole('complementary', { name: 'Στοιχεία συνομιλίας' })).toBeVisible()
    await expect(page.getByRole('complementary', { name: 'Στοιχεία συνομιλίας' })).toBeFocused()
    await expect(page.getByRole('link', { name: other, exact: true })).toBeVisible()
    await expect(page.getByRole('button', { name: `Αφαίρεση ${other}` })).toBeVisible()
    if (width === 390) {
      await page.getByRole('button', { name: `Αφαίρεση ${other}` }).click()
      await expect(page.getByRole('button', { name: 'Επιβεβαίωση αφαίρεσης' })).toBeFocused()
      await page.getByRole('button', { name: 'Ακύρωση' }).click()
      await expect(page.getByRole('button', { name: `Αφαίρεση ${other}` })).toBeFocused()
    }
    await noOverflow(page)
    if (width === 390) await page.screenshot({ path: testInfo.outputPath('mobile-details.png') })
    await page.reload()
    await expect(page.getByRole('complementary', { name: 'Στοιχεία συνομιλίας' })).toBeVisible()
    await page.getByRole('link', { name: 'Πίσω στη συνομιλία' }).click()
    await expect(page.getByRole('region', { name: 'Ενεργή συνομιλία' })).toBeVisible()
    await expect(page.getByRole('complementary', { name: 'Στοιχεία συνομιλίας' })).toBeHidden()
    await expect(page.getByRole('button', { name: 'Στοιχεία συνομιλίας' })).toBeFocused()
    await page.getByRole('link', { name: 'Πίσω στις συνομιλίες' }).click()
    await expect(page.getByRole('complementary', { name: 'Λίστα συνομιλιών' })).toBeVisible()
  }
})

test('mobile creation and short viewport keep controls usable without horizontal overflow', async ({ page }) => {
  await setup(page)
  await page.setViewportSize({ width: 390, height: 480 })
  await page.goto('/#/conversations/new')
  await expect(page.getByRole('complementary', { name: 'Λίστα συνομιλιών' })).toBeHidden()
  await expect(page.getByRole('link', { name: 'Πίσω στις συνομιλίες' })).toBeVisible()
  await page.getByRole('button', { name: 'Ομάδα', exact: true }).click()
  await expect(page.getByLabel('Όνομα ομάδας')).toBeVisible()
  await noOverflow(page)
  await page.goto(`/#/conversations/${group}`)
  await expect(page.getByRole('textbox', { name: 'Νέο μήνυμα' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Αποστολή', exact: true })).toBeVisible()
  await noOverflow(page)
  expect(await page.evaluate(() => document.documentElement.scrollHeight <= innerHeight)).toBe(true)
  expect(await page.locator('.message-history').evaluate(element => element.clientHeight)).toBeGreaterThan(60)
})

test('resizing an open desktop details column to mobile starts on chat', async ({ page }) => {
  await setup(page)
  await page.setViewportSize({ width: 1280, height: 800 })
  await page.goto(`/#/conversations/${group}`)
  await expect(page.getByRole('complementary', { name: 'Στοιχεία συνομιλίας' })).toBeVisible()
  await page.setViewportSize({ width: 390, height: 844 })
  await expect(page.getByRole('complementary', { name: 'Στοιχεία συνομιλίας' })).toBeHidden()
  await expect(page.getByRole('button', { name: 'Στοιχεία συνομιλίας' })).toHaveAttribute('aria-expanded', 'false')
  await page.getByRole('button', { name: 'Στοιχεία συνομιλίας' }).click()
  await expect(page.getByRole('complementary', { name: 'Στοιχεία συνομιλίας' })).toBeVisible()
  await noOverflow(page)
})

test('keyboard opens mobile details, returns to chat and validates the composer', async ({ page }) => {
  await setup(page)
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto('/#/conversations')
  const row = page.getByRole('link', { name, exact: true })
  await row.focus()
  await page.keyboard.press('Enter')
  await expect(page.getByRole('link', { name: 'Πίσω στις συνομιλίες' })).toBeVisible()
  await page.getByRole('link', { name: 'Πίσω στις συνομιλίες' }).focus()
  await page.keyboard.press('Tab')
  await expect(page.getByRole('button', { name: 'Στοιχεία συνομιλίας' })).toBeFocused()
  await page.keyboard.press('Enter')
  await expect(page.getByRole('complementary', { name: 'Στοιχεία συνομιλίας' })).toBeFocused()
  await page.keyboard.press('Tab')
  await expect(page.getByRole('link', { name: 'Πίσω στη συνομιλία' })).toBeFocused()
  await page.keyboard.press('Enter')
  await expect(page.getByRole('button', { name: 'Στοιχεία συνομιλίας' })).toBeFocused()
  const composer = page.getByRole('textbox', { name: 'Νέο μήνυμα' })
  await composer.focus()
  await page.keyboard.type('   ')
  await page.keyboard.press('Tab')
  await expect(page.getByRole('button', { name: 'Αποστολή', exact: true })).toBeFocused()
  await page.keyboard.press('Enter')
  await expect(composer).toHaveAttribute('aria-invalid', 'true')
  await expect(page.getByRole('alert')).toContainText('Γράψε μήνυμα 1–4000')
  expect(await page.getByRole('button', { name: 'Αποστολή', exact: true }).evaluate(element => getComputedStyle(element).outlineStyle)).toBe('solid')
})

test('700/701 boundary keeps a single accessible details surface and clear focus', async ({ page }) => {
  await setup(page)
  await page.setViewportSize({ width: 700, height: 800 })
  await page.goto(`/#/conversations/${group}/details`)
  await expect(page.getByRole('complementary', { name: 'Στοιχεία συνομιλίας' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Πίσω στη συνομιλία' })).toBeVisible()
  await noOverflow(page)
  await page.setViewportSize({ width: 701, height: 800 })
  await expect(page.getByRole('link', { name: 'Πίσω στη συνομιλία' })).toBeHidden()
  await expect(page.getByRole('button', { name: 'Κλείσιμο στοιχείων' })).toBeVisible()
  await expect(page.locator('.conversation-workspace-header button')).toHaveAttribute('aria-expanded', 'true')
  await noOverflow(page)
  await page.getByRole('complementary', { name: 'Στοιχεία συνομιλίας' }).getByRole('button', { name: 'Κλείσιμο στοιχείων' }).click()
  await expect(page.getByRole('button', { name: 'Στοιχεία συνομιλίας' })).toBeFocused()
  await expect(page.getByRole('button', { name: 'Στοιχεία συνομιλίας' })).toHaveAttribute('aria-expanded', 'false')
})

test('reduced motion preference removes control transition', async ({ page }) => {
  await setup(page)
  await page.emulateMedia({ reducedMotion: 'reduce' })
  await page.goto('/#/conversations')
  const duration = await page.getByRole('button', { name: 'Ανανέωση λίστας' }).evaluate(element => getComputedStyle(element).transitionDuration)
  expect(Number.parseFloat(duration)).toBeLessThanOrEqual(0.001)
})

test('long mixed message and receipt text never create horizontal page overflow', async ({ page }) => {
  await setup(page)
  await page.route(`**/api/conversations/${group}/messages?*`, route => route.fulfill({ json: { items: [{ ...messages[1], body: 'Ελληνικά Latin <script>' + 'χωρίςκενά'.repeat(380) }], nextCursor: null } }))
  for (const width of [320, 390, 701, 1024, 1440]) {
    await page.setViewportSize({ width, height: 800 })
    await page.goto(`/#/conversations/${group}`)
    await expect(page.getByRole('list', { name: 'Ιστορικό μηνυμάτων' }).getByRole('listitem')).toHaveCount(1)
    await noOverflow(page)
    await expect(page.locator('.message-history script')).toHaveCount(0)
    await expect(page.getByLabel('Κατάσταση μηνύματος')).toContainText('Delivered 0/1')
  }
})

test('leaving mobile details or resizing with confirmation open clears it', async ({ page }) => {
  await setup(page)
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto(`/#/conversations/${group}/details`)
  await page.getByRole('button', { name: `Αφαίρεση ${other}` }).click()
  await expect(page.getByRole('group', { name: `Επιβεβαίωση αφαίρεσης ${other}` })).toBeVisible()
  await page.getByRole('link', { name: 'Πίσω στη συνομιλία' }).click()
  await page.getByRole('button', { name: 'Στοιχεία συνομιλίας' }).click()
  await expect(page.getByRole('group', { name: `Επιβεβαίωση αφαίρεσης ${other}` })).toHaveCount(0)
  await expect(page.getByRole('button', { name: `Αφαίρεση ${other}` })).toBeEnabled()
  await page.getByRole('button', { name: `Αφαίρεση ${other}` }).click()
  await page.setViewportSize({ width: 1024, height: 800 })
  await page.getByRole('complementary', { name: 'Στοιχεία συνομιλίας' }).getByRole('button', { name: 'Κλείσιμο στοιχείων' }).click()
  await expect(page.getByRole('group', { name: `Επιβεβαίωση αφαίρεσης ${other}` })).toHaveCount(0)
  await page.setViewportSize({ width: 390, height: 844 })
  await page.getByRole('button', { name: 'Στοιχεία συνομιλίας' }).click()
  await expect(page.getByRole('button', { name: `Αφαίρεση ${other}` })).toBeEnabled()
})
