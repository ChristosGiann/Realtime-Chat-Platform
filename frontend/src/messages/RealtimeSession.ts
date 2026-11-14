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
  typing?: (users: string[]) => void
  contextChanged?: (kind: 'receipt' | 'presence' | 'membership') => void
  revoked?: (membershipId: string) => void
}
export type PresenceMember = { userId: string; isOnline: boolean }
type PresencePage = { conversationId: string; items: PresenceMember[]; nextOffset: number | null; version: number }
type TypingEvent = { conversationId: string; userId: string; expiresInMilliseconds: number; version: number }
type TypingSnapshot = { conversationId: string; items: Omit<TypingEvent, 'conversationId' | 'version'>[]; version: number }
type Consumer = { callbacks: ConversationCallbacks; controller: AbortController; ready: number }
type Subscription = {
  id: string; consumers: Set<Consumer>; joined: number; receiving: number
  syncing: boolean; timer?: ReturnType<typeof setTimeout>; attempts: number
  typing: Map<string, { expires: number; version: number }>; typingExpiry?: ReturnType<typeof setTimeout>
  typingIdle?: ReturnType<typeof setTimeout>; typingSent: boolean; lastTypingSent: number
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
    for (const [event, kind] of [['ReceiptAdvanced', 'receipt'], ['PresenceChanged', 'presence'], ['ConversationMembershipChanged', 'membership']] as const) {
      this.connection.on(event, (value: { conversationId: string }) => {
        const entry = this.subscriptions.get(value?.conversationId)
        if (!entry || !this.current(entry, this.transport) || entry.receiving !== this.transport) return
        if (kind === 'membership') this.clearTyping(entry)
        for (const consumer of entry.consumers) consumer.callbacks.contextChanged?.(kind)
      })
    }
    this.connection.on('ConversationAccessRevoked', (value: { conversationId: string; membershipId: string }) => {
      const entry = this.subscriptions.get(value?.conversationId)
      if (entry && this.current(entry, this.transport) && typeof value.membershipId === 'string')
        for (const consumer of entry.consumers) consumer.callbacks.revoked?.(value.membershipId)
    })
    this.connection.on('MessageCreated', (message: Message) => {
      const entry = this.subscriptions.get(message?.conversationId)
      if (!this.running || !entry || entry.receiving !== this.transport || typeof message.id !== 'string'
        || typeof message.clientMessageId !== 'string' || typeof message.senderId !== 'string'
        || typeof message.body !== 'string' || typeof message.createdAt !== 'string') return
      for (const consumer of entry.consumers) consumer.callbacks.message(message)
    })
    this.connection.on('TypingChanged', (event: TypingEvent) => {
      const entry = this.subscriptions.get(event?.conversationId)
      if (entry && this.current(entry, this.transport) && entry.receiving === this.transport) this.applyTyping(entry, event)
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
      entry = { id, consumers: new Set(), joined: 0, receiving: 0, syncing: false, attempts: 0,
        typing: new Map(), typingSent: false, lastTypingSent: -Infinity }
      this.subscriptions.set(id, entry)
    }
    const consumer: Consumer = { callbacks, controller: new AbortController(), ready: 0 }
    entry.consumers.add(consumer)
    callbacks.status(this.attempts ? 'reconnecting' : 'connecting')
    this.notifyTyping(entry)
    void this.synchronize(entry)
    return () => this.remove(entry, consumer)
  }
  typing(id: string, active: boolean) {
    const entry = this.subscriptions.get(id)
    if (!entry) return
    if (entry.typingIdle) clearTimeout(entry.typingIdle)
    entry.typingIdle = undefined
    if (!this.current(entry, this.transport) || entry.joined !== this.transport) return
    if (active) {
      const now = performance.now()
      if (!entry.typingSent || now - entry.lastTypingSent >= 2000) {
        entry.typingSent = true; entry.lastTypingSent = now
        void this.connection.invoke('SetTyping', id, true).catch(() => {})
      }
      entry.typingIdle = setTimeout(() => this.typing(id, false), 2000)
    } else if (entry.typingSent) {
      entry.typingSent = false
      void this.connection.invoke('SetTyping', id, false).catch(() => {})
    }
  }
  async presence(id: string, signal: AbortSignal): Promise<PresenceMember[]> {
    const epoch = this.transport, entry = this.subscriptions.get(id)
    const items: PresenceMember[] = []
    let offset: number | null = 0
    do {
      if (!entry || !this.current(entry, epoch) || signal.aborted) throw new Error('Connection changed')
      const page: PresencePage = await this.connection.invoke('GetPresence', id, offset, 100)
      if (!this.current(entry, epoch) || signal.aborted) throw new Error('Connection changed')
      if (page?.conversationId !== id || !Array.isArray(page.items)) throw new Error('Invalid presence snapshot')
      items.push(...page.items)
      if (page.nextOffset !== null && page.nextOffset <= offset) throw new Error('Invalid presence cursor')
      offset = page.nextOffset
    } while (offset !== null)
    return items
  }
  private applyTyping(entry: Subscription, event: TypingEvent) {
    if (typeof event.userId !== 'string' || event.userId === this.userId || !Number.isSafeInteger(event.version)
      || !Number.isFinite(event.expiresInMilliseconds) || event.expiresInMilliseconds < 0 || event.expiresInMilliseconds > 5000) return
    if ((entry.typing.get(event.userId)?.version ?? -1) >= event.version) return
    entry.typing.set(event.userId, { expires: performance.now() + event.expiresInMilliseconds, version: event.version })
    this.notifyTyping(entry)
  }
  private notifyTyping(entry: Subscription) {
    if (entry.typingExpiry) clearTimeout(entry.typingExpiry)
    entry.typingExpiry = undefined
    const now = performance.now(), active = [...entry.typing.entries()].filter(([, value]) => value.expires > now)
    for (const consumer of entry.consumers) consumer.callbacks.typing?.(active.map(([user]) => user))
    if (active.length) entry.typingExpiry = setTimeout(() => this.notifyTyping(entry), Math.max(1, Math.min(...active.map(([, value]) => value.expires - now))))
  }
  private clearTyping(entry: Subscription) {
    if (entry.typingExpiry) clearTimeout(entry.typingExpiry)
    if (entry.typingIdle) clearTimeout(entry.typingIdle)
    entry.typingExpiry = undefined; entry.typingIdle = undefined
    entry.typing.clear(); entry.typingSent = false; entry.lastTypingSent = -Infinity
    for (const consumer of entry.consumers) consumer.callbacks.typing?.([])
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
    this.typing(entry.id, false); this.clearTyping(entry)
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
      this.clearTyping(entry)
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
          if (consumer.callbacks.typing) {
            const snapshot = await this.connection.invoke<TypingSnapshot>('GetTyping', entry.id)
            if (!this.current(entry, epoch) || signal.aborted) continue
            if (snapshot?.conversationId !== entry.id || !Array.isArray(snapshot.items) || !Number.isSafeInteger(snapshot.version))
              throw new Error('Invalid typing snapshot')
            for (const user of entry.typing.keys()) this.applyTyping(entry, { conversationId: entry.id, userId: user, expiresInMilliseconds: 0, version: snapshot.version })
            for (const item of snapshot.items) {
              // Apply the snapshot atomically at its watermark, retaining newer live updates.
              if ((entry.typing.get(item.userId)?.version ?? -1) <= snapshot.version) entry.typing.delete(item.userId)
              this.applyTyping(entry, { ...item, conversationId: entry.id, version: snapshot.version })
            }
          }
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
    // The online event can precede the first successful fetch. Start a fresh
    // short retry schedule instead of retaining the offline 30s backoff.
    this.attempts = 0
    if (this.connection.state === HubConnectionState.Disconnected) void this.connect()
  }
  private offline = () => { void this.connection.stop() }
}
