import { useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { ApiError } from '../api/client'
import { messagesApi, messagingError, recoverMessages } from '../api/messages'
import type { HistoryMessage, ReadState, SendRequest } from '../api/messages'
import { useAuth } from '../auth/context'
import { useRealtime } from './realtimeContext'
import type { LiveStatus } from './RealtimeSession'
import type { PresenceMember } from './RealtimeSession'
import { covers, receiptsApi } from '../api/receipts'
import type { ReceiptSnapshot } from '../api/receipts'
import { ReceiptSession } from './ReceiptSession'

// Preserve PostgreSQL microseconds; JavaScript Date would truncate timestamp ties to milliseconds.
const timeKey = (value: string) => value.slice(0, 19) + (value.includes('.') ? value.split('.')[1].replace('Z', '') : '').padEnd(7, '0')
function mergeMessages(items: HistoryMessage[]) {
  return [...new Map(items.map(item => [item.id, item])).values()].sort((a, b) => {
    const left = timeKey(a.createdAt) + a.id, right = timeKey(b.createdAt) + b.id
    return left > right ? -1 : left < right ? 1 : 0
  })
}

function displayTime(value: string) {
  const timestamp = new Date(value)
  const now = new Date()
  const sameDay = timestamp.getFullYear() === now.getFullYear() && timestamp.getMonth() === now.getMonth() && timestamp.getDate() === now.getDate()
  return new Intl.DateTimeFormat('el-GR', sameDay ? { hour: '2-digit', minute: '2-digit' } : {
    day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit',
  }).format(timestamp)
}

export function MessagingPanel({ id, type }: { id: string; type: 'Direct' | 'Group' }) {
  const { user } = useAuth()
  const realtime = useRealtime()
  const [items, setItems] = useState<HistoryMessage[]>([])
  const [next, setNext] = useState<string | null>(null)
  const [read, setRead] = useState<ReadState | null>(null)
  const [readTarget, setReadTarget] = useState<string | null>(null)
  const [body, setBody] = useState('')
  const [pending, setPending] = useState<SendRequest | null>(null)
  const [loading, setLoading] = useState(true)
  const [sending, setSending] = useState(false)
  const [reading, setReading] = useState(false)
  const [unavailable, setUnavailable] = useState(false)
  const [error, setError] = useState('')
  const [sendError, setSendError] = useState('')
  const [live, setLive] = useState<LiveStatus>('connecting')
  const [typingUsers, setTypingUsers] = useState<string[]>([])
  const [receipts, setReceipts] = useState<ReceiptSnapshot | null>(null)
  const [presence, setPresence] = useState<PresenceMember[] | null>(null)
  const receiptSession = useRef<ReceiptSession | null>(null)
  const active = useRef<AbortController | null>(null)
  const alive = useRef(false)
  const mutation = useRef(false)
  const liveStop = useRef<(() => void) | undefined>(undefined)
  const busy = loading || sending || reading
  const timeline = [...items].reverse()
  function fail(cause: unknown) {
    setError(messagingError(cause))
    if (cause instanceof ApiError && [401, 403, 404].includes(cause.status)) {
      liveStop.current?.()
      setUnavailable(true); setItems([]); setRead(null); setReadTarget(null); setNext(null)
      receiptSession.current?.stop(); setReceipts(null); setPresence(null); setTypingUsers([])
      window.dispatchEvent(new Event('conversation-resynced'))
    }
  }
  useEffect(() => {
    alive.current = true
    // Lifetime requests must not be aborted by the manual refresh/pagination controller.
    const controller = new AbortController()
    let stop: (() => void) | undefined
    let anchor: string | null = null
    let readVersion = 0
    let presenceExpiry: ReturnType<typeof setTimeout> | undefined
    const progress = new ReceiptSession(id, user?.id ?? '', realtime, (state, online) => {
      if (!controller.signal.aborted) {
        if (presenceExpiry) clearTimeout(presenceExpiry)
        setReceipts(state); setPresence(online)
        if (online !== null) presenceExpiry = setTimeout(() => { setPresence(null); progress.request() }, 10_000)
      }
    }, fail)
    receiptSession.current = progress
    const connect = () => realtime?.subscribe(id, {
      status: status => { setLive(status); if (status !== 'connected') { progress.stop(); setPresence(null) } },
      typing: setTypingUsers,
      contextChanged: kind => {
        if (kind === 'membership') { progress.invalidate(); window.dispatchEvent(new Event('conversation-resynced')) }
        else progress.request()
        if (kind === 'receipt') {
          const version = ++readVersion
          void messagesApi.readState(id, controller.signal).then(state => {
            if (!controller.signal.aborted && version === readVersion) setRead(state)
          }).catch(cause => { if (!controller.signal.aborted) fail(cause) })
        }
      },
      revoked: membership => progress.invalidate(membership),
      validate: async signal => { await messagesApi.readState(id, signal) },
      rejected: cause => { fail(cause); window.dispatchEvent(new Event('conversation-resynced')) },
      message: message => {
        progress.request(true)
        setItems(previous => mergeMessages([...previous, { ...message, sender: {
          userId: message.senderId, displayName: message.senderId === user?.id ? user.displayName ?? user.userName : null, avatarUrl: null,
        } }]))
        const version = ++readVersion
        void messagesApi.readState(id, controller.signal).then(state => {
          if (!controller.signal.aborted && version === readVersion) setRead(state)
        }).catch(cause => { if (!controller.signal.aborted) fail(cause) })
      },
      subscribed: async signal => {
        await progress.begin(signal)
        const page = await recoverMessages(id, anchor, signal)
        const version = ++readVersion
        const state = await messagesApi.readState(id, signal)
        if (!controller.signal.aborted && !signal.aborted) {
          setError(''); setUnavailable(false)
          setItems(previous => mergeMessages([...previous, ...page.items])); setNext(page.nextCursor)
          if (version === readVersion) setRead(state)
          setReadTarget(page.items[0]?.id ?? null)
          anchor = page.items[0]?.id ?? anchor
          window.dispatchEvent(new Event('conversation-resynced'))
        }
      },
    })
    void Promise.all([messagesApi.history(id, null, controller.signal), messagesApi.readState(id, controller.signal), receiptsApi.snapshot(id, controller.signal)])
      .then(([page, state, receiptState]) => {
        if (controller.signal.aborted) return
        progress.seed(receiptState)
        anchor = page.items[0]?.id ?? null
        setItems(previous => mergeMessages([...previous, ...page.items])); setNext(page.nextCursor); setRead(state); setReadTarget(anchor)
        stop = connect(); liveStop.current = stop
      }).catch(cause => {
        if (!controller.signal.aborted) {
          fail(cause)
          if (!(cause instanceof ApiError && [401, 403, 404].includes(cause.status))) { stop = connect(); liveStop.current = stop }
        }
      })
      .finally(() => { if (!controller.signal.aborted) setLoading(false) })
    return () => { alive.current = false; controller.abort(); active.current?.abort(); if (presenceExpiry) clearTimeout(presenceExpiry); progress.stop(); stop?.() }
  }, [id, realtime, user?.id, user?.displayName, user?.userName])
  async function load(older = false) {
    if (mutation.current) return
    active.current?.abort(); const controller = new AbortController(); active.current = controller
    setLoading(true); setError('')
    try {
      const [page, state] = await Promise.all([messagesApi.history(id, older ? next : null, controller.signal), messagesApi.readState(id, controller.signal), receiptSession.current?.manual(controller.signal)])
      if (controller.signal.aborted) return
      setItems(previous => mergeMessages([...previous, ...page.items])); setNext(page.nextCursor); setRead(state); setUnavailable(false)
      if (!older) setReadTarget(page.items[0]?.id ?? null)
    } catch (cause) { if (!controller.signal.aborted) fail(cause) }
    finally { if (!controller.signal.aborted) setLoading(false) }
  }
  async function send(event: FormEvent) {
    event.preventDefault()
    if (mutation.current || loading || unavailable) return
    if (!pending && (!body.trim() || body.length > 4000 || body.includes('\0'))) { setSendError('Γράψε μήνυμα 1–4000 χαρακτήρων, όχι μόνο κενά και χωρίς NUL.'); return }
    const draft = pending ?? { clientMessageId: crypto.randomUUID(), body }
    mutation.current = true; setPending(draft); setSending(true); setSendError('')
    try {
      const saved = await messagesApi.send(id, draft)
      if (!alive.current) return
      setItems(previous => mergeMessages([...previous, { ...saved, sender: { userId: saved.senderId, displayName: user?.displayName ?? user?.userName ?? null, avatarUrl: user?.avatarUrl ?? null } }]))
      realtime?.typing(id, false); setPending(null); setBody('')
      receiptSession.current?.request(true)
    } catch (cause) {
      if (!alive.current) return
      setSendError(messagingError(cause))
      if (cause instanceof ApiError && [400, 409].includes(cause.status)) setPending(null)
      if (cause instanceof ApiError && [401, 403, 404].includes(cause.status)) fail(cause)
    } finally { mutation.current = false; if (alive.current) setSending(false) }
  }
  async function markRead() {
    const membership = receiptSession.current?.snapshot?.myMembershipId
    if (!readTarget || !membership || mutation.current || busy || unavailable) return
    mutation.current = true; setReading(true); setError('')
    try { const state = await messagesApi.advance(id, readTarget, membership); if (alive.current) { setRead(state); receiptSession.current?.request() } }
    catch (cause) { if (alive.current) fail(cause) }
    finally { mutation.current = false; if (alive.current) setReading(false) }
  }
  return <section className="messaging-panel" aria-label="Μηνύματα συνομιλίας"><div className="messaging-toolbar"><div><h3>Μηνύματα</h3>
    {!unavailable && <p className="presence-state" data-presence={presence === null ? 'unknown' : presence.some(x => x.userId !== user?.id && x.isOnline) ? 'online' : 'offline'} aria-label="Παρουσία συνομιλίας">{presence === null ? 'Παρουσία: άγνωστη / επανασύνδεση' : `${presence.filter(x => x.userId !== user?.id && x.isOnline).length} online · ${presence.filter(x => x.userId !== user?.id && !x.isOnline).length} offline (άλλα μέλη)`}</p>}
    <p role="status" aria-label="Κατάσταση live σύνδεσης" data-live-status={live}>{live === 'connected' ? 'Ζωντανή σύνδεση' : live === 'connecting' ? 'Σύνδεση ζωντανών μηνυμάτων…' : live === 'reconnecting' ? 'Επανασύνδεση…' : 'Χωρίς ζωντανή σύνδεση — μπορείς να ανανεώσεις τα μηνύματα.'}</p></div>
    <button disabled={busy} onClick={() => void load()}>Ανανέωση μηνυμάτων</button></div>
    <div className="messaging-progress">
    {read && <p className="unread-state" data-has-unread={read.unreadCount > 0} aria-live="polite">Μη αναγνωσμένα: {read.unreadCount}</p>}
    {readTarget && !unavailable && <><button disabled={busy || !receipts} onClick={() => void markRead()}>Σήμανση ιστορικού ως αναγνωσμένου</button>
      <p className="hint visually-hidden">Έως το νεότερο μήνυμα που φορτώθηκε.</p></>}
    {reading && <p role="status">Αποθήκευση ανάγνωσης…</p>}</div>
    <div className="message-history">
    {loading && <p className="history-notice" role="status">Φόρτωση μηνυμάτων…</p>}
    {error && <p className={unavailable ? 'history-notice history-notice--access' : 'history-notice history-notice--error'} role="alert">{error}</p>}
    {next && !unavailable && <button disabled={busy} onClick={() => void load(true)}>Παλαιότερα μηνύματα</button>}
    {!loading && !error && items.length === 0 && <div className="message-empty"><h4>Εδώ ξεκινά η συζήτηση</h4><p>Δεν υπάρχουν ακόμη μηνύματα.</p><p>Γράψε το πρώτο μήνυμα παρακάτω.</p></div>}
    <ol className="message-list" aria-label="Ιστορικό μηνυμάτων">{timeline.map((item, index) => {
      const own = item.senderId === user?.id
      const previous = timeline[index - 1]
      const grouped = previous?.senderId === item.senderId && new Date(item.createdAt).getTime() - new Date(previous.createdAt).getTime() <= 5 * 60_000
        && new Date(item.createdAt).getTime() >= new Date(previous.createdAt).getTime()
      const receipt = own ? receiptLabel(receipts, user!.id, item) : null
      return <li key={item.id} data-message-id={item.id} className={`message-item ${own ? 'message-item--outgoing' : 'message-item--incoming'}${grouped ? ' message-item--grouped' : ''}`}>
      <div className="message-meta"><strong className={own || type === 'Direct' || grouped ? 'visually-hidden' : undefined}>{item.sender.displayName ?? 'Χρήστης'}</strong>
        <time dateTime={item.createdAt}>{displayTime(item.createdAt)}</time></div>
      <p className="message-body">{item.body}</p>
      {receipt && <p className="message-receipt" data-receipt-state={(receipts?.conversationType ?? type) === 'Direct' ? receipt.split(' · ')[0] : 'Group'} aria-label="Κατάσταση μηνύματος">{receipt}</p>}
    </li>})}</ol></div>
    <div className="message-composer">
    <div className="typing-slot">{typingUsers.some(id => receipts?.items.some(member => member.userId === id)) && !unavailable && <p role="status" aria-label="Πληκτρολόγηση">{typingUsers.filter(id => receipts?.items.some(member => member.userId === id)).length === 1 ? 'Ένας χρήστης πληκτρολογεί…' : `${typingUsers.filter(id => receipts?.items.some(member => member.userId === id)).length} χρήστες πληκτρολογούν…`}</p>}</div>
    {!unavailable && <form onSubmit={send}><div className="field composer-surface"><label htmlFor={`message-${id}`}>Νέο μήνυμα</label>
      <textarea id={`message-${id}`} value={body} maxLength={4000} disabled={busy || pending !== null} aria-invalid={!!sendError}
        onBlur={() => realtime?.typing(id, false)}
        onChange={event => { setBody(event.target.value); setSendError(''); realtime?.typing(id, event.target.value.trim().length > 0) }} />
      <div className="composer-footer"><p>{body.length}/4000</p><button disabled={busy} type="submit">{pending && !sending ? 'Επανάληψη αποστολής' : 'Αποστολή'}</button></div></div>
      {sendError && <p className="composer-error" role="alert">{sendError}</p>}
      {pending && !sending && <p className="composer-retry">Η αποστολή δεν επιβεβαιώθηκε. Επανάλαβε το ίδιο μήνυμα με ασφάλεια πριν γράψεις άλλο.</p>}
      {sending && <p role="status">Αποστολή μηνύματος…</p>}
    </form>}</div>
  </section>
}

function receiptLabel(state: ReceiptSnapshot | null, sender: string, message: HistoryMessage) {
  if (!state) return 'Sent · ενημέρωση αποδείξεων…'
  const recipients = state.items.filter(x => x.userId !== sender)
  const delivered = recipients.filter(x => covers(x.delivered, message)).length
  const read = recipients.filter(x => covers(x.read, message)).length
  if (state.conversationType === 'Direct') return read ? 'Read' : delivered ? 'Delivered' : 'Sent'
  if (!recipients.length) return 'Sent · χωρίς άλλα τρέχοντα μέλη'
  return `Sent · Delivered ${delivered}/${recipients.length} · Read ${read}/${recipients.length} (τρέχοντα μέλη)`
}
