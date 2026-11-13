import { expect, test } from '@playwright/test'

test('receipt pagination restarts when the cohort changes and never mixes membership incarnations', async ({ page }) => {
  await page.route('**/api/auth/me', route => route.fulfill({ status: 401, json: {} }))
  const offsets: number[] = []
  await page.route('**/api/conversations/test/receipts?*', route => {
    const offset = Number(new URL(route.request().url()).searchParams.get('offset'))
    offsets.push(offset)
    const roster = offsets.length === 1 ? 'old' : 'new'
    return route.fulfill({ json: { conversationId: 'test', conversationType: 'Group', rosterVersion: roster,
      myMembershipId: `me-${roster}`, nextOffset: offset === 0 ? 100 : null,
      items: Array.from({ length: offset === 0 ? 100 : 1 }, (_, index) => ({
        userId: `user-${offset + index}`, membershipId: `${roster}-${offset + index}`, delivered: null, read: null,
      })),
    } })
  })
  await page.goto('/')
  const state = await page.evaluate(async () => {
    const modulePath = '/src/api/receipts.ts'
    const { receiptsApi } = await import(modulePath)
    return receiptsApi.snapshot('test', new AbortController().signal)
  })
  expect(offsets).toEqual([0, 100, 0, 100])
  expect(state.rosterVersion).toBe('new'); expect(state.myMembershipId).toBe('me-new')
  expect(state.items).toHaveLength(101)
  expect(state.items.every((item: { membershipId: string }) => item.membershipId.startsWith('new-'))).toBe(true)
})
