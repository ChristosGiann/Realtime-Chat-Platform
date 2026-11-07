import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { baseUrl } from '../api/client'
import type { Message } from '../api/messages'

export type LiveStatus = 'connecting' | 'connected' | 'disconnected' | 'reconnecting'
export function liveConversation(id: string, callbacks: {
  message: (message: Message) => void
  status: (status: LiveStatus) => void
  subscribed: () => Promise<void>
}) {
  let disposed = false
  const connection = new HubConnectionBuilder().withUrl(`${baseUrl}/hubs/chat`, { withCredentials: true })
    .configureLogging(LogLevel.None).build()
  connection.on('MessageCreated', (message: Message) => {
    if (!disposed && message?.conversationId === id && typeof message.id === 'string'
      && typeof message.clientMessageId === 'string' && typeof message.senderId === 'string'
      && typeof message.body === 'string' && typeof message.createdAt === 'string') callbacks.message(message)
  })
  connection.onclose(() => { if (!disposed) callbacks.status('disconnected') })
  void (async () => {
    try {
      await connection.start()
      if (disposed) { await connection.stop(); return }
      await connection.invoke('Subscribe', id)
      if (disposed) { await connection.stop(); return }
      await callbacks.subscribed()
      if (!disposed) callbacks.status('connected')
    } catch { if (!disposed) callbacks.status('disconnected'); await connection.stop() }
  })()
  return () => {
    disposed = true; connection.off('MessageCreated')
    if (connection.state === HubConnectionState.Connected)
      void connection.invoke('Unsubscribe', id).catch(() => {}).finally(() => connection.stop())
    else void connection.stop()
  }
}
