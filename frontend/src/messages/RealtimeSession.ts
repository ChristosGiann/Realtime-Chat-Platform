import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { ApiError, baseUrl, request } from '../api/client'
import type { CurrentUser } from '../api/client'
import type { Message } from '../api/messages'

export type LiveStatus = 'connecting' | 'connected' | 'disconnected' | 'reconnecting'
export type ConversationCallbacks = {
  message: (message: Message) => void
  status: (status: LiveStatus) => void
  validate: (signal: AbortSignal) => Promise<void>
  rejected: (cause: unknown) => void
  subscribed: (signal: AbortSignal) => Promise<void>
}
type Consumer = { callbacks: ConversationCallbacks; controller: AbortController; ready: number }
type Subscription = {
  id: string; consumers: Set<Consumer>; joined: number; receiving: number
  syncing: boolean; timer?: ReturnType<typeof setTimeout>; attempts: number
}
const delay = (attempt: number) => [1000, 2000, 5000, 15000, 30000][Math.min(attempt, 4)]

// Owned by one authenticated React session, not by a conversation or a module global.
export class RealtimeSession {
  private readonly connection = new HubConnectionBuilder().withUrl(`${baseUrl}/hubs/chat`, { withCredentials: true })
    .configureLogging(LogLevel.None).build()
  private readonly subscriptions = new Map<string, Subscription>()
  private running = false
  private lifecycle = 0
  private transport = 0
  private starting = false
  private attempts = 0
  private timer?: ReturnType<typeof setTimeout>
  private auth = new AbortController()
  private stopping: Promise<void> = Promise.resolve()
  private operations: Promise<void> = Promise.resolve()

  private readonly userId: string

  constructor(userId: string) {
    this.userId = userId
    this.connection.on('MessageCreated', (message: Message) => {
      const entry = this.subscriptions.get(message?.conversationId)
      if (!this.running || !entry || entry.receiving !== this.transport || typeof message.id !== 'string'
        || typeof message.clientMessageId !== 'string' || typeof message.senderId !== 'string'
        || typeof message.body !== 'string' || typeof message.createdAt !== 'string') return
      for (const consumer of entry.consumers) consumer.callbacks.message(message)
    })
    this.connection.onclose(() => {
      this.invalidateTransport()
      if (this.running && !this.starting) this.retryConnection()
    })
  }
  // Restartable for StrictMode effect replay; a previous stop completes before the next start.
  start() {
    if (this.running) return
    this.running = true; this.lifecycle++; this.auth = new AbortController()
    window.addEventListener('online', this.online); window.addEventListener('offline', this.offline)
    void this.connect()
  }
  stop() {
    this.running = false; this.lifecycle++; this.auth.abort()
    if (this.timer) clearTimeout(this.timer)
    this.timer = undefined
    window.removeEventListener('online', this.online); window.removeEventListener('offline', this.offline)
    this.invalidateTransport(); this.subscriptions.clear()
    this.stopping = this.connection.stop()
    return this.stopping
  }
  subscribe(id: string, callbacks: ConversationCallbacks) {
    let entry = this.subscriptions.get(id)
    if (!entry) {
      entry = { id, consumers: new Set(), joined: 0, receiving: 0, syncing: false, attempts: 0 }
      this.subscriptions.set(id, entry)
    }
    const consumer: Consumer = { callbacks, controller: new AbortController(), ready: 0 }
    entry.consumers.add(consumer)
    callbacks.status(this.attempts ? 'reconnecting' : 'connecting')
    void this.synchronize(entry)
    return () => this.remove(entry, consumer)
  }
  private current(entry: Subscription, epoch: number) {
    return this.running && this.transport === epoch && this.subscriptions.get(entry.id) === entry
      && this.connection.state === HubConnectionState.Connected
  }
  private enqueue(operation: () => Promise<void>) {
    const pending = this.operations.then(operation)
    this.operations = pending.catch(() => {})
    return pending
  }
  private remove(entry: Subscription, consumer: Consumer) {
    consumer.controller.abort(); entry.consumers.delete(consumer)
    if (entry.consumers.size || this.subscriptions.get(entry.id) !== entry) return
    this.subscriptions.delete(entry.id)
    if (entry.timer) clearTimeout(entry.timer)
    const epoch = this.transport
    // Runs after pending Subscribe and before a new subscription to the same group.
    void this.enqueue(async () => {
      if (this.running && this.transport === epoch && entry.joined === epoch
        && this.connection.state === HubConnectionState.Connected) {
        try { await this.connection.invoke('Unsubscribe', entry.id) }
        catch { await this.connection.stop() }
      }
    })
  }
  private invalidateTransport() {
    this.transport++
    for (const entry of this.subscriptions.values()) {
      entry.joined = 0; entry.receiving = 0
      if (entry.timer) clearTimeout(entry.timer)
      entry.timer = undefined
      for (const consumer of entry.consumers) {
        consumer.controller.abort(); consumer.controller = new AbortController(); consumer.ready = 0
        consumer.callbacks.status(this.running ? 'reconnecting' : 'disconnected')
      }
    }
  }
  private retryConnection() {
    if (!this.running || this.timer) return
    for (const entry of this.subscriptions.values()) for (const consumer of entry.consumers) consumer.callbacks.status('reconnecting')
    this.timer = setTimeout(() => { this.timer = undefined; void this.connect() }, delay(this.attempts++))
  }
  private async connect() {
    if (!this.running || this.starting) return
    this.starting = true
    const generation = this.lifecycle
    const signal = this.auth.signal
    let success = false
    try {
      await this.stopping
      if (!this.running || generation !== this.lifecycle) return
      const user = await request<CurrentUser>('/api/auth/me', { signal })
      if (!this.running || generation !== this.lifecycle) return
      if (user.id !== this.userId) throw new ApiError(401)
      await this.connection.start()
      if (!this.running || generation !== this.lifecycle) { await this.connection.stop(); return }
      this.transport++; this.attempts = 0; success = true
      for (const entry of this.subscriptions.values()) void this.synchronize(entry)
    } catch (cause) {
      if (this.running && generation === this.lifecycle && cause instanceof ApiError && cause.status === 401) this.expire()
    } finally {
      this.starting = false
      if (this.running && generation !== this.lifecycle) void this.connect()
      else if (!success) this.retryConnection()
    }
  }
  private expire() {
    void this.stop()
    window.dispatchEvent(new Event('session-expired'))
  }
  private async synchronize(entry: Subscription) {
    const epoch = this.transport
    if (!this.current(entry, epoch) || entry.syncing || entry.timer) return
    entry.syncing = true
    let retry = false
    try {
      for (const consumer of [...entry.consumers]) {
        if (!this.current(entry, epoch) || !entry.consumers.has(consumer) || consumer.ready === epoch) continue
        const signal = consumer.controller.signal
        try {
          await consumer.callbacks.validate(signal)
          if (!this.current(entry, epoch) || signal.aborted) continue
          await this.enqueue(async () => {
            if (!this.current(entry, epoch) || signal.aborted || entry.joined === epoch) return
            entry.receiving = epoch
            await this.connection.invoke('Subscribe', entry.id)
            entry.joined = epoch
          })
          if (!this.current(entry, epoch) || signal.aborted) continue
          await consumer.callbacks.subscribed(signal)
          if (this.current(entry, epoch) && !signal.aborted) {
            consumer.ready = epoch; entry.attempts = 0; consumer.callbacks.status('connected')
          }
        } catch (cause) {
          if (!this.current(entry, epoch) || signal.aborted) continue
          if (cause instanceof ApiError && cause.status === 401) { this.expire(); return }
          if (cause instanceof ApiError && [403, 404].includes(cause.status)) {
            consumer.callbacks.rejected(cause); consumer.callbacks.status('disconnected'); this.remove(entry, consumer)
          } else { consumer.callbacks.status('reconnecting'); retry = true }
        }
      }
    } finally {
      entry.syncing = false
      if (this.current(entry, this.transport) && [...entry.consumers].some(consumer => consumer.ready !== this.transport)) {
        if (retry && epoch === this.transport) {
          entry.timer = setTimeout(() => { entry.timer = undefined; void this.synchronize(entry) }, delay(entry.attempts++))
        } else void this.synchronize(entry)
      }
    }
  }
  private online = () => {
    if (this.timer) clearTimeout(this.timer)
    this.timer = undefined
    if (this.connection.state === HubConnectionState.Disconnected) void this.connect()
  }
  private offline = () => { void this.connection.stop() }
}
