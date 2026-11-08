import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { ApiError, baseUrl } from '../api/client'
import type { Message } from '../api/messages'

export type LiveStatus = 'connecting' | 'connected' | 'disconnected' | 'reconnecting'
export function liveConversation(id: string, callbacks: {
  message: (message: Message) => void
  status: (status: LiveStatus) => void
  validate: () => Promise<void>
  rejected: (cause: unknown) => void
  subscribed: () => Promise<void>
}) {
  let disposed = false
  let starting = false
  let attempts = 0
  let timer: ReturnType<typeof setTimeout> | undefined
  const connection = new HubConnectionBuilder().withUrl(`${baseUrl}/hubs/chat`, { withCredentials: true })
    .configureLogging(LogLevel.None).build()
  connection.on('MessageCreated', (message: Message) => {
    if (!disposed && message?.conversationId === id && typeof message.id === 'string'
      && typeof message.clientMessageId === 'string' && typeof message.senderId === 'string'
      && typeof message.body === 'string' && typeof message.createdAt === 'string') callbacks.message(message)
  })
  function retry() {
    if (disposed || timer) return
    callbacks.status('reconnecting')
    timer = setTimeout(() => { timer = undefined; void start() }, [1000, 2000, 5000, 15000, 30000][Math.min(attempts++, 4)])
  }
  connection.onclose(() => { if (!starting) retry() })
  async function start() {
    if (disposed || starting) return
    starting = true
    let succeeded = false
    try {
      await callbacks.validate()
      if (disposed) return
      await connection.start()
      if (disposed) { await connection.stop(); return }
      await connection.invoke('Subscribe', id)
      if (disposed) { await connection.stop(); return }
      await callbacks.subscribed()
      if (connection.state !== HubConnectionState.Connected) throw new Error('Connection lost during synchronization.')
      if (!disposed) { attempts = 0; succeeded = true; callbacks.status('connected') }
    } catch (cause) {
      await connection.stop()
      if (!disposed && cause instanceof ApiError && [401, 403, 404].includes(cause.status)) {
        callbacks.rejected(cause); disposed = true; callbacks.status('disconnected')
      }
    } finally { starting = false; if (!succeeded) retry() }
  }
  const online = () => { if (timer) clearTimeout(timer); timer = undefined; if (connection.state === HubConnectionState.Disconnected) void start() }
  const offline = () => { void connection.stop() }
  window.addEventListener('online', online); window.addEventListener('offline', offline)
  void start()
  return () => {
    disposed = true; connection.off('MessageCreated')
    if (timer) clearTimeout(timer)
    window.removeEventListener('online', online); window.removeEventListener('offline', offline)
    if (connection.state === HubConnectionState.Connected)
      void connection.invoke('Unsubscribe', id).catch(() => {})
    void connection.stop()
  }
}
