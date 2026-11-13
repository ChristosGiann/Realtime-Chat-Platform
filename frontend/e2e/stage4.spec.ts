import { expect, test } from '@playwright/test'
import type { Page } from '@playwright/test'
import { randomUUID } from 'node:crypto'
import type { Message } from '../src/api/messages'
import { api, csrf, live, login, panel, register, send, sendUi } from './realtime-helpers'

// Observe actual server frames; no API or WebSocket mocks are installed in this suite.
function events(page: Page) {
  const messages: Message[] = []
  page.on('websocket', socket => {
    if (!socket.url().includes('/hubs/chat')) return
    socket.on('framereceived', frame => {
      for (const raw of frame.payload.toString().split('\u001e').filter(Boolean)) {
        const value = JSON.parse(raw)
        if (value.type === 1 && value.target === 'MessageCreated') messages.push(value.arguments[0])
      }
    })
  })
  return messages
}

test('real A/B live frames reconcile once and rejected writes never broadcast', async ({ page, browser, request }) => {
  const a = await register(request), b = await register(request), outsider = await register(request)
  const context = await browser.newContext({ baseURL: 'http://localhost:5173' })
  const outsiderContext = await browser.newContext({ baseURL: 'http://localhost:5173' })
  try {
    const bob = await context.newPage(), other = await outsiderContext.newPage()
    const aEvents = events(page), bEvents = events(bob)
    await login(page, a.email); await login(bob, b.email); await login(other, outsider.email)
    const created = await page.request.post(`${api}/api/conversations/direct`, { headers: await csrf(page.request), data: { targetUserId: b.id } })
    expect(created.status()).toBe(201); const id = (await created.json()).id as string
    await page.goto(`/#/conversations/${id}`); await bob.goto(`/#/conversations/${id}`)
    await expect(live(page)).toHaveText('Ζωντανή σύνδεση'); await expect(live(bob)).toHaveText('Ζωντανή σύνδεση')
    await sendUi(page, 'A → B live'); await expect(panel(bob).getByText('A → B live', { exact: true })).toBeVisible()
    await sendUi(bob, 'B → A live'); await expect(panel(page).getByText('B → A live', { exact: true })).toBeVisible()
    const key = randomUUID(), saved = await send(page.request, id, 'Stable retry', key)
    expect((await send(page.request, id, 'Stable retry', key)).id).toBe(saved.id)
    const url = `${api}/api/conversations/${id}/messages`, headers = await csrf(page.request)
    expect((await page.request.post(url, { headers, data: { clientMessageId: key, body: 'Conflict rejected' } })).status()).toBe(409)
    expect((await page.request.post(url, { headers, data: { clientMessageId: randomUUID(), body: '   ' } })).status()).toBe(400)
    expect((await other.request.post(url, { headers: await csrf(other.request), data: { clientMessageId: randomUUID(), body: 'Outsider rejected' } })).status()).toBe(404)
    // Awaited writes precede this marker on each subscribed socket.
    await sendUi(page, 'Delivery marker')
    await expect(panel(bob).getByText('Delivery marker', { exact: true })).toBeVisible()
    for (const received of [aEvents, bEvents]) {
      await expect.poll(() => received.length).toBe(4)
      expect(received.filter(message => message.clientMessageId === key)).toHaveLength(1)
      expect(received.map(message => message.body)).not.toContain('Conflict rejected')
      expect(received.map(message => message.body)).not.toContain('Outsider rejected')
      expect(received).toHaveLength(4)
    }
    await expect(panel(page).locator('[data-message-id]')).toHaveCount(4)
    await expect(panel(bob).locator('[data-message-id]')).toHaveCount(4)
    const history = await (await bob.request.get(`${url}?limit=30`)).json()
    expect(new Set(history.items.map((message: Message) => message.id)).size).toBe(4)
    const next = await page.request.post(`${api}/api/conversations/group`, { headers, data: { name: 'Switched conversation', memberIds: [] } })
    expect(next.status()).toBe(201); const nextId = (await next.json()).id as string
    await page.goto(`/#/conversations/${nextId}`); await expect(live(page)).toHaveText('Ζωντανή σύνδεση')
    await sendUi(bob, 'Previous conversation after switch')
    await sendUi(page, 'New conversation marker')
    await expect(panel(page).locator('[data-message-id]')).toHaveCount(1)
    expect(aEvents.map(message => message.body)).not.toContain('Previous conversation after switch')
  } finally { await context.close(); await outsiderContext.close() }
})

test('real membership removal revokes two tabs and rejoin recovers through authorized REST', async ({ page, browser, request }) => {
  const owner = await register(request), member = await register(request)
  const context = await browser.newContext({ baseURL: 'http://localhost:5173' })
  try {
    const first = await context.newPage(), second = await context.newPage()
    const firstEvents = events(first), secondEvents = events(second)
    await login(page, owner.email); await login(first, member.email)
    const headers = await csrf(page.request)
    const created = await page.request.post(`${api}/api/conversations/group`, { headers, data: { name: 'Live two tabs', memberIds: [member.id] } })
    expect(created.status()).toBe(201); const id = (await created.json()).id as string
    for (const tab of [page, first, second]) { await tab.goto(`/#/conversations/${id}`); await expect(live(tab)).toHaveText('Ζωντανή σύνδεση') }
    await sendUi(page, 'Before removal')
    for (const tab of [first, second]) await expect(panel(tab).getByText('Before removal', { exact: true })).toBeVisible()
    expect((await page.request.delete(`${api}/api/conversations/${id}/members/${member.id}`, { headers })).status()).toBe(204)
    await sendUi(page, 'After removal secret')
    for (const tab of [first, second]) {
      await expect(panel(tab).getByText('After removal secret', { exact: true })).toHaveCount(0)
      await expect(tab.getByLabel('Νέο μήνυμα', { exact: true })).toHaveCount(0)
      await expect(panel(tab).locator('[data-message-id]')).toHaveCount(0)
    }
    expect((await page.request.put(`${api}/api/conversations/${id}/members/${member.id}`, { headers })).status()).toBe(204)
    for (const tab of [first, second]) { await tab.reload(); await expect(live(tab)).toHaveText('Ζωντανή σύνδεση') }
    await sendUi(page, 'After authorized rejoin')
    for (const tab of [first, second]) {
      await expect(panel(tab).getByText('After authorized rejoin', { exact: true })).toBeVisible()
      await expect(panel(tab).getByText('After removal secret', { exact: true })).toBeVisible() // Durable history policy allows rejoined members.
    }
    for (const received of [firstEvents, secondEvents]) {
      expect(received.map(message => message.body)).toContain('After authorized rejoin')
      expect(received.map(message => message.body)).not.toContain('After removal secret')
    }
  } finally { await context.close() }
})

for (const lostAccess of ['membership', 'session']) test(`real reconnect rejects lost ${lostAccess}`, async ({ page, browser, request }) => {
  const owner = await register(request), member = await register(request)
  const context = await browser.newContext({ baseURL: 'http://localhost:5173' })
  try {
    const tab = await context.newPage()
    await login(page, owner.email); await login(tab, member.email)
    const headers = await csrf(page.request)
    const created = await page.request.post(`${api}/api/conversations/group`, { headers, data: { name: `Reconnect ${lostAccess}`, memberIds: [member.id] } })
    expect(created.status()).toBe(201); const id = (await created.json()).id as string
    await tab.goto(`/#/conversations/${id}`); await expect(live(tab)).toHaveText('Ζωντανή σύνδεση')
    await context.setOffline(true); await expect(live(tab)).toContainText('Επανασύνδεση')
    if (lostAccess === 'membership') expect((await page.request.delete(`${api}/api/conversations/${id}/members/${member.id}`, { headers })).status()).toBe(204)
    else await context.clearCookies()
    await send(page.request, id, 'Not authorized after reconnect')
    await context.setOffline(false)
    if (lostAccess === 'session') await expect(tab.getByRole('heading', { name: 'Σύνδεση', exact: true })).toBeVisible()
    else await expect(tab.getByRole('alert').first()).toBeVisible()
    await expect(tab.getByLabel('Νέο μήνυμα', { exact: true })).toHaveCount(0)
    await expect(tab.getByText('Not authorized after reconnect', { exact: true })).toHaveCount(0)
  } finally { await context.close() }
})
