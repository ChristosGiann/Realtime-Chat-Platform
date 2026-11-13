import { ApiError } from '../api/client'
import { observePrefix, receiptsApi } from '../api/receipts'
import type { ReceiptSnapshot } from '../api/receipts'
import type { PresenceMember, RealtimeSession } from './RealtimeSession'

// Events invalidate a coalesced REST query; their payload is never receipt authority.
// One worker per transport, bounded pending flags, no per-message replay queue.
export class ReceiptSession {
  snapshot: ReceiptSnapshot | null = null
  private epoch = 0
  private signal?: AbortSignal
  private pending?: Promise<void>
  private dirty = false
  private observe = false
  private retry?: ReturnType<typeof setTimeout>
  private readonly id: string
  private readonly userId: string
  private readonly realtime: RealtimeSession | null
  private readonly changed: (state: ReceiptSnapshot | null, presence: PresenceMember[] | null) => void
  private readonly failed: (cause: unknown) => void
  constructor(id: string, userId: string, realtime: RealtimeSession | null,
    changed: (state: ReceiptSnapshot | null, presence: PresenceMember[] | null) => void, failed: (cause: unknown) => void) {
    this.id = id; this.userId = userId; this.realtime = realtime; this.changed = changed; this.failed = failed
  }

  seed(state: ReceiptSnapshot) { this.snapshot = state; this.changed(state, null) }
  async manual(signal: AbortSignal) {
    if (this.signal && !this.signal.aborted) { await this.refresh(true); return }
    const epoch = this.epoch, state = await receiptsApi.snapshot(this.id, signal)
    if (epoch !== this.epoch || signal.aborted) return
    if (this.snapshot && state.myMembershipId !== this.snapshot.myMembershipId) throw new ApiError(404)
    this.seed(state)
  }
  stop() { this.epoch++; this.signal = undefined; this.pending = undefined; if (this.retry) clearTimeout(this.retry); this.retry = undefined }
  async begin(signal: AbortSignal) {
    this.stop(); this.signal = signal
    await this.refresh(true)
  }
  invalidate(membership?: string) {
    if (membership && this.snapshot?.myMembershipId !== membership) { this.request(); return }
    if (membership) { this.stop(); this.snapshot = null; this.changed(null, null); this.failed(new ApiError(404)); return }
    this.changed(null, null); this.request(true)
  }
  request(observe = false) {
    const epoch = this.epoch
    void this.refresh(observe).catch(cause => {
      if (epoch !== this.epoch || !this.signal || this.signal.aborted) return
      if (cause instanceof ApiError && [401, 403, 404].includes(cause.status)) { this.failed(cause); return }
      if (!this.retry) this.retry = setTimeout(() => { this.retry = undefined; this.request(observe) }, 1000)
    })
  }
  refresh(observe = false): Promise<void> {
    const signal = this.signal, epoch = this.epoch
    if (!signal || signal.aborted) return Promise.resolve()
    this.dirty = true; this.observe ||= observe
    if (this.pending) return this.pending
    const current = () => epoch === this.epoch && !signal.aborted
    this.pending = (async () => {
      while (this.dirty && current()) {
        this.dirty = false; const observe = this.observe; this.observe = false
        let state = await receiptsApi.snapshot(this.id, signal)
        const membership = state.myMembershipId
        if (!current()) return
        if (this.snapshot && state.myMembershipId !== this.snapshot.myMembershipId) throw new ApiError(404)
        if (observe) {
          const floor = state.items.find(x => x.userId === this.userId)?.delivered?.messageId ?? null
          const newest = await observePrefix(this.id, floor, signal)
          if (!current()) return
          if (newest && newest !== floor) {
            await receiptsApi.deliver(this.id, newest, state.myMembershipId, signal)
            state = await receiptsApi.snapshot(this.id, signal)
            if (state.myMembershipId !== membership) throw new ApiError(404)
          }
        }
        const presence = this.realtime ? await this.realtime.presence(this.id, signal) : null
        if (!current()) return
        this.snapshot = state
        this.changed(state, presence?.filter(x => state.items.some(member => member.userId === x.userId)) ?? null)
      }
    })().catch(cause => {
      if (current()) { this.dirty = true; this.observe = true }
      throw cause
    }).finally(() => { if (epoch === this.epoch) this.pending = undefined })
    return this.pending
  }
}
