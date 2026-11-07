import { expect, test } from '@playwright/test'
import type { Page } from '@playwright/test'
import { randomUUID } from 'node:crypto'

async function connect(page: Page) {
  return page.evaluate(() => new Promise<boolean>(resolve => {
    const socket = new WebSocket('ws://localhost:5080/hubs/chat')
    const finish = (success: boolean) => { clearTimeout(timeout); socket.close(); resolve(success) }
    const timeout = setTimeout(() => finish(false), 10000)
    socket.onopen = () => socket.send('{"protocol":"json","version":1}\u001e')
    socket.onmessage = event => finish(String(event.data).split('\u001e')[0] === '{}')
    socket.onerror = () => finish(false)
    socket.onclose = () => { clearTimeout(timeout); resolve(false) }
  }))
}

test('browser WebSocket handshake uses the existing login cookie and rejects anonymous/logout', async ({ page, request }) => {
  await page.goto('/#/login'); expect(await connect(page)).toBe(false)
  const userName = 'hub' + randomUUID().replaceAll('-', '')
  const email = `${userName}@example.test`, password = 'Test-password123!'
  const registered = await request.post('http://localhost:5080/api/auth/register', { data: { email, userName, password } })
  expect(registered.status()).toBe(201)
  await page.getByLabel('Email', { exact: true }).fill(email)
  await page.getByLabel('Κωδικός', { exact: true }).fill(password)
  await page.getByRole('button', { name: 'Σύνδεση', exact: true }).click()
  await expect(page.getByRole('heading', { name: /Καλώς ήρθες/ })).toBeVisible()
  expect(await connect(page)).toBe(true)
  await page.getByRole('button', { name: 'Αποσύνδεση', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Σύνδεση', exact: true })).toBeVisible()
  expect(await connect(page)).toBe(false)
})
