import { expect, test } from '@playwright/test'
import type { APIRequestContext, Page } from '@playwright/test'
import { randomUUID } from 'node:crypto'
const api = 'http://localhost:5080'
const password = 'Test-password123!'
async function register(request: APIRequestContext) {
  const userName = 's2' + randomUUID().replaceAll('-', '')
  const user = { userName, email: `${userName}@example.test`, password, displayName: userName }
  const response = await request.post(`${api}/api/auth/register`, { data: user })
  expect(response.status()).toBe(201)
  return { ...user, id: (await response.json()).id as string }
}
async function login(page: Page, email: string) {
  await page.goto('http://localhost:5173/#/login')
  await page.getByLabel('Email', { exact: true }).fill(email)
  await page.getByLabel('Κωδικός', { exact: true }).fill(password)
  await page.getByRole('button', { name: 'Σύνδεση', exact: true }).click()
  await expect(page.getByRole('heading', { name: /Καλώς ήρθες/ })).toBeVisible()
}
async function pick(page: Page, name: string) {
  await page.getByLabel('Αναζήτηση ατόμων', { exact: true }).fill(name)
  await page.getByRole('button', { name: 'Αναζήτηση ατόμων', exact: true }).click()
  await page.getByRole('button', { name: `Επιλογή ${name}`, exact: true }).click()
}
async function csrf(request: APIRequestContext) {
  const response = await request.get(`${api}/api/auth/csrf`)
  expect(response.ok()).toBe(true)
  return { 'X-CSRF-TOKEN': (await response.json()).requestToken as string }
}
async function details(request: APIRequestContext, id: string) {
  const response = await request.get(`${api}/api/conversations/${id}`)
  expect(response.status()).toBe(200)
  return response.json()
}
async function selectedId(page: Page) {
  await expect(page).toHaveURL(/#\/conversations\/[0-9a-f-]{36}$/)
  return page.url().split('/').at(-1)!
}

test('real Direct UI creation, reciprocal concurrent reuse and participant-only access', async ({ page, request, browser }, testInfo) => {
  const alice = await register(request); const bob = await register(request); const outsider = await register(request)
  const bobContext = await browser.newContext(); const outsiderContext = await browser.newContext()
  try {
    const bobPage = await bobContext.newPage(); const outsiderPage = await outsiderContext.newPage()
    await login(page, alice.email); await login(bobPage, bob.email); await login(outsiderPage, outsider.email)
    await page.getByRole('link', { name: 'Συνομιλίες', exact: true }).click()
    await expect(page.getByText('Δεν έχεις ακόμη συνομιλίες.')).toBeVisible()
    await page.getByRole('link', { name: 'Νέα συνομιλία', exact: true }).click()
    await pick(page, bob.userName)
    const id = await selectedId(page)
    await expect(page.getByRole('link', { name: bob.userName, exact: true })).toBeVisible()
    const aliceHeaders = await csrf(page.request); const bobHeaders = await csrf(bobPage.request)
    const repeats = await Promise.all(Array.from({ length: 12 }, (_, index) => (index % 2 ? bobPage.request : page.request)
      .post(`${api}/api/conversations/direct`, { headers: index % 2 ? bobHeaders : aliceHeaders, data: { targetUserId: index % 2 ? alice.id : bob.id } })))
    for (const response of repeats) { expect(response.status()).toBe(200); expect((await response.json()).id).toBe(id) }
    for (const client of [page.request, bobPage.request]) {
      const list = await (await client.get(`${api}/api/conversations`)).json()
      expect(list.items.map((x: { id: string }) => x.id)).toEqual([id])
      const saved = await details(client, id)
      expect(saved.type).toBe('Direct'); expect(saved.memberCount).toBe(2)
      expect(saved.members.map((m: { userId: string }) => m.userId).sort()).toEqual([alice.id, bob.id].sort())
      for (const member of saved.members) expect(Object.keys(member.profile).sort()).toEqual(['avatarUrl', 'bio', 'displayName', 'userId'])
    }
    expect((await outsiderPage.request.get(`${api}/api/conversations/${id}`)).status()).toBe(404)
    expect((await request.get(`${api}/api/conversations/${id}`)).status()).toBe(401)
    const outsiderHeaders = await csrf(outsiderPage.request)
    expect((await outsiderPage.request.put(`${api}/api/conversations/${id}/members/${outsider.id}`, { headers: outsiderHeaders })).status()).toBe(404)
    expect((await page.request.delete(`${api}/api/conversations/${id}/membership`, { headers: aliceHeaders })).status()).toBe(409)
    await bobPage.goto(`http://localhost:5173/#/conversations/${id}`)
    await expect(bobPage.getByRole('link', { name: alice.userName, exact: true })).toBeVisible()
    await page.screenshot({ path: testInfo.outputPath('stage2-direct-desktop.png'), fullPage: true })
  } finally { await bobContext.close(); await outsiderContext.close() }
})

test('real Group UI creation, owner add/remove, member leave and cross-user rejection', async ({ page, request, browser }, testInfo) => {
  const alice = await register(request); const bob = await register(request); const carol = await register(request); const outsider = await register(request)
  const bobContext = await browser.newContext(); const carolContext = await browser.newContext(); const outsiderContext = await browser.newContext()
  try {
    const bobPage = await bobContext.newPage(); const carolPage = await carolContext.newPage(); const outsiderPage = await outsiderContext.newPage()
    await login(page, alice.email); await login(bobPage, bob.email); await login(carolPage, carol.email); await login(outsiderPage, outsider.email)
    await page.goto('http://localhost:5173/#/conversations/new')
    await page.getByRole('button', { name: 'Ομάδα', exact: true }).click()
    await pick(page, bob.userName)
    const name = 'Stage 2 ' + randomUUID()
    await page.getByLabel('Όνομα ομάδας').fill(name)
    await page.getByRole('button', { name: 'Δημιουργία ομάδας' }).click()
    const id = await selectedId(page)
    await expect(page.getByRole('heading', { name, exact: true })).toBeVisible()
    let saved = await details(page.request, id)
    expect(saved.memberCount).toBe(2)
    expect(saved.members.find((m: { userId: string }) => m.userId === alice.id).role).toBe('Owner')
    expect(saved.members.find((m: { userId: string }) => m.userId === bob.id).role).toBe('Member')
    const bobHeaders = await csrf(bobPage.request); const ownerHeaders = await csrf(page.request); const outsiderHeaders = await csrf(outsiderPage.request)
    expect((await bobPage.request.put(`${api}/api/conversations/${id}/members/${carol.id}`, { headers: bobHeaders })).status()).toBe(403)
    expect((await outsiderPage.request.delete(`${api}/api/conversations/${id}/members/${bob.id}`, { headers: outsiderHeaders })).status()).toBe(404)
    expect((await outsiderPage.request.get(`${api}/api/conversations/${id}`)).status()).toBe(404)
    expect((await (await outsiderPage.request.get(`${api}/api/conversations`)).json()).items).toEqual([])
    expect((await page.request.delete(`${api}/api/conversations/${id}/membership`, { headers: ownerHeaders })).status()).toBe(409)
    expect((await page.request.delete(`${api}/api/conversations/${id}/members/${alice.id}`, { headers: ownerHeaders })).status()).toBe(409)
    expect((await page.request.put(`${api}/api/conversations/${id}/members/${carol.id}`)).status()).toBe(400)
    await pick(page, carol.userName)
    await expect(page.getByRole('link', { name: carol.userName, exact: true })).toBeVisible()
    await page.getByRole('button', { name: `Αφαίρεση ${bob.userName}`, exact: true }).click()
    await expect(page.getByRole('link', { name: bob.userName, exact: true })).toHaveCount(0)
    expect((await bobPage.request.get(`${api}/api/conversations/${id}`)).status()).toBe(404)
    expect((await (await bobPage.request.get(`${api}/api/conversations`)).json()).items).toEqual([])
    await carolPage.getByRole('link', { name: 'Συνομιλίες', exact: true }).click()
    await carolPage.getByRole('link', { name, exact: true }).click()
    await expect(carolPage.getByRole('heading', { name: 'Προσθήκη μέλους', exact: true })).toHaveCount(0)
    await carolPage.getByRole('button', { name: 'Αποχώρηση από ομάδα' }).click()
    await expect(carolPage.getByText('Δεν έχεις ακόμη συνομιλίες.')).toBeVisible()
    saved = await details(page.request, id)
    expect(saved.memberCount).toBe(1); expect(saved.members[0].userId).toBe(alice.id)
    await page.getByRole('button', { name: 'Ανανέωση στοιχείων' }).click()
    await expect(page.getByRole('link', { name: carol.userName, exact: true })).toHaveCount(0)
    await page.setViewportSize({ width: 390, height: 844 })
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
    await page.screenshot({ path: testInfo.outputPath('stage2-group-mobile.png'), fullPage: true })
  } finally { await bobContext.close(); await carolContext.close(); await outsiderContext.close() }
})

test('real conversation pagination and expired session clear protected UI', async ({ page, request }) => {
  const alice = await register(request); await login(page, alice.email)
  const headers = await csrf(page.request)
  const ids = new Set<string>()
  for (let index = 0; index < 21; index++) {
    const response = await page.request.post(`${api}/api/conversations/group`, { headers, data: { name: `Page group ${index}`, memberIds: [] } })
    expect(response.status()).toBe(201); ids.add((await response.json()).id)
  }
  await page.goto('http://localhost:5173/#/conversations')
  const rows = page.getByRole('complementary', { name: 'Λίστα συνομιλιών' }).locator('.results li')
  await expect(rows).toHaveCount(20)
  await page.getByRole('button', { name: 'Περισσότερες συνομιλίες' }).click()
  await expect(rows).toHaveCount(21)
  const links = await rows.locator('a').evaluateAll(elements => elements.map(x => x.getAttribute('href')!.split('/').at(-1)))
  expect(new Set(links)).toEqual(ids)
  expect((await page.request.post(`${api}/api/auth/logout`, { headers })).status()).toBe(204)
  await page.getByRole('button', { name: 'Ανανέωση λίστας' }).click()
  await expect(page.getByRole('heading', { name: 'Σύνδεση', exact: true })).toBeVisible()
})
