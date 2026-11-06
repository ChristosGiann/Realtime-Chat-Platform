import { expect, test } from '@playwright/test'
import type { APIRequestContext, Page } from '@playwright/test'
import { randomUUID } from 'node:crypto'

const api = 'http://localhost:5080'
const password = 'Test-password123!'
async function register(request: APIRequestContext) {
  const userName = 's3' + randomUUID().replaceAll('-', '')
  const response = await request.post(`${api}/api/auth/register`, { data: { userName, email: `${userName}@example.test`, password, displayName: userName } })
  expect(response.status()).toBe(201)
  return { id: (await response.json()).id as string, email: `${userName}@example.test` }
}
async function login(page: Page, email: string) {
  await page.goto('/#/login'); await page.getByLabel('Email', { exact: true }).fill(email)
  await page.getByLabel('Κωδικός', { exact: true }).fill(password)
  await page.getByRole('button', { name: 'Σύνδεση', exact: true }).click()
  await expect(page.getByRole('heading', { name: /Καλώς ήρθες/ })).toBeVisible()
}
async function csrf(request: APIRequestContext) {
  const response = await request.get(`${api}/api/auth/csrf`); expect(response.ok()).toBe(true)
  return { 'X-CSRF-TOKEN': (await response.json()).requestToken as string }
}
const messages = (id: string) => `${api}/api/conversations/${id}/messages`
const readState = (id: string) => `${api}/api/conversations/${id}/read-state`
const panel = (page: Page) => page.getByRole('region', { name: 'Μηνύματα συνομιλίας' })
async function sendUi(page: Page, body: string) {
  await page.getByLabel('Νέο μήνυμα', { exact: true }).fill(body)
  await panel(page).getByRole('button', { name: 'Αποστολή', exact: true }).click()
  await expect(panel(page).getByText(body, { exact: true })).toBeVisible()
  await expect(page.getByLabel('Νέο μήνυμα', { exact: true })).toHaveValue('')
}

test('real A/B sends survive reload, retries stay unique, history and independent reads persist', async ({ page, browser, request }, testInfo) => {
  const alice = await register(request), bob = await register(request), outsider = await register(request)
  const bobContext = await browser.newContext({ baseURL: 'http://localhost:5173' })
  const outsiderContext = await browser.newContext({ baseURL: 'http://localhost:5173' })
  try {
    const bobPage = await bobContext.newPage(), outsiderPage = await outsiderContext.newPage()
    await login(page, alice.email); await login(bobPage, bob.email); await login(outsiderPage, outsider.email)
    const headers = await csrf(page.request), bobHeaders = await csrf(bobPage.request), outsiderHeaders = await csrf(outsiderPage.request)
    const created = await page.request.post(`${api}/api/conversations/direct`, { headers, data: { targetUserId: bob.id } })
    expect(created.status()).toBe(201); const id = (await created.json()).id as string
    await page.goto(`/#/conversations/${id}`); await bobPage.goto(`/#/conversations/${id}`)
    await expect(panel(bobPage).getByText('Δεν υπάρχουν ακόμη μηνύματα.')).toBeVisible()
    const body = 'Persisted Ελληνικά <script>plain text</script>'
    await sendUi(page, body)
    await page.reload(); await expect(panel(page).getByText(body, { exact: true })).toBeVisible()
    // No live delivery is promised in Stage 3.
    await expect(panel(bobPage).getByText('Δεν υπάρχουν ακόμη μηνύματα.')).toBeVisible()
    await panel(bobPage).getByRole('button', { name: 'Ανανέωση μηνυμάτων' }).click()
    await expect(panel(bobPage).getByText('Μη αναγνωσμένα: 1')).toBeVisible()
    await panel(bobPage).getByRole('button', { name: 'Σήμανση ιστορικού ως αναγνωσμένου' }).click()
    await expect(panel(bobPage).getByText('Μη αναγνωσμένα: 0')).toBeVisible()
    await sendUi(bobPage, 'Reply from Bob')
    await panel(page).getByRole('button', { name: 'Ανανέωση μηνυμάτων' }).click()
    await expect(panel(page).getByText('Μη αναγνωσμένα: 1')).toBeVisible()
    const key = randomUUID()
    const responses = await Promise.all(Array.from({ length: 8 }, () => page.request.post(messages(id), { headers, data: { clientMessageId: key, body: 'Concurrent retry' } })))
    expect(responses.filter(x => x.status() === 201)).toHaveLength(1)
    expect(responses.filter(x => x.status() === 200)).toHaveLength(7)
    const rows = await Promise.all(responses.map(x => x.json()))
    expect(new Set(rows.map(x => x.id)).size).toBe(1)
    expect((await page.request.post(messages(id), { headers, data: { clientMessageId: key, body: 'Changed body' } })).status()).toBe(409)
    for (let i = 0; i < 31; i++) {
      const response = await page.request.post(messages(id), { headers, data: { clientMessageId: randomUUID(), body: `History ${i}` } })
      expect(response.status()).toBe(201)
    }
    let cursor: string | null = null
    const ids: string[] = []
    do {
      const response = await bobPage.request.get(`${messages(id)}?limit=7${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`)
      expect(response.status()).toBe(200)
      const history = await response.json()
      ids.push(...history.items.map((x: { id: string }) => x.id)); cursor = history.nextCursor
    } while (cursor)
    expect(ids).toHaveLength(34); expect(new Set(ids).size).toBe(34)
    await page.reload(); await expect(panel(page).locator('[data-message-id]')).toHaveCount(30)
    await panel(page).getByRole('button', { name: 'Παλαιότερα μηνύματα' }).click()
    await expect(panel(page).locator('[data-message-id]')).toHaveCount(34)
    await expect(panel(page).getByText(body, { exact: true })).toBeVisible()
    await panel(page).getByRole('button', { name: 'Σήμανση ιστορικού ως αναγνωσμένου' }).click()
    await expect(panel(page).getByText('Μη αναγνωσμένα: 0')).toBeVisible()
    expect((await (await bobPage.request.get(readState(id))).json()).unreadCount).toBe(32)
    const newest = ids[0], oldest = ids.at(-1)!
    await Promise.all([newest, oldest, newest, oldest].map(messageId => bobPage.request.put(readState(id), { headers: bobHeaders, data: { messageId } })))
    const finalRead = await (await bobPage.request.get(readState(id))).json()
    expect(finalRead.lastReadMessageId).toBe(newest); expect(finalRead.unreadCount).toBe(0)
    await bobPage.reload(); await expect(panel(bobPage).getByText('Μη αναγνωσμένα: 0')).toBeVisible()
    for (const url of [messages(id), readState(id)]) expect((await outsiderPage.request.get(url)).status()).toBe(404)
    expect((await outsiderPage.request.post(messages(id), { headers: outsiderHeaders, data: { clientMessageId: randomUUID(), body: 'Forbidden' } })).status()).toBe(404)
    expect((await request.get(messages(id))).status()).toBe(401)
    await page.screenshot({ path: testInfo.outputPath('stage3-persisted-history.png'), fullPage: true })
  } finally { await bobContext.close(); await outsiderContext.close() }
})

test('real group removal revokes messaging and read state; rejoin starts fresh progress', async ({ page, browser, request }) => {
  const owner = await register(request), member = await register(request)
  const context = await browser.newContext({ baseURL: 'http://localhost:5173' })
  try {
    const memberPage = await context.newPage(); await login(page, owner.email); await login(memberPage, member.email)
    const headers = await csrf(page.request), memberHeaders = await csrf(memberPage.request)
    const created = await page.request.post(`${api}/api/conversations/group`, { headers, data: { name: 'Stage 3 group', memberIds: [member.id] } })
    expect(created.status()).toBe(201); const id = (await created.json()).id as string
    const sent = await page.request.post(messages(id), { headers, data: { clientMessageId: randomUUID(), body: 'Retained group history' } })
    expect(sent.status()).toBe(201); const saved = await sent.json()
    await memberPage.goto(`/#/conversations/${id}`)
    await expect(panel(memberPage).getByText('Μη αναγνωσμένα: 1')).toBeVisible()
    await panel(memberPage).getByRole('button', { name: 'Σήμανση ιστορικού ως αναγνωσμένου' }).click()
    await expect(panel(memberPage).getByText('Μη αναγνωσμένα: 0')).toBeVisible()
    expect((await page.request.delete(`${api}/api/conversations/${id}/members/${member.id}`, { headers })).status()).toBe(204)
    await panel(memberPage).getByRole('button', { name: 'Ανανέωση μηνυμάτων' }).click()
    await expect(memberPage.getByLabel('Νέο μήνυμα', { exact: true })).toHaveCount(0)
    await expect(panel(memberPage).locator('[data-message-id]')).toHaveCount(0)
    expect((await memberPage.request.put(readState(id), { headers: memberHeaders, data: { messageId: saved.id } })).status()).toBe(404)
    expect((await memberPage.request.post(messages(id), { headers: memberHeaders, data: { clientMessageId: randomUUID(), body: 'Removed' } })).status()).toBe(404)
    expect((await page.request.put(`${api}/api/conversations/${id}/members/${member.id}`, { headers })).status()).toBe(204)
    await memberPage.reload()
    await expect(panel(memberPage).getByText('Retained group history', { exact: true })).toBeVisible()
    await expect(panel(memberPage).getByText('Μη αναγνωσμένα: 1')).toBeVisible()
    expect((await (await memberPage.request.get(readState(id))).json()).lastReadMessageId).toBeNull()
  } finally { await context.close() }
})
