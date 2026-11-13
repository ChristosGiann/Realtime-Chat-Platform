import type { Page } from '@playwright/test'

export async function receiptMocks(page: Page, owner: string) {
  await page.route('**/api/conversations/*/receipts?*', route => route.fulfill({ json: {
    conversationId: new URL(route.request().url()).pathname.split('/')[3], conversationType: 'Group',
    rosterVersion: 'roster', myMembershipId: 'membership-owner', nextOffset: null,
    items: [{ userId: owner, membershipId: 'membership-owner', delivered: null, read: null },
      { userId: 'bob', membershipId: 'membership-bob', delivered: null, read: null }],
  } }))
  await page.route('**/api/conversations/*/delivery-state', route => route.fulfill({ json: {} }))
}
