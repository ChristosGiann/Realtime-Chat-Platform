import { expect, test } from '@playwright/test'
import type { APIRequestContext, Browser, Page } from '@playwright/test'
import { randomUUID } from 'node:crypto'
import { live, login, panel, password, sendUi } from '../e2e/realtime-helpers'

const apiA = 'http://localhost:5081'
const apiB = 'http://localhost:5082'
const frontendA = 'http://localhost:5174'
const frontendB = 'http://localhost:5175'

async function register(request: APIRequestContext, api: string) {
  const userName = 's6' + randomUUID().replaceAll('-', '')
  const email = `${userName}@example.test`
  const response = await request.post(`${api}/api/auth/register`, { data: { userName, email, password } })
  expect(response.status()).toBe(201)
  return { id: (await response.json()).id as string, email }
}
async function csrf(request: APIRequestContext, api: string) {
  const response = await request.get(`${api}/api/auth/csrf`)
  expect(response.ok()).toBe(true)
  return { 'X-CSRF-TOKEN': (await response.json()).requestToken as string }
}
async function browserPage(browser: Browser, origin: string) {
  const context = await browser.newContext({ baseURL: origin })
  return { context, page: await context.newPage() }
}
async function open(page: Page, conversation: string) {
  await page.goto(`/#/conversations/${conversation}`)
  await expect(live(page)).toHaveText('Ζωντανή σύνδεση', { timeout: 20000 })
}
function frames(page: Page) {
  const received: { target: string; data: Record<string, unknown> }[] = []
  const sockets: string[] = []
  page.on('websocket', socket => {
    if (!socket.url().includes('/hubs/chat')) return
    sockets.push(socket.url())
    socket.on('framereceived', frame => {
      for (const raw of frame.payload.toString().split('\u001e').filter(Boolean)) {
        const parsed = JSON.parse(raw) as { type?: number; target?: string; arguments?: Record<string, unknown>[] }
        if (parsed.type === 1 && parsed.target && parsed.arguments?.[0])
          received.push({ target: parsed.target, data: parsed.arguments[0] })
      }
    })
  })
  return { received, sockets }
}
function routeApi(page: Page, expected: string) {
  const urls: string[] = []
  page.on('request', request => {
    if (request.url().includes('/api/auth/me')) urls.push(request.url())
  })
  return async () => {
    await expect.poll(() => urls.length).toBeGreaterThan(0)
    expect(urls.every(url => url.startsWith(expected))).toBe(true)
  }
}

test('browsers on distinct APIs exchange live messages, receipts, presence and typing', async ({ browser, request }) => {
  const alice = await register(request, apiA), bob = await register(request, apiB)
  const a = await browserPage(browser, frontendA), b = await browserPage(browser, frontendB)
  const extra = await browserPage(browser, frontendA)
  try {
    const aRoute = routeApi(a.page, apiA), bRoute = routeApi(b.page, apiB)
    const aFrames = frames(a.page), bFrames = frames(b.page)
    await login(a.page, alice.email); await login(b.page, bob.email)
    const created = await a.page.request.post(`${apiA}/api/conversations/group`, {
      headers: await csrf(a.page.request, apiA), data: { name: 'Stage 6 cross instance', memberIds: [bob.id] },
    })
    expect(created.status()).toBe(201)
    const conversation = (await created.json()).id as string
    await open(a.page, conversation); await open(b.page, conversation)
    await aRoute(); await bRoute()
    expect(aFrames.sockets.length).toBeGreaterThan(0)
    expect(bFrames.sockets.length).toBeGreaterThan(0)
    expect(aFrames.sockets.every(url => url.startsWith('ws://localhost:5081/'))).toBe(true)
    expect(bFrames.sockets.every(url => url.startsWith('ws://localhost:5082/'))).toBe(true)
    await expect(a.page.getByLabel('Παρουσία συνομιλίας')).toHaveText('1 online · 0 offline (άλλα μέλη)')
    await a.page.getByLabel('Νέο μήνυμα', { exact: true }).fill('typing across instances')
    await expect(b.page.getByLabel('Πληκτρολόγηση', { exact: true })).toBeVisible()
    await expect(b.page.getByLabel('Πληκτρολόγηση', { exact: true })).toHaveCount(0, { timeout: 7000 })
    await a.page.getByLabel('Νέο μήνυμα', { exact: true }).clear()
    await sendUi(a.page, 'Stage 6 A to B live')
    await expect(panel(b.page).getByText('Stage 6 A to B live', { exact: true })).toBeVisible()
    await expect.poll(() => bFrames.received.filter(event => event.target === 'MessageCreated'
      && event.data.body === 'Stage 6 A to B live').length).toBe(1)
    const history = await (await b.page.request.get(`${apiB}/api/conversations/${conversation}/messages`)).json()
    expect(history.items.filter((message: { body: string }) => message.body === 'Stage 6 A to B live')).toHaveLength(1)
    await expect(panel(a.page).getByLabel('Κατάσταση μηνύματος')).toContainText('Delivered')
    const messageId = history.items.find((message: { body: string }) => message.body === 'Stage 6 A to B live').id
    expect((await b.page.request.put(`${apiB}/api/conversations/${conversation}/read-state`, {
      headers: await csrf(b.page.request, apiB), data: { messageId },
    })).status()).toBe(200)
    await expect(panel(a.page).getByLabel('Κατάσταση μηνύματος')).toContainText('Read')
    await expect.poll(() => aFrames.received.filter(event => event.target === 'ReceiptAdvanced').length).toBeGreaterThan(0)
    const receipts = await (await a.page.request.get(`${apiA}/api/conversations/${conversation}/receipts`)).json()
    expect(receipts.items.find((item: { userId: string }) => item.userId === bob.id).read).not.toBeNull()
    await login(extra.page, bob.email); await open(extra.page, conversation)
    await b.page.close()
    await expect(a.page.getByLabel('Παρουσία συνομιλίας')).toHaveText('1 online · 0 offline (άλλα μέλη)')
    await extra.page.close()
    await expect(a.page.getByLabel('Παρουσία συνομιλίας')).toHaveText('0 online · 1 offline (άλλα μέλη)', { timeout: 20000 })
  } finally { await a.context.close(); await b.context.close(); await extra.context.close() }
})

test('cross-instance revocation fences both tabs and rejoin has a fresh incarnation', async ({ browser, request }) => {
  const owner = await register(request, apiA), member = await register(request, apiB)
  const a = await browserPage(browser, frontendA), b = await browserPage(browser, frontendB)
  const second = await browserPage(browser, frontendA)
  try {
    const firstFrames = frames(b.page), secondFrames = frames(second.page)
    await login(a.page, owner.email); await login(b.page, member.email); await login(second.page, member.email)
    const created = await a.page.request.post(`${apiA}/api/conversations/group`, {
      headers: await csrf(a.page.request, apiA), data: { name: 'Stage 6 revocation', memberIds: [member.id] },
    })
    expect(created.status()).toBe(201)
    const conversation = (await created.json()).id as string
    for (const page of [a.page, b.page, second.page]) await open(page, conversation)
    await sendUi(a.page, 'Before revocation')
    for (const page of [b.page, second.page])
      await expect(panel(page).getByText('Before revocation', { exact: true })).toBeVisible()
    const old = await (await b.page.request.get(`${apiB}/api/conversations/${conversation}/delivery-state`)).json()
    const history = await (await b.page.request.get(`${apiB}/api/conversations/${conversation}/messages`)).json()
    const oldMessageId = history.items.find((message: { body: string }) => message.body === 'Before revocation').id
    await b.page.getByLabel('Νέο μήνυμα', { exact: true }).fill('stale typing')
    await expect(a.page.getByLabel('Πληκτρολόγηση', { exact: true })).toBeVisible()
    expect((await a.page.request.delete(`${apiA}/api/conversations/${conversation}/members/${member.id}`,
      { headers: await csrf(a.page.request, apiA) })).status()).toBe(204)
    for (const page of [b.page, second.page]) await expect(page.getByLabel('Νέο μήνυμα', { exact: true })).toHaveCount(0)
    await expect(a.page.getByLabel('Πληκτρολόγηση', { exact: true })).toHaveCount(0)
    await sendUi(a.page, 'Secret after revocation')
    for (const observed of [firstFrames, secondFrames])
      expect(observed.received.filter(event => event.target === 'MessageCreated'
        && event.data.body === 'Secret after revocation')).toHaveLength(0)
    expect((await a.page.request.put(`${apiA}/api/conversations/${conversation}/members/${member.id}`,
      { headers: await csrf(a.page.request, apiA) })).status()).toBe(204)
    const fresh = await (await b.page.request.get(`${apiB}/api/conversations/${conversation}/delivery-state`)).json()
    expect(fresh.membershipId).not.toBe(old.membershipId)
    expect(fresh.lastDeliveredMessageId).toBeNull()
    expect((await b.page.request.put(`${apiB}/api/conversations/${conversation}/delivery-state`, {
      headers: await csrf(b.page.request, apiB),
      data: { membershipId: old.membershipId, messageId: oldMessageId },
    })).status()).toBe(409)
    for (const page of [b.page, second.page]) {
      await page.reload()
      await expect(live(page)).toHaveText('Ζωντανή σύνδεση', { timeout: 20000 })
    }
    await expect(a.page.getByLabel('Πληκτρολόγηση', { exact: true })).toHaveCount(0)
    await sendUi(a.page, 'After fresh subscription')
    for (const page of [b.page, second.page])
      await expect(panel(page).getByText('After fresh subscription', { exact: true })).toBeVisible()
  } finally { await a.context.close(); await b.context.close(); await second.context.close() }
})

test('real API process kill preserves B and browser A recovers missed message and receipt from REST', async ({ browser, request }) => {
  const alice = await register(request, apiA), bob = await register(request, apiB)
  const a = await browserPage(browser, frontendA), b = await browserPage(browser, frontendB)
  let stopped = false
  try {
    await login(a.page, alice.email); await login(b.page, bob.email)
    const created = await a.page.request.post(`${apiA}/api/conversations/direct`, {
      headers: await csrf(a.page.request, apiA), data: { targetUserId: bob.id },
    })
    expect(created.status()).toBe(201)
    const conversation = (await created.json()).id as string
    await open(a.page, conversation); await open(b.page, conversation)
    await sendUi(a.page, 'Read while A is down')
    await expect(panel(b.page).getByText('Read while A is down', { exact: true })).toBeVisible()
    const before = await (await b.page.request.get(`${apiB}/api/conversations/${conversation}/messages`)).json()
    const firstId = before.items.find((message: { body: string }) => message.body === 'Read while A is down').id
    const kill = await request.post('http://127.0.0.1:5090/stop-a')
    expect(kill.ok()).toBe(true); stopped = true
    expect((await request.get(`${apiB}/health/ready`)).ok()).toBe(true)
    expect((await b.page.request.put(`${apiB}/api/conversations/${conversation}/read-state`, {
      headers: await csrf(b.page.request, apiB), data: { messageId: firstId },
    })).status()).toBe(200)
    await sendUi(b.page, 'Message missed by A')
    const resume = await request.post('http://127.0.0.1:5090/resume-a')
    expect(resume.ok()).toBe(true); stopped = false
    expect((await resume.json()).pid).not.toBe((await kill.json()).previousPid)
    await a.page.reload(); await expect(live(a.page)).toHaveText('Ζωντανή σύνδεση', { timeout: 20000 })
    await expect(panel(a.page).getByText('Message missed by A', { exact: true })).toBeVisible()
    await expect(panel(a.page).getByLabel('Κατάσταση μηνύματος').first()).toContainText('Read')
    const history = await (await a.page.request.get(`${apiA}/api/conversations/${conversation}/messages`)).json()
    expect(history.items).toHaveLength(2)
    expect(new Set(history.items.map((message: { id: string }) => message.id)).size).toBe(2)
    const receipts = await (await a.page.request.get(`${apiA}/api/conversations/${conversation}/receipts`)).json()
    expect(receipts.items.find((item: { userId: string }) => item.userId === bob.id).read).not.toBeNull()
  } finally {
    if (stopped) await request.post('http://127.0.0.1:5090/resume-a')
    await a.context.close(); await b.context.close()
  }
})
