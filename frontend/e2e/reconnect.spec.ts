import { expect, test } from '@playwright/test'
import { api, csrf, live, login, panel, register, send, sendUi } from './realtime-helpers'

test('real disconnect recovers a multi-page gap and preserves deduplicated durable state', async ({ page, browser, request }) => {
  const alice = await register(request), bob = await register(request)
  const context = await browser.newContext({ baseURL: 'http://localhost:5173' })
  try {
    const bobPage = await context.newPage()
    await login(page, alice.email); await login(bobPage, bob.email)
    const created = await page.request.post(`${api}/api/conversations/direct`, { headers: await csrf(page.request), data: { targetUserId: bob.id } })
    expect(created.status()).toBe(201); const id = (await created.json()).id as string
    await page.goto(`/#/conversations/${id}`); await bobPage.goto(`/#/conversations/${id}`)
    await expect(live(page)).toHaveText('Ζωντανή σύνδεση'); await expect(live(bobPage)).toHaveText('Ζωντανή σύνδεση')
    await sendUi(page, 'Before offline')
    await expect(panel(bobPage).getByText('Before offline', { exact: true })).toBeVisible()
    await panel(bobPage).getByRole('button', { name: 'Ανανέωση μηνυμάτων' }).click()
    await expect(panel(bobPage).getByRole('button', { name: 'Σήμανση ιστορικού ως αναγνωσμένου' })).toBeEnabled()
    await panel(bobPage).getByRole('button', { name: 'Σήμανση ιστορικού ως αναγνωσμένου' }).click()
    await expect(panel(bobPage).getByText('Μη αναγνωσμένα: 0')).toBeVisible()
    await context.setOffline(true)
    await expect(live(bobPage)).toContainText('Επανασύνδεση')
    for (let i = 0; i < 35; i++) await send(page.request, id, `Offline ${i}`)
    await expect(panel(bobPage).locator('[data-message-id]')).toHaveCount(1)
    await context.setOffline(false)
    await expect(live(bobPage)).toHaveText('Ζωντανή σύνδεση', { timeout: 20000 })
    await expect(panel(bobPage).locator('[data-message-id]')).toHaveCount(36)
    await expect(panel(bobPage).getByText('Μη αναγνωσμένα: 35')).toBeVisible()
    await sendUi(bobPage, 'After reconnect')
    await expect(panel(page).getByText('After reconnect', { exact: true })).toBeVisible()
    await expect(panel(bobPage).locator('[data-message-id]')).toHaveCount(37)
    await bobPage.reload()
    await expect(live(bobPage)).toHaveText('Ζωντανή σύνδεση')
    await expect(panel(bobPage).locator('[data-message-id]')).toHaveCount(30)
    await panel(bobPage).getByRole('button', { name: 'Παλαιότερα μηνύματα' }).click()
    await expect(panel(bobPage).locator('[data-message-id]')).toHaveCount(37)
  } finally { await context.close() }
})
