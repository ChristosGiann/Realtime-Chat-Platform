import { expect, test } from '@playwright/test'
import type { Page } from '@playwright/test'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

// Run explicitly with: npx playwright test --config playwright.portfolio.config.ts
// All people, conversations and messages below are fictional demo data.
const images = resolve(dirname(fileURLToPath(import.meta.url)), '../../docs/images')
const alice = '11111111-1111-1111-1111-111111111111'
const bob = '22222222-2222-2222-2222-222222222222'
const maria = '33333333-3333-3333-3333-333333333333'
const design = '44444444-4444-4444-4444-444444444444'
const planning = '44444444-4444-4444-4444-444444444445'
const direct = '44444444-4444-4444-4444-444444444446'
const summaries = [
  { id: design, type: 'Group', name: 'Product design', createdAt: '2026-11-01T09:00:00Z', myRole: 'Owner', memberCount: 3 },
  { id: planning, type: 'Group', name: 'Launch planning', createdAt: '2026-11-02T09:00:00Z', myRole: 'Member', memberCount: 4 },
  { id: direct, type: 'Direct', name: 'Bob', createdAt: '2026-11-03T09:00:00Z', myRole: 'Member', memberCount: 2 },
] as const
const message = (index: number, senderId: string, senderName: string, body: string, time: string) => ({
  id: `55555555-5555-5555-5555-${String(index).padStart(12, '0')}`,
  clientMessageId: `66666666-6666-6666-6666-${String(index).padStart(12, '0')}`,
  conversationId: design, senderId, body, createdAt: `2026-11-18T${time}:00Z`,
  sender: { userId: senderId, displayName: senderName, avatarUrl: null },
})
const messages = [
  message(1, bob, 'Bob', 'I added the updated conversation layout to the design review.', '09:14'),
  message(2, maria, 'Maria', 'The mobile flow looks much clearer now. Details have their own screen.', '09:17'),
  message(3, alice, 'Alice', 'Agreed. I will check the group controls and keyboard navigation next.', '09:19'),
  message(4, bob, 'Bob', 'Perfect. The receipt labels also read well at smaller widths.', '09:22'),
  message(5, alice, 'Alice', 'Great — let’s keep the release notes focused on what is verified.', '09:25'),
]

async function setup(page: Page) {
  await page.route('**/api/auth/me', route => route.fulfill({ json: {
    id: alice, userName: 'alice-demo', email: 'alice@example.test', displayName: 'Alice', bio: null, avatarUrl: null,
  } }))
  await page.route('**/api/conversations?*', route => route.fulfill({ json: { items: summaries, nextOffset: null } }))
  await page.route(`**/api/conversations/${design}?*`, route => route.fulfill({ json: {
    ...summaries[0], members: [
      { userId: alice, role: 'Owner', profile: { userId: alice, displayName: 'Alice' } },
      { userId: bob, role: 'Member', profile: { userId: bob, displayName: 'Bob' } },
      { userId: maria, role: 'Member', profile: { userId: maria, displayName: 'Maria' } },
    ], nextOffset: null,
  } }))
  await page.route(`**/api/conversations/${design}/messages?*`, route => route.fulfill({ json: { items: [...messages].reverse(), nextCursor: null } }))
  await page.route(`**/api/conversations/${design}/read-state`, route => route.fulfill({ json: {
    lastReadMessageId: messages[4].id, lastReadAt: '2026-11-18T09:26:00Z', unreadCount: 0,
  } }))
  await page.route('**/api/conversations/*/receipts?*', route => route.fulfill({ json: {
    conversationId: design, conversationType: 'Group', rosterVersion: 'demo-roster',
    myMembershipId: 'demo-membership-alice', nextOffset: null,
    items: [
      { userId: alice, membershipId: 'demo-membership-alice', delivered: null, read: null },
      { userId: bob, membershipId: 'demo-membership-bob', delivered: null, read: null },
      { userId: maria, membershipId: 'demo-membership-maria', delivered: null, read: null },
    ],
  } }))
  await page.route('**/api/conversations/*/delivery-state', route => route.fulfill({ json: {} }))
  await page.route('**/api/users?*', route => route.fulfill({ json: { items: [], nextCursor: null } }))
}

test('capture the final Stage 7 desktop and mobile UI with demo data', async ({ page }) => {
  await setup(page)
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto(`/#/conversations/${design}`)
  await expect(page.getByRole('list', { name: 'Ιστορικό μηνυμάτων' }).getByRole('listitem')).toHaveCount(5)
  await expect(page.getByRole('complementary', { name: 'Στοιχεία συνομιλίας' })).toBeVisible()
  await page.screenshot({ path: resolve(images, 'apothiki-desktop-chat.png'), animations: 'disabled' })

  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto('/#/conversations')
  await expect(page.getByRole('complementary', { name: 'Λίστα συνομιλιών' })).toBeVisible()
  await page.screenshot({ path: resolve(images, 'apothiki-mobile-conversations.png'), animations: 'disabled' })
  await page.getByRole('link', { name: 'Product design', exact: true }).click()
  await expect(page.getByRole('list', { name: 'Ιστορικό μηνυμάτων' }).getByRole('listitem')).toHaveCount(5)
  await page.screenshot({ path: resolve(images, 'apothiki-mobile-chat.png'), animations: 'disabled' })
  await page.getByRole('button', { name: 'Στοιχεία συνομιλίας' }).click()
  await expect(page.getByRole('complementary', { name: 'Στοιχεία συνομιλίας' })).toBeVisible()
  await page.screenshot({ path: resolve(images, 'apothiki-mobile-details.png'), animations: 'disabled' })
})
