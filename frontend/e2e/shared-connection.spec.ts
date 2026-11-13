import { expect, test } from '@playwright/test'
import { api, csrf, live, login, panel, register, send } from './realtime-helpers'

test('one real connection per tab survives navigation and tab cleanup is independent', async ({ page, context, request }) => {
  const user = await register(request)
  let firstConnections = 0, secondConnections = 0, firstClosed = 0
  page.on('websocket', socket => {
    if (!socket.url().includes('/hubs/chat')) return
    firstConnections++; socket.on('close', () => { firstClosed++ })
  })
  await login(page, user.email)
  await expect.poll(() => firstConnections).toBe(1)
  const ids: string[] = []
  for (const name of ['Shared first', 'Shared second']) {
    const response = await page.request.post(`${api}/api/conversations/group`, {
      headers: await csrf(page.request), data: { name, memberIds: [] },
    })
    expect(response.status()).toBe(201); ids.push((await response.json()).id)
  }
  const second = await context.newPage()
  try {
    second.on('websocket', socket => { if (socket.url().includes('/hubs/chat')) secondConnections++ })
    await second.goto(`/#/conversations/${ids[0]}`)
    await expect(live(second)).toHaveText('Ζωντανή σύνδεση')
    for (const id of [ids[0], ids[1], ids[0]]) {
      await page.evaluate(id => { location.hash = `/conversations/${id}` }, id)
      await expect(live(page)).toHaveText('Ζωντανή σύνδεση')
    }
    await send(page.request, ids[0], 'Both independent tabs')
    for (const tab of [page, second]) await expect(panel(tab).getByText('Both independent tabs', { exact: true })).toBeVisible()
    expect(firstConnections).toBe(1); expect(secondConnections).toBe(1); expect(firstClosed).toBe(0)
    await second.close()
    await page.getByRole('link', { name: 'Realtime Chat' }).click()
    await expect(page.getByRole('heading', { name: /Καλώς ήρθες/ })).toBeVisible()
    await page.evaluate(id => { location.hash = `/conversations/${id}` }, ids[0])
    await expect(live(page)).toHaveText('Ζωντανή σύνδεση')
    await send(page.request, ids[0], 'Remaining tab still live')
    await expect(panel(page).getByText('Remaining tab still live', { exact: true })).toBeVisible()
    expect(firstConnections).toBe(1)
    await page.getByRole('button', { name: 'Αποσύνδεση', exact: true }).click()
    await expect(page.getByRole('heading', { name: 'Σύνδεση', exact: true })).toBeVisible()
    await expect.poll(() => firstClosed).toBe(1)
  } finally { if (!second.isClosed()) await second.close() }
})
