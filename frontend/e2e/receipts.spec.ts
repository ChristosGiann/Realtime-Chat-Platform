import { expect, test } from '@playwright/test'
import { api, csrf, live, login, panel, register, sendUi } from './realtime-helpers'

test('real direct receipts advance Sent to Delivered to Read only after observation and explicit read', async ({ page, browser, request }) => {
  const a = await register(request), b = await register(request)
  const context = await browser.newContext({ baseURL: 'http://localhost:5173' })
  try {
    const bob = await context.newPage()
    await login(page, a.email); await login(bob, b.email)
    const created = await page.request.post(`${api}/api/conversations/direct`, { headers: await csrf(page.request), data: { targetUserId: b.id } })
    expect(created.status()).toBe(201); const id = (await created.json()).id
    await page.goto(`/#/conversations/${id}`); await expect(live(page)).toHaveText('Ζωντανή σύνδεση')
    await sendUi(page, 'Delivery is durable observation')
    const status = panel(page).getByLabel('Κατάσταση μηνύματος')
    await expect(status).toHaveText('Sent')
    await bob.goto(`/#/conversations/${id}`); await expect(live(bob)).toHaveText('Ζωντανή σύνδεση')
    await expect(status).toHaveText('Delivered')
    const before = await (await bob.request.get(`${api}/api/conversations/${id}/read-state`)).json()
    expect(before.lastReadMessageId).toBeNull()
    await panel(bob).getByRole('button', { name: 'Σήμανση ιστορικού ως αναγνωσμένου' }).click()
    await expect(status).toHaveText('Read')
    const read = await (await bob.request.get(`${api}/api/conversations/${id}/read-state`)).json()
    const delivered = await (await bob.request.get(`${api}/api/conversations/${id}/delivery-state`)).json()
    expect(delivered.lastDeliveredMessageId).toBe(read.lastReadMessageId)
    await page.reload(); await expect(live(page)).toHaveText('Ζωντανή σύνδεση')
    await expect(status).toHaveText('Read')
  } finally { await context.close() }
})
