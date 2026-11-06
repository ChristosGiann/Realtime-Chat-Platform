import { expect, test } from '@playwright/test'
import type { Page } from '@playwright/test'
const owner = '11111111-1111-1111-1111-111111111111'
const bob = '22222222-2222-2222-2222-222222222222'
const carol = '33333333-3333-3333-3333-333333333333'
const group = '44444444-4444-4444-4444-444444444444'
const profile = (userId: string, displayName: string) => ({ userId, displayName, bio: null, avatarUrl: null })
const member = (userId: string, displayName: string, role = 'Member') => ({ userId, role, joinedAt: '2026-01-01T00:00:00Z', profile: profile(userId, displayName) })
const summary = { id: group, type: 'Group', name: 'Η ομάδα μας', createdAt: '2026-01-01T00:00:00Z', myRole: 'Owner', memberCount: 2 }
async function session(page: Page) {
  await page.route('**/api/conversations/*/messages?*', route => route.fulfill({ json: { items: [], nextCursor: null } }))
  await page.route('**/api/conversations/*/read-state', route => route.fulfill({ json: { lastReadMessageId: null, lastReadAt: null, unreadCount: 0 } }))
  await page.route('**/api/auth/me', route => route.fulfill({ json: { id: owner, userName: 'alice', email: 'alice@example.test', displayName: 'Alice' } }))
  await page.route('**/api/auth/csrf', route => route.fulfill({ json: { requestToken: 'conversation-token' } }))
}
async function list(page: Page, items: unknown[] = []) {
  await page.route('**/api/conversations?*', route => route.fulfill({ json: { items, nextOffset: null } }))
}
async function search(page: Page, items = [profile(bob, 'Bob')]) {
  await page.route('**/api/users?*', route => route.fulfill({ json: { items, nextCursor: null } }))
}
async function find(page: Page, name: string) {
  await page.getByLabel('Αναζήτηση ατόμων', { exact: true }).fill(name)
  await page.getByRole('button', { name: 'Αναζήτηση ατόμων', exact: true }).click()
  await page.getByRole('button', { name: `Επιλογή ${name}`, exact: true }).click()
}

test('empty list, loading, error and retry are visible', async ({ page }) => {
  await session(page)
  let release!: () => void
  const gate = new Promise<void>(resolve => { release = resolve })
  let fail = true
  await page.route('**/api/conversations?*', async route => {
    if (fail) { await gate; await route.fulfill({ status: 500, json: {} }) }
    else await route.fulfill({ json: { items: [], nextOffset: null } })
  })
  await page.goto('/#/conversations')
  await expect(page.getByText('Φόρτωση συνομιλιών…')).toBeVisible()
  release()
  await expect(page.getByRole('alert')).toBeVisible()
  fail = false
  await page.getByRole('button', { name: 'Ανανέωση λίστας' }).click()
  await expect(page.getByText('Δεν έχεις ακόμη συνομιλίες.')).toBeVisible()
})

test('direct creation sends selected target with CSRF and opens safe details', async ({ page }) => {
  await session(page); await list(page); await search(page)
  await page.route('**/api/conversations/direct', route => {
    expect(route.request().postDataJSON()).toEqual({ targetUserId: bob })
    expect(route.request().headers()['x-csrf-token']).toBe('conversation-token')
    return route.fulfill({ json: { id: group } })
  })
  await page.route(`**/api/conversations/${group}?*`, route => route.fulfill({ json: { ...summary, type: 'Direct', name: null, myRole: 'Member', members: [member(bob, '<script>Bob</script>')], nextOffset: null } }))
  await page.goto('/#/conversations/new'); await find(page, 'Bob')
  await expect(page.getByRole('heading', { name: 'Direct συνομιλία' })).toBeVisible()
  await expect(page.getByRole('link', { name: '<script>Bob</script>', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Αποχώρηση από ομάδα' })).toHaveCount(0)
  await expect(page.getByRole('heading', { name: 'Προσθήκη μέλους' })).toHaveCount(0)
})

test('group validates name and sends distinct selected members', async ({ page }) => {
  await session(page); await list(page); await search(page)
  await page.route('**/api/conversations/group', route => {
    expect(route.request().postDataJSON()).toEqual({ name: 'Η ομάδα μας', memberIds: [bob] })
    return route.fulfill({ status: 201, json: { id: group } })
  })
  await page.route(`**/api/conversations/${group}?*`, route => route.fulfill({ json: { ...summary, members: [member(owner, 'Alice', 'Owner'), member(bob, 'Bob')], nextOffset: null } }))
  await page.goto('/#/conversations/new')
  await page.getByRole('button', { name: 'Ομάδα', exact: true }).click()
  await find(page, 'Bob')
  await expect(page.getByRole('button', { name: 'Επιλογή Bob', exact: true })).toHaveCount(0)
  await page.getByLabel('Όνομα ομάδας').fill('   ')
  await page.getByRole('button', { name: 'Δημιουργία ομάδας' }).click()
  await expect(page.getByLabel('Όνομα ομάδας')).toHaveAttribute('aria-invalid', 'true')
  await page.getByLabel('Όνομα ομάδας').fill('Η ομάδα μας')
  await page.getByRole('button', { name: 'Δημιουργία ομάδας' }).click()
  await expect(page.getByRole('heading', { name: 'Η ομάδα μας' })).toBeVisible()
})

test('owner can add and remove members while owner actions remain protected', async ({ page }) => {
  await session(page); await list(page, [summary]); await search(page, [profile(carol, 'Carol')])
  let members = [member(owner, 'Alice', 'Owner'), member(bob, 'Bob')]
  await page.route(`**/api/conversations/${group}?*`, route => route.fulfill({ json: { ...summary, memberCount: members.length, members, nextOffset: null } }))
  await page.route(`**/api/conversations/${group}/members/*`, route => {
    expect(route.request().headers()['x-csrf-token']).toBe('conversation-token')
    if (route.request().method() === 'PUT') members.push(member(carol, 'Carol'))
    else members = members.filter(x => x.userId !== bob)
    return route.fulfill({ status: 204 })
  })
  await page.goto(`/#/conversations/${group}`)
  await expect(page.getByRole('button', { name: 'Αφαίρεση Alice', exact: true })).toHaveCount(0)
  await find(page, 'Carol')
  await expect(page.getByRole('link', { name: 'Carol', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Αφαίρεση Bob', exact: true }).click()
  await expect(page.getByRole('link', { name: 'Bob', exact: true })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Αποχώρηση από ομάδα' })).toHaveCount(0)
})

test('member can leave and list is refreshed', async ({ page }) => {
  await session(page); await list(page, [summary])
  await page.route(`**/api/conversations/${group}?*`, route => route.fulfill({ json: { ...summary, myRole: 'Member', members: [member(owner, 'Alice')], nextOffset: null } }))
  await page.route(`**/api/conversations/${group}/membership`, async route => { await list(page); await route.fulfill({ status: 204 }) })
  await page.goto(`/#/conversations/${group}`)
  await expect(page.getByRole('heading', { name: 'Προσθήκη μέλους' })).toHaveCount(0)
  await page.getByRole('button', { name: 'Αποχώρηση από ομάδα' }).click()
  await expect(page.getByText('Δεν έχεις ακόμη συνομιλίες.')).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Επίλεξε συνομιλία' })).toBeVisible()
})

test('list and member pages append, fit mobile and missing profile uses ID', async ({ page }, testInfo) => {
  await session(page)
  await page.route('**/api/conversations?*', route => route.fulfill({ json: new URL(route.request().url()).searchParams.get('offset') === '0'
    ? { items: [summary], nextOffset: 20 } : { items: [{ ...summary, id: bob, name: 'Δεύτερη' }], nextOffset: null } }))
  await page.route(`**/api/conversations/${group}?*`, route => route.fulfill({ json: { ...summary,
    members: new URL(route.request().url()).searchParams.get('offset') === '0' ? [member(owner, 'Alice', 'Owner')] : [{ ...member(bob, 'Bob'), profile: null }],
    nextOffset: new URL(route.request().url()).searchParams.get('offset') === '0' ? 20 : null } }))
  await page.goto(`/#/conversations/${group}`)
  await page.getByRole('button', { name: 'Περισσότερες συνομιλίες' }).click()
  await expect(page.getByRole('link', { name: 'Δεύτερη', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Περισσότερα μέλη' }).click()
  await expect(page.getByRole('link', { name: bob, exact: true })).toBeVisible()
  await page.setViewportSize({ width: 390, height: 844 })
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
  await page.screenshot({ path: testInfo.outputPath('conversations-mobile.png'), fullPage: true })
})

test('missing access and expired session remove protected actions', async ({ page }) => {
  await session(page); await list(page)
  await page.route(`**/api/conversations/${group}?*`, route => route.fulfill({ status: 404, json: {} }))
  await page.goto(`/#/conversations/${group}`)
  await expect(page.getByRole('alert')).toContainText('δεν είναι διαθέσιμος')
  await expect(page.getByRole('heading', { name: 'Μέλη', exact: true })).toHaveCount(0)
  await page.route('**/api/conversations?*', route => route.fulfill({ status: 401, json: {} }))
  await page.getByRole('button', { name: 'Ανανέωση λίστας' }).click()
  await expect(page.getByRole('heading', { name: 'Σύνδεση', exact: true })).toBeVisible()
})

test('anonymous conversation route requires login', async ({ page }) => {
  await page.route('**/api/auth/me', route => route.fulfill({ status: 401, json: {} }))
  await page.goto('/#/conversations')
  await expect(page.getByRole('heading', { name: 'Σύνδεση', exact: true })).toBeVisible()
})

for (const status of [403, 409]) {
  test(`mutation ${status} shows conversation error without claiming success`, async ({ page }) => {
    await session(page); await list(page, [summary])
    await page.route(`**/api/conversations/${group}?*`, route => route.fulfill({ json: { ...summary, members: [member(owner, 'Alice', 'Owner'), member(bob, 'Bob')], nextOffset: null } }))
    await page.route(`**/api/conversations/${group}/members/${bob}`, route => route.fulfill({ status, json: {} }))
    await page.goto(`/#/conversations/${group}`)
    await page.getByRole('button', { name: 'Αφαίρεση Bob', exact: true }).click()
    await expect(page.getByRole('alert')).toContainText(status === 403 ? 'Δεν έχεις δικαίωμα' : 'δεν επιτρέπεται')
    if (status === 403) await expect(page.getByRole('button', { name: 'Αφαίρεση Bob', exact: true })).toHaveCount(0)
    else await expect(page.getByRole('link', { name: 'Bob', exact: true })).toBeVisible()
  })
}
