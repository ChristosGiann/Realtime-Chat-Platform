import { expect, test } from '@playwright/test'
import type { Page, TestInfo } from '@playwright/test'
import { randomUUID } from 'node:crypto'
import { execFileSync } from 'node:child_process'

const password = 'Test-password123!'
function account() {
  const userName = 'prod' + randomUUID().replaceAll('-', '')
  return { userName, email: `${userName}@example.test`, password }
}

async function recoverConversationScreen(page: Page, conversationId: string, testInfo: TestInfo) {
  const live = page.getByLabel('Κατάσταση live σύνδεσης')
  const authError = page.getByRole('heading', { name: 'Αδυναμία ελέγχου συνεδρίας' })
  const detailsError = page.getByRole('alert').filter({ hasText: 'Αδυναμία φόρτωσης στοιχείων:' })
  try {
    await expect(page).toHaveURL(new RegExp(`#/conversations/${conversationId}$`))
    // A ready API does not imply that the browser's first post-restart REST calls
    // succeeded. Use the existing explicit recovery controls for degraded screens.
    for (let attempt = 0; attempt < 2; attempt++) {
      await expect(live.or(authError).or(detailsError).first()).toBeVisible()
      if (await authError.isVisible()) await page.getByRole('button', { name: 'Δοκιμή ξανά' }).click()
      else if (await detailsError.isVisible()) {
        const refresh = page.getByRole('button', { name: 'Ανανέωση στοιχείων' })
        if (!await refresh.isVisible()) await page.getByRole('button', { name: 'Στοιχεία συνομιλίας' }).click()
        await refresh.click()
      } else break
    }
    await expect(live).toHaveText('Ζωντανή σύνδεση')
  } catch (error) {
    await testInfo.attach('restart-browser-state', {
      body: JSON.stringify({ url: page.url(), main: await page.locator('main').innerText().catch(() => '<unavailable>') }),
      contentType: 'application/json',
    })
    throw error
  }
}

test('packaged same-origin app keeps secure session, CSRF, SignalR and durable chat across restart', async ({ page, browser, request }, testInfo) => {
  const socketUrls: string[] = []
  const hubUrl = new URL('/hubs/chat', process.env.PRODUCTION_BASE_URL)
  hubUrl.protocol = 'wss:'
  page.on('websocket', socket => socketUrls.push(socket.url()))
  await page.goto('/')
  await expect(page).toHaveTitle(/Apothiki/)
  await page.goto('/conversations')
  await expect(page).toHaveTitle(/Apothiki/)
  await page.goto('/#/register')
  const alice = account(), bob = account()
  await page.getByLabel('Email', { exact: true }).fill(alice.email)
  await page.getByLabel('Όνομα χρήστη', { exact: true }).fill(alice.userName)
  await page.getByLabel('Κωδικός', { exact: true }).fill(password)
  await page.getByRole('button', { name: 'Δημιουργία λογαριασμού', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Ο λογαριασμός δημιουργήθηκε' })).toBeVisible()
  const bobRegistration = await request.post('/api/auth/register', { data: bob })
  expect(bobRegistration.status()).toBe(201)
  const bobId = (await bobRegistration.json()).id as string
  await page.goto('/#/login')
  await page.getByLabel('Email', { exact: true }).fill(alice.email)
  await page.getByLabel('Κωδικός', { exact: true }).fill(password)
  await page.getByRole('button', { name: 'Σύνδεση', exact: true }).click()
  await expect(page.getByRole('heading', { name: /Καλώς ήρθες/ })).toBeVisible()
  const session = (await page.context().cookies()).find(cookie => cookie.name === '__Host-Chat.Session')
  expect(session).toMatchObject({ secure: true, httpOnly: true, sameSite: 'Lax' })
  const csrfResponse = await page.request.get('/api/auth/csrf')
  expect(csrfResponse.status()).toBe(200)
  const csrfCookie = (await page.context().cookies()).find(cookie => cookie.name === '__Host-Chat.Csrf')
  expect(csrfCookie).toMatchObject({ secure: true, httpOnly: true, sameSite: 'Strict' })
  const token = (await csrfResponse.json()).requestToken as string
  const directPayload = { targetUserId: bobId }
  const missingCsrf = await page.request.post('/api/conversations/direct', { data: directPayload })
  expect(missingCsrf.status()).toBe(400)
  expect((await missingCsrf.json()).title).toBe('Invalid CSRF token.')
  const invalidCsrf = await page.request.post('/api/conversations/direct', {
    headers: { 'X-CSRF-TOKEN': 'invalid-token' }, data: directPayload,
  })
  expect(invalidCsrf.status()).toBe(400)
  expect((await invalidCsrf.json()).title).toBe('Invalid CSRF token.')
  const bobContext = await browser.newContext({ baseURL: process.env.PRODUCTION_BASE_URL, ignoreHTTPSErrors: true })
  try {
    const bobPage = await bobContext.newPage()
    await bobPage.goto('/#/login')
    await bobPage.getByLabel('Email', { exact: true }).fill(bob.email)
    await bobPage.getByLabel('Κωδικός', { exact: true }).fill(password)
    await bobPage.getByRole('button', { name: 'Σύνδεση', exact: true }).click()
    await expect(bobPage.getByRole('heading', { name: /Καλώς ήρθες/ })).toBeVisible()
    expect((await bobPage.request.get('/api/auth/me')).status()).toBe(200)
    const created = await page.request.post('/api/conversations/direct', { headers: { 'X-CSRF-TOKEN': token }, data: directPayload })
    expect(created.status()).toBe(201)
    const conversationId = (await created.json()).id as string
    await page.goto(`/#/conversations/${conversationId}`)
    await bobPage.goto(`/#/conversations/${conversationId}`)
    await expect(page.getByLabel('Κατάσταση live σύνδεσης')).toHaveText('Ζωντανή σύνδεση')
    await expect.poll(() => socketUrls.some(url => url.startsWith(hubUrl.toString()))).toBe(true)
    await expect(bobPage.getByLabel('Κατάσταση live σύνδεσης')).toHaveText('Ζωντανή σύνδεση')
    const body = `Production smoke ${randomUUID()}`
    await page.getByLabel('Νέο μήνυμα', { exact: true }).fill(body)
    await page.getByRole('region', { name: 'Μηνύματα συνομιλίας' }).getByRole('button', { name: 'Αποστολή', exact: true }).click()
    await expect(bobPage.getByText(body, { exact: true })).toBeVisible()
    const history = await bobPage.request.get(`/api/conversations/${conversationId}/messages`)
    expect(history.status()).toBe(200)
    expect(JSON.stringify(await history.json())).toContain(body)
    // Prove the existing details refresh path with a deterministic transient REST failure.
    await page.route(`**/api/conversations/${conversationId}`, route => route.fulfill({ status: 503, json: { title: 'Temporary failure' } }), { times: 1 })
    await page.reload()
    await recoverConversationScreen(page, conversationId, testInfo)
    const restart = process.env.PRODUCTION_DOCKER_CONTAINER
    if (restart) {
      execFileSync('docker', ['restart', restart], { timeout: 30000 })
      await expect.poll(async () => (await page.request.get('/health/ready')).status(), { timeout: 30000 }).toBe(200)
      await page.reload(); await bobPage.reload()
      expect((await page.request.get('/api/auth/me')).status()).toBe(200)
      expect((await bobPage.request.get('/api/auth/me')).status()).toBe(200)
      await expect(bobPage.getByText(body, { exact: true })).toBeVisible()
      await recoverConversationScreen(page, conversationId, testInfo)
    }
    if (process.env.PRODUCTION_DOCKER_REDEPLOY === 'true') {
      const connection = process.env.PRODUCTION_DB_CONNECTION_STRING
      const keys = process.env.PRODUCTION_KEY_BIND_PATH
      const network = process.env.PRODUCTION_DOCKER_NETWORK ?? 'apothiki-smoke-net'
      const image = process.env.PRODUCTION_DOCKER_IMAGE ?? 'apothiki:ci'
      if (!restart || !connection || !keys) throw new Error('Redeploy test requires a container, disposable database and key bind path.')
      execFileSync('docker', ['rm', '-f', restart], { timeout: 30000 })
      execFileSync('docker', ['run', '-d', '--name', restart, '--user', '0', '--network', network, '--network-alias', 'apothiki-smoke',
        '-p', '8090:8090', '--add-host=host.docker.internal:host-gateway',
        '-e', 'PORT=8090', '-e', 'Deployment__RailwayEdgeMode=true',
        '-e', 'Deployment__PublicOrigin=https://localhost:8443',
        '-e', `ConnectionStrings__ChatDatabase=${connection}`,
        '-e', 'SharedDataProtection__KeyRingPath=/keys',
        '-e', 'SharedDataProtection__ApplicationName=apothiki-v1',
        '-v', `${keys}:/keys`, image], { timeout: 30000 })
      await expect.poll(async () => (await page.request.get('/health/ready')).status(), { timeout: 30000 }).toBe(200)
      await page.reload(); await bobPage.reload()
      expect((await page.request.get('/api/auth/me')).status()).toBe(200)
      expect((await bobPage.request.get('/api/auth/me')).status()).toBe(200)
      await expect(bobPage.getByText(body, { exact: true })).toBeVisible()
      await recoverConversationScreen(page, conversationId, testInfo)
    }
    await bobPage.getByRole('region', { name: 'Μηνύματα συνομιλίας' }).getByRole('button', { name: 'Σήμανση ιστορικού ως αναγνωσμένου' }).click()
    await expect(page.getByLabel('Κατάσταση μηνύματος')).toHaveText('Read')
    const readState = await bobPage.request.get(`/api/conversations/${conversationId}/read-state`)
    expect(readState.status()).toBe(200)
    expect((await readState.json()).lastReadMessageId).toBeTruthy()
    const savedAfterRestart = await bobPage.request.get(`/api/conversations/${conversationId}/messages`)
    expect(JSON.stringify(await savedAfterRestart.json())).toContain(body)
    await page.getByRole('button', { name: 'Αποσύνδεση', exact: true }).click()
    await expect(page.getByRole('heading', { name: 'Σύνδεση', exact: true })).toBeVisible()
    await expect.poll(async () => (await page.request.get('/api/auth/me')).status()).toBe(401)
  } finally { await bobContext.close() }
})
