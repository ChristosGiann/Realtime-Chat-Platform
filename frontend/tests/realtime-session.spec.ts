import { expect, test } from '@playwright/test'
import type { WebSocketRoute } from '@playwright/test'
import type { RealtimeSession } from '../src/messages/RealtimeSession'

declare global {
  interface Window {
    realtimeTest: { session: RealtimeSession; dispose: (() => void)[]; messages: number[]; status: string[] }
  }
}

test('shared consumers retain their group until the last disposer and isolate rejected subscriptions', async ({ page }) => {
  let socket: WebSocketRoute
  const calls: string[] = []
  let connections = 0
  // A minimal browser harness exercises the production service without creating another React provider.
  await page.route('**/harness', route => route.fulfill({ contentType: 'text/html', body: '<!doctype html><title>Lifecycle</title>' }))
  await page.route('**/api/auth/me', route => route.fulfill({ json: { id: 'owner' } }))
  await page.route('**/hubs/chat/negotiate?*', route => route.fulfill({ json: {
    negotiateVersion: 1, connectionId: 'shared', connectionToken: 'shared',
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text'] }],
  } }))
  await page.routeWebSocket('**/hubs/chat?*', ws => {
    socket = ws; connections++
    ws.onMessage(data => {
      for (const raw of data.toString().split('\u001e').filter(Boolean)) {
        const frame = JSON.parse(raw)
        if (frame.protocol) ws.send('{}\u001e')
        else if (frame.type === 1) {
          calls.push(`${frame.target}:${frame.arguments[0]}`)
          ws.send(JSON.stringify({ type: 3, invocationId: frame.invocationId }) + '\u001e')
        }
      }
    })
  })
  await page.goto('/harness')
  await page.evaluate(async () => {
    const servicePath = '/src/messages/RealtimeSession.ts', apiPath = '/src/api/client.ts'
    const { RealtimeSession } = await import(servicePath)
    const { ApiError } = await import(apiPath)
    const state = window.realtimeTest = { session: new RealtimeSession('owner'), dispose: [], messages: [0, 0, 0], status: ['', '', ''] }
    state.session.start()
    state.dispose = [0, 1, 2].map(index => state.session.subscribe(index === 2 ? 'forbidden' : 'shared', {
      message: () => { state.messages[index]++ }, status: value => { state.status[index] = value },
      validate: async () => { if (index === 2) throw new ApiError(404) },
      subscribed: async () => {}, rejected: () => {},
    }))
  })
  await expect.poll(() => page.evaluate(() => window.realtimeTest.status)).toEqual(['connected', 'connected', 'disconnected'])
  expect(calls).toEqual(['Subscribe:shared'])
  const emit = () => socket.send(JSON.stringify({ type: 1, target: 'MessageCreated', arguments: [{
    id: 'message', clientMessageId: 'client', conversationId: 'shared', senderId: 'owner', body: 'Hello', createdAt: '2026-01-01T00:00:00Z',
  }] }) + '\u001e')
  emit()
  await expect.poll(() => page.evaluate(() => window.realtimeTest.messages)).toEqual([1, 1, 0])
  await page.evaluate(() => window.realtimeTest.dispose[0]())
  emit()
  await expect.poll(() => page.evaluate(() => window.realtimeTest.messages)).toEqual([1, 2, 0])
  expect(calls).toEqual(['Subscribe:shared'])
  await page.evaluate(() => window.realtimeTest.dispose[1]())
  await expect.poll(() => calls).toEqual(['Subscribe:shared', 'Unsubscribe:shared'])
  expect(connections).toBe(1)
  await page.evaluate(() => window.realtimeTest.session.stop())
})
