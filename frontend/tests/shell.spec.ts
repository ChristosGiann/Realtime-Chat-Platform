import { expect, test } from '@playwright/test'
import { receiptMocks } from './receipt-mocks'

const owner = '11111111-1111-1111-1111-111111111111'
const group = '44444444-4444-4444-4444-444444444444'
const conversation = (index: number) => ({ id: index === 0 ? group : `44444444-4444-4444-4444-${String(index).padStart(12, '0')}`,
  type: 'Group', name: index === 0 ? 'Design review' : `Συνομιλία ${index}`, createdAt: '2026-01-01T00:00:00Z', myRole: 'Owner', memberCount: 2 })

test('desktop shell keeps inbox and history independently scrollable at common widths', async ({ page }, testInfo) => {
  await receiptMocks(page, owner)
  await page.route('**/api/auth/me', route => route.fulfill({ json: { id: owner, userName: 'alice', email: 'alice@example.test', displayName: 'Alice' } }))
  await page.route('**/api/conversations?*', route => route.fulfill({ json: { items: Array.from({ length: 35 }, (_, index) => conversation(index)), nextOffset: null } }))
  await page.route(`**/api/conversations/${group}?*`, route => route.fulfill({ json: { ...conversation(0), members: [], nextOffset: null } }))
  await page.route(`**/api/conversations/${group}/messages?*`, route => route.fulfill({ json: { items: Array.from({ length: 35 }, (_, index) => ({
    id: `55555555-5555-5555-5555-${String(index).padStart(12, '0')}`, clientMessageId: `66666666-6666-6666-6666-${String(index).padStart(12, '0')}`,
    conversationId: group, senderId: owner, body: `Μήνυμα ${index}`, createdAt: `2026-01-01T00:${String(index).padStart(2, '0')}:00Z`,
    sender: { userId: owner, displayName: 'Alice', avatarUrl: null },
  })), nextCursor: null } }))
  await page.route(`**/api/conversations/${group}/read-state`, route => route.fulfill({ json: { lastReadMessageId: null, lastReadAt: null, unreadCount: 0 } }))

  for (const width of [1440, 1280, 1024]) {
    await page.setViewportSize({ width, height: 800 })
    await page.goto(`/#/conversations/${group}`)
    await page.reload()
    await expect(page.getByRole('heading', { name: 'Design review' })).toBeVisible()
    await expect(page.getByRole('link', { name: 'Συνομιλίες', exact: true })).toHaveAttribute('aria-current', 'page')
    await expect(page.getByRole('link', { name: 'Design review' })).toHaveAttribute('aria-current', 'page')
    await expect(page.getByRole('list', { name: 'Ιστορικό μηνυμάτων' }).getByRole('listitem')).toHaveCount(35)
    const geometry = await page.evaluate(() => {
      const inbox = document.querySelector('.conversation-list-scroll')!
      const history = document.querySelector('.message-history')!
      const composer = document.querySelector('.message-composer')!
      const header = document.querySelector('.conversation-workspace-header')!
      return { pageOverflow: document.documentElement.scrollWidth > innerWidth,
        inboxOverflow: inbox.scrollHeight > inbox.clientHeight, historyOverflow: history.scrollHeight > history.clientHeight,
        headerTop: header.getBoundingClientRect().top, composerTop: composer.getBoundingClientRect().top }
    })
    expect(geometry.pageOverflow).toBe(false)
    expect(geometry.inboxOverflow).toBe(true)
    expect(geometry.historyOverflow).toBe(true)
    await page.locator('.conversation-list-scroll').evaluate(element => { element.scrollTop = 400 })
    await page.locator('.message-history').evaluate(element => { element.scrollTop = 400 })
    expect(await page.evaluate(() => ({ headerTop: document.querySelector('.conversation-workspace-header')!.getBoundingClientRect().top,
      composerTop: document.querySelector('.message-composer')!.getBoundingClientRect().top }))).toEqual({ headerTop: geometry.headerTop, composerTop: geometry.composerTop })
    if (width === 1024) {
      await expect(page.getByRole('button', { name: 'Στοιχεία συνομιλίας' })).toHaveAttribute('aria-expanded', 'false')
      await page.getByRole('button', { name: 'Στοιχεία συνομιλίας' }).click()
      await expect(page.getByRole('complementary', { name: 'Στοιχεία συνομιλίας' })).toBeVisible()
      await page.getByRole('button', { name: 'Κλείσιμο στοιχείων' }).click()
      await expect(page.getByRole('button', { name: 'Στοιχεία συνομιλίας' })).toBeFocused()
    } else await expect(page.getByRole('complementary', { name: 'Στοιχεία συνομιλίας' })).toBeVisible()
    if (width === 1440) await page.screenshot({ path: testInfo.outputPath('apothiki-desktop-shell.png') })
  }
})
