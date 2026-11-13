import { expect } from '@playwright/test'
import type { APIRequestContext, Page } from '@playwright/test'
import { randomUUID } from 'node:crypto'

export const api = 'http://localhost:5080'
export const password = 'Test-password123!'
export async function register(request: APIRequestContext) {
  const userName = 's4' + randomUUID().replaceAll('-', '')
  const email = `${userName}@example.test`
  const response = await request.post(`${api}/api/auth/register`, { data: { userName, email, password, displayName: userName } })
  expect(response.status()).toBe(201)
  return { id: (await response.json()).id as string, email }
}
export async function login(page: Page, email: string) {
  await page.goto('/#/login'); await page.getByLabel('Email', { exact: true }).fill(email)
  await page.getByLabel('Κωδικός', { exact: true }).fill(password)
  await page.getByRole('button', { name: 'Σύνδεση', exact: true }).click()
  await expect(page.getByRole('heading', { name: /Καλώς ήρθες/ })).toBeVisible()
}
export async function csrf(request: APIRequestContext) {
  const response = await request.get(`${api}/api/auth/csrf`); expect(response.ok()).toBe(true)
  return { 'X-CSRF-TOKEN': (await response.json()).requestToken as string }
}
export const panel = (page: Page) => page.getByRole('region', { name: 'Μηνύματα συνομιλίας' })
export const live = (page: Page) => page.getByLabel('Κατάσταση live σύνδεσης')
export async function sendUi(page: Page, body: string) {
  await page.getByLabel('Νέο μήνυμα', { exact: true }).fill(body)
  await panel(page).getByRole('button', { name: 'Αποστολή', exact: true }).click()
  // The committed live echo can arrive before REST clears the composer.
  await expect(panel(page).getByRole('list', { name: 'Ιστορικό μηνυμάτων' }).getByText(body, { exact: true })).toBeVisible()
  await expect(page.getByLabel('Νέο μήνυμα', { exact: true })).toHaveValue('')
}
export async function send(request: APIRequestContext, id: string, body: string, clientMessageId = randomUUID()) {
  const response = await request.post(`${api}/api/conversations/${id}/messages`, { headers: await csrf(request), data: { clientMessageId, body } })
  expect([200, 201]).toContain(response.status())
  return response.json()
}
