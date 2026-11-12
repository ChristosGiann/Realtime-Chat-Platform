import { expect, test } from '@playwright/test'
import { api, csrf, live, login, register, sendUi } from './realtime-helpers'

test('real authorized typing appears for the other member and clears on idle/send/navigation', async ({ page, browser, request }) => {
  const a = await register(request), b = await register(request)
  const context = await browser.newContext({ baseURL: 'http://localhost:5173' })
  try {
    const bob = await context.newPage()
    await login(page, a.email); await login(bob, b.email)
    const response = await page.request.post(`${api}/api/conversations/direct`, { headers: await csrf(page.request), data: { targetUserId: b.id } })
    expect(response.status()).toBe(201); const id = (await response.json()).id
    await page.goto(`/#/conversations/${id}`); await bob.goto(`/#/conversations/${id}`)
    await expect(live(page)).toHaveText('Ζωντανή σύνδεση'); await expect(live(bob)).toHaveText('Ζωντανή σύνδεση')
    await page.getByLabel('Νέο μήνυμα', { exact: true }).fill('Writing')
    await expect(bob.getByLabel('Πληκτρολόγηση', { exact: true })).toBeVisible()
    await expect(page.getByLabel('Πληκτρολόγηση', { exact: true })).toHaveCount(0)
    await expect(bob.getByLabel('Πληκτρολόγηση', { exact: true })).toHaveCount(0, { timeout: 7000 })
    await sendUi(page, 'Durable message')
    await expect(bob.getByLabel('Πληκτρολόγηση', { exact: true })).toHaveCount(0)
    await bob.getByLabel('Νέο μήνυμα', { exact: true }).fill('Bob writing')
    await expect(page.getByLabel('Πληκτρολόγηση', { exact: true })).toBeVisible()
    await bob.getByRole('link', { name: 'Realtime Chat' }).click()
    await expect(page.getByLabel('Πληκτρολόγηση', { exact: true })).toHaveCount(0)
  } finally { await context.close() }
})
