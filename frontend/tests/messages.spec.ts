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

async function liveSocket(page: Page, failInitial = false, deferSubscribe = false) {
  let socket: WebSocketRoute
  let attempts = 0
  let connections = 0
  let closed = 0
  const invocations: string[] = []
  const typingCalls: boolean[] = []
  let releaseSubscribe = () => {}
  await page.route('**/hubs/chat/negotiate?*', route => {
    if (failInitial && attempts++ === 0) return route.fulfill({ status: 503, json: {} })
    return route.fulfill({ json: {
    negotiateVersion: 1, connectionId: 'test-connection', connectionToken: 'test-token',
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text'] }],
    } })
  })
  await page.routeWebSocket('**/hubs/chat?*', ws => {
    socket = ws
    connections++
    ws.onClose(() => { closed++ })
    ws.onMessage(data => {
      for (const raw of data.toString().split('\u001e').filter(Boolean)) {
        const frame = JSON.parse(raw)
        if (frame.protocol) ws.send('{}\u001e')
        else if (frame.type === 1) {
          if (frame.target === 'SetTyping') typingCalls.push(frame.arguments[1])
          if (['Subscribe', 'Unsubscribe'].includes(frame.target)) invocations.push(`${frame.target}:${frame.arguments[0]}`)
          const complete = () => ws.send(JSON.stringify({ type: 3, invocationId: frame.invocationId,
            ...(frame.target === 'GetTyping' ? { result: { conversationId: frame.arguments[0], items: [], version: 0 } } : {}),
          }) + '\u001e')
          if (deferSubscribe && frame.target === 'Subscribe') { deferSubscribe = false; releaseSubscribe = complete }
          else complete()
        }
      }
    })
  })
  return {
    connections: () => connections,
    closed: () => closed,
    invocations,
    typingCalls,
    typing: (value: unknown) => socket.send(JSON.stringify({ type: 1, target: 'TypingChanged', arguments: [value] }) + '\u001e'),
    releaseSubscribe: () => releaseSubscribe(),
    emit: (value: unknown) => socket.send(JSON.stringify({ type: 1, target: 'MessageCreated', arguments: [value] }) + '\u001e'),
    close: () => socket.close(),
  }
}

test('typing throttles keystrokes and stops on idle, clear and blur', async ({ page }) => {
  await setup(page); const socket = await liveSocket(page)
  await page.goto(`/#/conversations/${group}`)
  await expect(page.getByLabel('Κατάσταση live σύνδεσης')).toHaveText('Ζωντανή σύνδεση')
  await page.clock.install()
  const composer = page.getByLabel('Νέο μήνυμα', { exact: true })
  await composer.pressSequentially('typing many keystrokes')
  await expect.poll(() => socket.typingCalls).toEqual([true])
  await page.clock.fastForward(2100)
  await expect.poll(() => socket.typingCalls).toEqual([true, false])
  await composer.fill('more activity'); await composer.fill('')
  await expect.poll(() => socket.typingCalls).toEqual([true, false, true, false])
  await composer.fill('blur activity'); await composer.blur()
  await expect.poll(() => socket.typingCalls).toEqual([true, false, true, false, true, false])
})

test('typing hides self and other conversations, expires lost stops and rejects old events', async ({ page }) => {
  await setup(page); const socket = await liveSocket(page)
  await page.goto(`/#/conversations/${group}`)
  await expect(page.getByLabel('Κατάσταση live σύνδεσης')).toHaveText('Ζωντανή σύνδεση')
  await page.clock.install()
  const state = { conversationId: group, userId: owner, expiresInMilliseconds: 5000, version: 1 }
  socket.typing(state); socket.typing({ ...state, conversationId: 'other', userId: 'bob' })
  await expect(page.getByLabel('Πληκτρολόγηση', { exact: true })).toHaveCount(0)
  socket.typing({ ...state, userId: 'bob' })
  await expect(page.getByLabel('Πληκτρολόγηση', { exact: true })).toContainText('Ένας χρήστης')
  await page.clock.fastForward(5100)
  await expect(page.getByLabel('Πληκτρολόγηση', { exact: true })).toHaveCount(0)
  socket.typing({ ...state, userId: 'bob' })
  await expect(page.getByLabel('Πληκτρολόγηση', { exact: true })).toHaveCount(0)
  socket.typing({ ...state, userId: 'bob', version: 2 })
  await expect(page.getByLabel('Πληκτρολόγηση', { exact: true })).toBeVisible()
  await page.getByLabel('Νέο μήνυμα', { exact: true }).fill('Draft before reconnect')
  await expect.poll(() => socket.typingCalls).toEqual([true])
  socket.close()
  await expect(page.getByLabel('Πληκτρολόγηση', { exact: true })).toHaveCount(0)
  await expect(page.getByLabel('Κατάσταση live σύνδεσης')).toContainText('Επανασύνδεση')
  await page.clock.fastForward(1200)
  await expect(page.getByLabel('Κατάσταση live σύνδεσης')).toHaveText('Ζωντανή σύνδεση')
  expect(socket.typingCalls).toEqual([true])
})

test('shared connection survives conversation navigation and cleans groups without duplicate delivery', async ({ page }) => {
  await setup(page); const socket = await liveSocket(page)
  const second = '55555555-5555-5555-5555-555555555555'
  await page.route(`**/api/conversations/${second}?*`, route => route.fulfill({ json: { id: second, type: 'Group', name: 'Second', myRole: 'Owner', members: [], nextOffset: null } }))
  await page.route(`**/api/conversations/${second}/messages?*`, route => route.fulfill({ json: { items: [], nextCursor: null } }))
  await page.route(`**/api/conversations/${second}/read-state`, route => route.fulfill({ json: { lastReadMessageId: null, lastReadAt: null, unreadCount: 0 } }))
  await page.goto('/#/app')
  await expect.poll(socket.connections).toBe(1)
  for (const id of [group, second, group]) {
    await page.evaluate(id => { location.hash = `/conversations/${id}` }, id)
    await expect(page.getByLabel('Κατάσταση live σύνδεσης')).toHaveText('Ζωντανή σύνδεση')
  }
  expect(socket.invocations).toEqual([`Subscribe:${group}`, `Unsubscribe:${group}`, `Subscribe:${second}`, `Unsubscribe:${second}`, `Subscribe:${group}`])
  socket.emit({ ...older, conversationId: second }); socket.emit(latest); socket.emit(latest)
  await expect(panel(page).locator('[data-message-id]')).toHaveCount(1)
  await page.getByRole('link', { name: 'Realtime Chat' }).click()
  await expect.poll(() => socket.invocations.at(-1)).toBe(`Unsubscribe:${group}`)
  expect(socket.connections()).toBe(1); expect(socket.closed()).toBe(0)
})

test('logout at home stops shared transport and cancels reconnect retries', async ({ page }) => {
  await setup(page); const socket = await liveSocket(page)
  await page.route('**/api/auth/logout', route => route.fulfill({ status: 204 }))
  await page.goto('/#/app'); await expect.poll(socket.connections).toBe(1)
  await page.getByRole('button', { name: 'Αποσύνδεση', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Σύνδεση', exact: true })).toBeVisible()
  await expect.poll(socket.closed).toBe(1)
  await page.clock.install(); await page.clock.fastForward(60000)
  expect(socket.connections()).toBe(1)
  await page.route('**/api/auth/login', route => route.fulfill({ status: 204 }))
  await page.getByLabel('Email', { exact: true }).fill('alice@example.test')
  await page.getByLabel('Κωδικός', { exact: true }).fill('Test-password123!')
  await page.getByRole('button', { name: 'Σύνδεση', exact: true }).click()
  await expect.poll(socket.connections).toBe(2)
  expect(socket.closed()).toBe(1)
})

test('late Subscribe completion after navigation is undone before the next subscription', async ({ page }) => {
  await setup(page); const socket = await liveSocket(page, false, true)
  await page.goto(`/#/conversations/${group}`)
  await expect.poll(() => socket.invocations).toEqual([`Subscribe:${group}`])
  await page.getByRole('link', { name: 'Realtime Chat' }).click()
  await expect(page.getByRole('heading', { name: /Καλώς ήρθες/ })).toBeVisible()
  socket.releaseSubscribe()
  await expect.poll(() => socket.invocations).toEqual([`Subscribe:${group}`, `Unsubscribe:${group}`])
  await page.evaluate(id => { location.hash = `/conversations/${id}` }, group)
  await expect(page.getByLabel('Κατάσταση live σύνδεσης')).toHaveText('Ζωντανή σύνδεση')
  socket.emit(latest)
  await expect(panel(page).locator('[data-message-id]')).toHaveCount(1)
  expect(socket.connections()).toBe(1)
})

test('session loss at home is detected before reconnect without conversation requests', async ({ page }) => {
  await setup(page); const socket = await liveSocket(page)
  await page.goto('/#/app'); await expect.poll(socket.connections).toBe(1)
  await page.route('**/api/auth/me', route => route.fulfill({ status: 401, json: {} }))
  socket.close()
  await expect(page.getByRole('heading', { name: 'Σύνδεση', exact: true })).toBeVisible()
  expect(socket.connections()).toBe(1)
})

test('conversation access rejection leaves the authenticated transport reusable', async ({ page }) => {
  await setup(page); const socket = await liveSocket(page)
  await page.goto(`/#/conversations/${group}`)
  await expect(page.getByLabel('Κατάσταση live σύνδεσης')).toHaveText('Ζωντανή σύνδεση')
  await page.route(`**/api/conversations/${group}/read-state`, route => route.fulfill({ status: 404, json: {} }))
  socket.close()
  await expect(panel(page).getByRole('alert')).toContainText('Δεν έχεις πλέον πρόσβαση')
  await page.getByRole('link', { name: 'Realtime Chat' }).click()
  await page.route(`**/api/conversations/${group}/read-state`, route => route.fulfill({ json: { lastReadMessageId: null, lastReadAt: null, unreadCount: 0 } }))
  await page.evaluate(id => { location.hash = `/conversations/${id}` }, group)
  await expect(page.getByLabel('Κατάσταση live σύνδεσης')).toHaveText('Ζωντανή σύνδεση')
  expect(socket.connections()).toBe(2)
})

test('initial SignalR failure retries without discarding durable history', async ({ page }) => {
  await setup(page); await liveSocket(page, true)
  await page.route(`**/api/conversations/${group}/messages?*`, route => route.fulfill({ json: { items: [latest], nextCursor: null } }))
  await page.goto(`/#/conversations/${group}`)
  await expect(page.getByLabel('Κατάσταση live σύνδεσης')).toContainText('Επανασύνδεση')
  await expect(panel(page).locator('[data-message-id]')).toHaveCount(1)
  await expect(page.getByLabel('Κατάσταση live σύνδεσης')).toHaveText('Ζωντανή σύνδεση')
  await expect(panel(page).locator('[data-message-id]')).toHaveCount(1)
})

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
  await expect(page.getByLabel('Κατάσταση live σύνδεσης')).toContainText('Επανασύνδεση')
  await expect(panel(page).locator('[data-message-id]')).toHaveCount(2)
})

test('reconnect recovers more than one history page and deduplicates existing events', async ({ page }) => {
  await setup(page); const socket = await liveSocket(page)
  let disconnected = false
  const missed = Array.from({ length: 35 }, (_, i) => message(`aaaaaaaa-aaaa-aaaa-aaaa-${String(i).padStart(12, '0')}`, `Missed ${i}`, `2026-02-01T00:00:${String(i).padStart(2, '0')}.000000Z`)).reverse()
  let pages = 0
  await page.route(`**/api/conversations/${group}/messages?*`, route => {
    if (!disconnected) return route.fulfill({ json: { items: [latest], nextCursor: null } })
    pages++
    return route.fulfill({ json: new URL(route.request().url()).searchParams.has('cursor')
      ? { items: [...missed.slice(30), latest], nextCursor: null } : { items: missed.slice(0, 30), nextCursor: 'recovery-page-2' } })
  })
  await page.goto(`/#/conversations/${group}`)
  await expect(page.getByLabel('Κατάσταση live σύνδεσης')).toHaveText('Ζωντανή σύνδεση')
  socket.emit(latest); disconnected = true; socket.close()
  await expect(page.getByLabel('Κατάσταση live σύνδεσης')).toContainText('Επανασύνδεση')
  await expect(page.getByLabel('Κατάσταση live σύνδεσης')).toHaveText('Ζωντανή σύνδεση')
  await expect(panel(page).locator('[data-message-id]')).toHaveCount(36)
  expect(pages).toBeGreaterThanOrEqual(2)
  socket.emit(missed[0])
  await expect(panel(page).locator('[data-message-id]')).toHaveCount(36)
})

for (const status of [401, 404]) test(`reconnect handles access status ${status} without resubscribing`, async ({ page }) => {
  await setup(page); const socket = await liveSocket(page)
  await page.goto(`/#/conversations/${group}`)
  await expect(page.getByLabel('Κατάσταση live σύνδεσης')).toHaveText('Ζωντανή σύνδεση')
  await page.route(`**/api/conversations/${group}/read-state`, route => route.fulfill({ status, json: {} }))
  socket.close()
  if (status === 401) await expect(page.getByRole('heading', { name: 'Σύνδεση', exact: true })).toBeVisible()
  else await expect(panel(page).getByRole('alert')).toContainText('Δεν έχεις πλέον πρόσβαση')
  await expect(page.getByLabel('Νέο μήνυμα', { exact: true })).toHaveCount(0)
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
