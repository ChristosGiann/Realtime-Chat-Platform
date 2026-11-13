import { expect, test } from '@playwright/test'
import type { Page } from '@playwright/test'
import { api, csrf, live, login, panel, register, sendUi } from './realtime-helpers'

const presence = (page: Page) => page.getByLabel('Παρουσία συνομιλίας')
const status = (page: Page) => panel(page).getByLabel('Κατάσταση μηνύματος')
const read = (page: Page) => panel(page).getByRole('button', { name: 'Σήμανση ιστορικού ως αναγνωσμένου' }).click()
async function open(page: Page, id: string) {
  await page.goto(`/#/conversations/${id}`)
  await expect(live(page)).toHaveText('Ζωντανή σύνδεση')
}
function protectedEvents(page: Page) {
  const received: { target: string; conversationId: string }[] = []
  page.on('websocket', socket => socket.on('framereceived', frame => {
    for (const raw of frame.payload.toString().split('\u001e').filter(Boolean)) {
      const value = JSON.parse(raw)
      if (value.type === 1 && ['PresenceChanged', 'TypingChanged', 'ReceiptAdvanced'].includes(value.target))
        received.push({ target: value.target, conversationId: value.arguments[0].conversationId })
    }
  }))
  return received
}

test('real presence aggregates tabs, last disconnect goes offline and removal revokes all ephemeral views', async ({ page, browser, request }) => {
  const a = await register(request), b = await register(request)
  const context = await browser.newContext({ baseURL: 'http://localhost:5173' })
  try {
    await login(page, a.email)
    const created = await page.request.post(`${api}/api/conversations/group`, { headers: await csrf(page.request), data: { name: 'Presence tabs', memberIds: [b.id] } })
    expect(created.status()).toBe(201); const id = (await created.json()).id
    await open(page, id); await expect(presence(page)).toHaveText('0 online · 1 offline (άλλα μέλη)')
    const first = await context.newPage(); await login(first, b.email)
    await expect(presence(page)).toHaveText('1 online · 0 offline (άλλα μέλη)')
    const second = await context.newPage(); await open(second, id)
    await first.close()
    await panel(page).getByRole('button', { name: 'Ανανέωση μηνυμάτων' }).click()
    await expect(presence(page)).toHaveText('1 online · 0 offline (άλλα μέλη)')
    await second.close()
    await expect(presence(page)).toHaveText('0 online · 1 offline (άλλα μέλη)', { timeout: 10000 })
    const rejoined = await context.newPage(), received = protectedEvents(rejoined)
    await open(rejoined, id); await expect(presence(page)).toHaveText('1 online · 0 offline (άλλα μέλη)')
    await rejoined.getByLabel('Νέο μήνυμα', { exact: true }).fill('Typing before revocation')
    await expect(page.getByLabel('Πληκτρολόγηση', { exact: true })).toBeVisible()
    expect((await page.request.delete(`${api}/api/conversations/${id}/members/${b.id}`, { headers: await csrf(page.request) })).status()).toBe(204)
    await expect(rejoined.getByLabel('Νέο μήνυμα', { exact: true })).toHaveCount(0)
    await expect(page.getByLabel('Πληκτρολόγηση', { exact: true })).toHaveCount(0)
    await expect(presence(page)).toHaveText('0 online · 0 offline (άλλα μέλη)')
    const count = received.length
    await sendUi(page, 'Only current member sees this')
    await panel(page).getByRole('button', { name: 'Ανανέωση μηνυμάτων' }).click(); await read(page)
    await expect(status(page)).toContainText('χωρίς άλλα τρέχοντα μέλη')
    await page.getByLabel('Νέο μήνυμα', { exact: true }).fill('Private typing')
    await page.getByLabel('Νέο μήνυμα', { exact: true }).blur()
    const refresh = panel(page).getByRole('button', { name: 'Ανανέωση μηνυμάτων' })
    await refresh.click(); await expect(refresh).toBeEnabled()
    expect(received).toHaveLength(count)
    expect((await rejoined.request.get(`${api}/api/conversations/${id}/receipts`)).status()).toBe(404)
  } finally { await context.close() }
})

test('real group counts and multi-tab read progress reset only for a new membership incarnation', async ({ page, browser, request }) => {
  const a = await register(request), b = await register(request), c = await register(request)
  const bc = await browser.newContext({ baseURL: 'http://localhost:5173' }), cc = await browser.newContext({ baseURL: 'http://localhost:5173' })
  try {
    const bob = await bc.newPage(), carol = await cc.newPage(), bobSecond = await bc.newPage()
    await login(page, a.email); await login(bob, b.email); await login(carol, c.email)
    const created = await page.request.post(`${api}/api/conversations/group`, { headers: await csrf(page.request), data: { name: 'Durable group counts', memberIds: [b.id, c.id] } })
    expect(created.status()).toBe(201); const id = (await created.json()).id
    await open(page, id); await sendUi(page, 'One message, independent recipients')
    await expect(status(page)).toContainText('Delivered 0/2 · Read 0/2')
    await open(bob, id); await open(bobSecond, id)
    await expect(status(page)).toContainText('Delivered 1/2 · Read 0/2')
    await read(bob)
    await expect(status(page)).toContainText('Delivered 1/2 · Read 1/2')
    await expect(panel(bobSecond).getByText('Μη αναγνωσμένα: 0')).toBeVisible()
    await open(carol, id); await expect(status(page)).toContainText('Delivered 2/2 · Read 1/2')
    await read(carol); await expect(status(page)).toContainText('Delivered 2/2 · Read 2/2')
    const old = await (await bob.request.get(`${api}/api/conversations/${id}/delivery-state`)).json()
    expect((await page.request.delete(`${api}/api/conversations/${id}/members/${b.id}`, { headers: await csrf(page.request) })).status()).toBe(204)
    for (const tab of [bob, bobSecond]) await expect(tab.getByLabel('Νέο μήνυμα', { exact: true })).toHaveCount(0)
    await expect(status(page)).toContainText('Delivered 1/1 · Read 1/1')
    expect((await page.request.put(`${api}/api/conversations/${id}/members/${b.id}`, { headers: await csrf(page.request) })).status()).toBe(204)
    const fresh = await (await bob.request.get(`${api}/api/conversations/${id}/delivery-state`)).json()
    expect(fresh.membershipId).not.toBe(old.membershipId); expect(fresh.lastDeliveredMessageId).toBeNull()
    expect((await bob.request.put(`${api}/api/conversations/${id}/delivery-state`, { headers: await csrf(bob.request), data: { messageId: old.lastDeliveredMessageId, membershipId: old.membershipId } })).status()).toBe(409)
    await expect(status(page)).toContainText('Delivered 1/2 · Read 1/2')
    await bob.reload(); await expect(live(bob)).toHaveText('Ζωντανή σύνδεση')
    await expect(status(page)).toContainText('Delivered 2/2 · Read 1/2')
    expect((await (await bob.request.get(`${api}/api/conversations/${id}/read-state`)).json()).lastReadMessageId).toBeNull()
    for (const user of [b, c]) expect((await page.request.delete(`${api}/api/conversations/${id}/members/${user.id}`, { headers: await csrf(page.request) })).status()).toBe(204)
    await expect(status(page)).toHaveText('Sent · χωρίς άλλα τρέχοντα μέλη')
  } finally { await bc.close(); await cc.close() }
})

test('real missed receipt while sender is offline recovers from PostgreSQL on reconnect and reload', async ({ page, context, browser, request }) => {
  const a = await register(request), b = await register(request)
  const bc = await browser.newContext({ baseURL: 'http://localhost:5173' })
  try {
    const bob = await bc.newPage(), received = protectedEvents(page)
    await login(page, a.email); await login(bob, b.email)
    const created = await page.request.post(`${api}/api/conversations/direct`, { headers: await csrf(page.request), data: { targetUserId: b.id } })
    expect(created.status()).toBe(201); const id = (await created.json()).id
    await open(page, id); await sendUi(page, 'Read while sender is offline'); await open(bob, id)
    await expect(status(page)).toHaveText('Delivered')
    await context.setOffline(true); await expect(live(page)).toContainText('Επανασύνδεση')
    const count = received.filter(x => x.target === 'ReceiptAdvanced').length
    await read(bob); await expect(panel(bob).getByText('Μη αναγνωσμένα: 0')).toBeVisible()
    const delivery = await (await bob.request.get(`${api}/api/conversations/${id}/delivery-state`)).json()
    const readState = await (await bob.request.get(`${api}/api/conversations/${id}/read-state`)).json()
    expect(readState.lastReadMessageId).not.toBeNull(); expect(delivery.lastDeliveredMessageId).toBe(readState.lastReadMessageId)
    expect(received.filter(x => x.target === 'ReceiptAdvanced')).toHaveLength(count)
    await context.setOffline(false); await expect(live(page)).toHaveText('Ζωντανή σύνδεση', { timeout: 20000 })
    await expect(status(page)).toHaveText('Read')
    await page.reload(); await expect(live(page)).toHaveText('Ζωντανή σύνδεση'); await expect(status(page)).toHaveText('Read')
  } finally { await context.setOffline(false); await bc.close() }
})
