import { expect, test } from '@playwright/test'
import type { APIRequestContext, Page } from '@playwright/test'
import { createServer } from 'node:http'
import { randomUUID } from 'node:crypto'

const api = 'http://localhost:5080'
const password = 'Test-password123!'
const summary = (id: string) => `${api}/api/conversations/${id}/unread-summary`

async function register(request: APIRequestContext) {
  const userName = 'ai' + randomUUID().replaceAll('-', '')
  const email = `${userName}@example.test`
  const response = await request.post(`${api}/api/auth/register`, { data: { userName, email, password, displayName: userName } })
  expect(response.status()).toBe(201)
  return { id: (await response.json()).id as string, email }
}
async function login(page: Page, email: string) {
  await page.goto('/#/login')
  await page.getByLabel('Email', { exact: true }).fill(email)
  await page.getByLabel('Κωδικός').fill(password)
  await page.getByRole('button', { name: 'Σύνδεση', exact: true }).click()
  await expect(page.getByRole('heading', { name: /Καλώς ήρθες/ })).toBeVisible()
}
async function csrf(request: APIRequestContext) {
  const response = await request.get(`${api}/api/auth/csrf`)
  return { 'X-CSRF-TOKEN': (await response.json()).requestToken as string }
}

test('real group summary uses fixed unread snapshot, leaves read state untouched and fails locally without breaking chat', async ({ page, browser, request }) => {
  let release: (() => void) | undefined
  let entered: (() => void) | undefined
  let fail = false
  const stub = createServer(async (req, res) => {
    const chunks: Buffer[] = []
    for await (const chunk of req) chunks.push(chunk as Buffer)
    const input = JSON.parse(Buffer.concat(chunks).toString()) as { prompt: string }
    expect(JSON.parse(input.prompt).messages).toBeDefined()
    entered?.()
    if (release) await new Promise<void>(resolve => { const unblock = release; release = () => { unblock?.(); resolve() } })
    if (fail) { res.writeHead(503); res.end('model unavailable'); return }
    res.writeHead(200, { 'Content-Type': 'application/json' })
    res.end(JSON.stringify({ response: JSON.stringify({ overview: 'Η ομάδα συζήτησε το πλάνο.', keyPoints: ['Ορίστηκε επόμενο βήμα.'], actionItems: [], unresolvedQuestions: [] }) }))
  })
  await new Promise<void>(resolve => stub.listen(11435, '127.0.0.1', resolve))
  const memberContext = await browser.newContext({ baseURL: 'http://localhost:5173' })
  try {
    const memberPage = await memberContext.newPage()
    const owner = await register(request), member = await register(request)
    await login(page, owner.email); await login(memberPage, member.email)
    const ownerHeaders = await csrf(page.request), memberHeaders = await csrf(memberPage.request)
    const created = await page.request.post(`${api}/api/conversations/group`, { headers: ownerHeaders, data: { name: 'AI E2E group', memberIds: [member.id] } })
    expect(created.status()).toBe(201)
    const group = (await created.json()).id as string
    const send = async (body: string) => {
      const response = await page.request.post(`${api}/api/conversations/${group}/messages`, { headers: ownerHeaders, data: { body, clientMessageId: randomUUID() } })
      expect(response.status()).toBe(201)
      return (await response.json()).id as string
    }
    const first = await send('Πρώτο θέμα')
    await memberPage.goto(`/#/conversations/${group}`)
    const button = memberPage.getByRole('button', { name: '✨ Περίληψη όσων έχασες' })
    await expect(button).toBeVisible()
    const before = await (await memberPage.request.get(`${api}/api/conversations/${group}/read-state`)).json()
    let started!: () => void
    const startedPromise = new Promise<void>(resolve => { started = resolve })
    entered = started
    release = () => {}
    await button.click()
    await expect(button).toBeDisabled()
    await startedPromise
    await send('Νεότερο θέμα μετά το snapshot')
    release()
    release = undefined; entered = undefined
    const card = memberPage.getByRole('region', { name: 'Περίληψη μη αναγνωσμένων' })
    await expect(card).toContainText('Η ομάδα συζήτησε το πλάνο.')
    await expect(card).toContainText('Περίληψη 1 μη αναγνωσμένων μηνυμάτων')
    await expect(card).toContainText(first)
    await memberPage.getByRole('button', { name: 'Ανανέωση μηνυμάτων' }).click()
    await expect(card).toContainText('+1 νεότερα μηνύματα')
    const after = await (await memberPage.request.get(`${api}/api/conversations/${group}/read-state`)).json()
    expect(after.lastReadMessageId).toBe(before.lastReadMessageId)
    expect(after.unreadCount).toBe(2)
    fail = true
    await button.click()
    await expect(card.getByRole('alert')).toContainText('Η περίληψη δεν είναι διαθέσιμη')
    await expect(memberPage.getByRole('textbox', { name: 'Νέο μήνυμα' })).toBeEnabled()
    const direct = await page.request.post(`${api}/api/conversations/direct`, { headers: ownerHeaders, data: { targetUserId: member.id } })
    expect(direct.status()).toBe(201)
    const directId = (await direct.json()).id as string
    await memberPage.goto(`/#/conversations/${directId}`)
    await expect(button).toHaveCount(0)
    expect((await memberPage.request.post(summary(directId), { headers: memberHeaders })).status()).toBe(409)
  } finally {
    await memberContext.close()
    await new Promise<void>(resolve => stub.close(() => resolve()))
  }
})
