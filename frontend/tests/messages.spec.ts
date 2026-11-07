import { expect, test } from '@playwright/test'
import type { Page, WebSocketRoute } from '@playwright/test'

const owner = '11111111-1111-1111-1111-111111111111'
const group = '44444444-4444-4444-4444-444444444444'
const message = (id: string, body: string, createdAt = '2026-01-01T00:00:00.123456Z') => ({ id, clientMessageId: id, conversationId: group, senderId: owner, body, createdAt, sender: { userId: owner, displayName: '<b>Alice</b>', avatarUrl: null } })
const latest = message('99999999-1111-1111-1111-111111111111', '<script>hello</script>')
const older = message('11111111-2222-2222-2222-222222222222', 'Older text', '2026-01-01T00:00:00.123455Z')
async function setup(page: Page) {
  await page.route('**/api/auth/me', route => route.fulfill({ json: { id: owner, userName: 'alice', email: 'alice@example.test', displayName: 'Alice' } }))
  await page.route('**/api/auth/csrf', route => route.fulfill({ json: { requestToken: 'message-token' } }))
  const summary = { id: group, type: 'Group', name: 'Messaging group', createdAt: latest.createdAt, myRole: 'Owner', memberCount: 1 }
  await page.route('**/api/conversations?*', route => route.fulfill({ json: { items: [summary], nextOffset: null } }))
  await page.route(`**/api/conversations/${group}?*`, route => route.fulfill({ json: { ...summary, members: [], nextOffset: null } }))
  await page.route(`**/api/conversations/${group}/messages?*`, route => route.fulfill({ json: { items: [], nextCursor: null } }))
  await page.route(`**/api/conversations/${group}/read-state`, route => route.fulfill({ json: { lastReadMessageId: null, lastReadAt: null, unreadCount: 2 } }))
}
const panel = (page: Page) => page.getByRole('region', { name: 'Μηνύματα συνομιλίας' })

async function liveSocket(page: Page) {
  let socket: WebSocketRoute
  await page.route('**/hubs/chat/negotiate?*', route => route.fulfill({ json: {
    negotiateVersion: 1, connectionId: 'test-connection', connectionToken: 'test-token',
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text'] }],
  } }))
  await page.routeWebSocket('**/hubs/chat?*', ws => {
    socket = ws
    ws.onMessage(data => {
      for (const raw of data.toString().split('\u001e').filter(Boolean)) {
        const frame = JSON.parse(raw)
        if (frame.protocol) ws.send('{}\u001e')
        else if (frame.type === 1) ws.send(JSON.stringify({ type: 3, invocationId: frame.invocationId }) + '\u001e')
      }
    })
  })
  return {
    emit: (value: unknown) => socket.send(JSON.stringify({ type: 1, target: 'MessageCreated', arguments: [value] }) + '\u001e'),
    close: () => socket.close(),
  }
}

test('live events reconcile with REST response and reject other conversations', async ({ page }) => {
  await setup(page); const socket = await liveSocket(page)
  await page.route(`**/api/conversations/${group}/messages`, async route => {
    const draft = route.request().postDataJSON()
    const saved = { ...latest, ...draft }
    socket.emit(saved); socket.emit(saved)
    await route.fulfill({ status: 201, json: saved })
  })
  await page.goto(`/#/conversations/${group}`)
  await expect(page.getByLabel('Κατάσταση live σύνδεσης')).toHaveText('Ζωντανή σύνδεση')
  await page.getByLabel('Νέο μήνυμα', { exact: true }).fill('Live sender')
  await panel(page).getByRole('button', { name: 'Αποστολή', exact: true }).click()
  await expect(page.getByLabel('Νέο μήνυμα', { exact: true })).toHaveValue('')
  await expect(panel(page).locator('[data-message-id]')).toHaveCount(1)
  socket.emit({ ...older, conversationId: 'another-conversation' })
  socket.emit({ ...older, body: '<img src=x onerror=alert(1)>' })
  await expect(panel(page).locator('[data-message-id]')).toHaveCount(2)
  await expect(panel(page).getByText('<img src=x onerror=alert(1)>', { exact: true })).toBeVisible()
  await expect(panel(page).locator('img')).toHaveCount(0)
  socket.close()
  await expect(page.getByLabel('Κατάσταση live σύνδεσης')).toContainText('Χωρίς ζωντανή σύνδεση')
  await expect(panel(page).locator('[data-message-id]')).toHaveCount(2)
})

test('history is safe text, older pages deduplicate and explicit read uses loaded anchor', async ({ page }, testInfo) => {
  await setup(page)
  await page.route(`**/api/conversations/${group}/messages?*`, route => route.fulfill({ json: new URL(route.request().url()).searchParams.has('cursor')
    ? { items: [latest, older], nextCursor: null } : { items: [latest], nextCursor: 'opaque+/=' } }))
  await page.route(`**/api/conversations/${group}/read-state`, route => {
    if (route.request().method() === 'PUT') {
      expect(route.request().postDataJSON()).toEqual({ messageId: latest.id })
      expect(route.request().headers()['x-csrf-token']).toBe('message-token')
      return route.fulfill({ json: { lastReadMessageId: latest.id, lastReadAt: latest.createdAt, unreadCount: 0 } })
    }
    return route.fulfill({ json: { lastReadMessageId: null, lastReadAt: null, unreadCount: 2 } })
  })
  await page.goto(`/#/conversations/${group}`)
  await expect(panel(page).getByText('<script>hello</script>', { exact: true })).toBeVisible()
  await expect(panel(page).locator('script')).toHaveCount(0)
  await expect(panel(page).getByText('Μη αναγνωσμένα: 2')).toBeVisible()
  await panel(page).getByRole('button', { name: 'Παλαιότερα μηνύματα' }).click()
  await expect(panel(page).locator('[data-message-id]')).toHaveCount(2)
  await expect(panel(page).locator('[data-message-id]').first()).toHaveAttribute('data-message-id', older.id)
  await panel(page).getByRole('button', { name: 'Σήμανση ιστορικού ως αναγνωσμένου' }).click()
  await expect(panel(page).getByText('Μη αναγνωσμένα: 0')).toBeVisible()
  await page.screenshot({ path: testInfo.outputPath('messaging-desktop.png'), fullPage: true })
  await page.setViewportSize({ width: 390, height: 844 })
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
})

test('lost response retries identical key and body, and refresh never duplicates persisted send', async ({ page }) => {
  await setup(page)
  let draft: { clientMessageId: string; body: string } | undefined
  let requests = 0
  await page.route(`**/api/conversations/${group}/messages`, async route => {
    const submitted = route.request().postDataJSON() as { clientMessageId: string; body: string }
    expect(route.request().headers()['x-csrf-token']).toBe('message-token')
    requests++
    if (!draft) { draft = submitted; await route.abort('failed'); return }
    expect(submitted).toEqual(draft)
    await route.fulfill({ status: 200, json: { ...latest, clientMessageId: draft.clientMessageId, body: draft.body } })
  })
  await page.goto(`/#/conversations/${group}`)
  await expect(panel(page).getByText('Δεν υπάρχουν ακόμη μηνύματα.')).toBeVisible()
  await page.getByLabel('Νέο μήνυμα', { exact: true }).fill('  retry body  ')
  await panel(page).getByRole('button', { name: 'Αποστολή', exact: true }).click()
  await expect(page.getByLabel('Νέο μήνυμα', { exact: true })).toBeDisabled()
  await panel(page).getByRole('button', { name: 'Επανάληψη αποστολής' }).click()
  await expect(panel(page).locator('[data-message-id]')).toHaveCount(1)
  await expect(page.getByLabel('Νέο μήνυμα', { exact: true })).toHaveValue('')
  expect(requests).toBe(2)
  expect(draft!.clientMessageId).toMatch(/^[0-9a-f-]{36}$/)
  await page.route(`**/api/conversations/${group}/messages?*`, route => route.fulfill({ json: { items: [{ ...latest, clientMessageId: draft!.clientMessageId, body: draft!.body }], nextCursor: null } }))
  await panel(page).getByRole('button', { name: 'Ανανέωση μηνυμάτων' }).click()
  await expect(panel(page).locator('[data-message-id]')).toHaveCount(1)
})

test('loading, history failure, validation and access revocation are visible', async ({ page }) => {
  await setup(page)
  let release!: () => void
  const gate = new Promise<void>(resolve => { release = resolve })
  await page.route(`**/api/conversations/${group}/messages?*`, async route => { await gate; await route.fulfill({ status: 500, json: {} }) })
  await page.goto(`/#/conversations/${group}`)
  await expect(panel(page).getByText('Φόρτωση μηνυμάτων…')).toBeVisible(); release()
  await expect(panel(page).getByRole('alert')).toBeVisible()
  await page.route(`**/api/conversations/${group}/messages?*`, route => route.fulfill({ json: { items: [latest], nextCursor: null } }))
  await panel(page).getByRole('button', { name: 'Ανανέωση μηνυμάτων' }).click()
  await expect(panel(page).locator('[data-message-id]')).toHaveCount(1)
  await page.getByLabel('Νέο μήνυμα', { exact: true }).fill('   ')
  await panel(page).getByRole('button', { name: 'Αποστολή', exact: true }).click()
  await expect(panel(page).getByText(/Γράψε μήνυμα 1–4000/)).toBeVisible()
  await page.route(`**/api/conversations/${group}/messages?*`, route => route.fulfill({ status: 404, json: {} }))
  await panel(page).getByRole('button', { name: 'Ανανέωση μηνυμάτων' }).click()
  await expect(panel(page).locator('[data-message-id]')).toHaveCount(0)
  await expect(page.getByLabel('Νέο μήνυμα', { exact: true })).toHaveCount(0)
})

test('session expiry on history uses the existing authentication flow', async ({ page }) => {
  await setup(page)
  await page.route(`**/api/conversations/${group}/messages?*`, route => route.fulfill({ status: 401, json: {} }))
  await page.goto(`/#/conversations/${group}`)
  await expect(page.getByRole('heading', { name: 'Σύνδεση', exact: true })).toBeVisible()
  await expect(page.getByLabel('Νέο μήνυμα', { exact: true })).toHaveCount(0)
})
