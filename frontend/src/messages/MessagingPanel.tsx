import { useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { ApiError } from '../api/client'
import { messagesApi, messagingError, recoverMessages } from '../api/messages'
import type { HistoryMessage, ReadState, SendRequest } from '../api/messages'
import { useAuth } from '../auth/context'
import { useRealtime } from './realtimeContext'
import type { LiveStatus } from './RealtimeSession'

// Preserve PostgreSQL microseconds; JavaScript Date would truncate timestamp ties to milliseconds.
const timeKey = (value: string) => value.slice(0, 19) + (value.includes('.') ? value.split('.')[1].replace('Z', '') : '').padEnd(7, '0')
function mergeMessages(items: HistoryMessage[]) {
  return [...new Map(items.map(item => [item.id, item])).values()].sort((a, b) => {
    const left = timeKey(a.createdAt) + a.id, right = timeKey(b.createdAt) + b.id
    return left > right ? -1 : left < right ? 1 : 0
  })
}

export function MessagingPanel({ id }: { id: string }) {
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
  const active = useRef<AbortController | null>(null)
  const alive = useRef(false)
  const mutation = useRef(false)
  const liveStop = useRef<(() => void) | undefined>(undefined)
  const busy = loading || sending || reading
  function fail(cause: unknown) {
    setError(messagingError(cause))
    if (cause instanceof ApiError && [401, 403, 404].includes(cause.status)) {
      liveStop.current?.()
      setUnavailable(true); setItems([]); setRead(null); setReadTarget(null); setNext(null)
    }
  }
  useEffect(() => {
    alive.current = true
    // Lifetime requests must not be aborted by the manual refresh/pagination controller.
    const controller = new AbortController()
    let stop: (() => void) | undefined
    let anchor: string | null = null
    let readVersion = 0
    const connect = () => realtime?.subscribe(id, {
      status: setLive,
      validate: async signal => { await messagesApi.readState(id, signal) },
      rejected: cause => { fail(cause); window.dispatchEvent(new Event('conversation-resynced')) },
      message: message => {
        setItems(previous => mergeMessages([...previous, { ...message, sender: {
          userId: message.senderId, displayName: message.senderId === user?.id ? user.displayName ?? user.userName : null, avatarUrl: null,
        } }]))
        const version = ++readVersion
        void messagesApi.readState(id, controller.signal).then(state => {
          if (!controller.signal.aborted && version === readVersion) setRead(state)
        }).catch(cause => { if (!controller.signal.aborted) fail(cause) })
      },
      subscribed: async signal => {
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
    void Promise.all([messagesApi.history(id, null, controller.signal), messagesApi.readState(id, controller.signal)])
      .then(([page, state]) => {
        if (controller.signal.aborted) return
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
    return () => { alive.current = false; controller.abort(); active.current?.abort(); stop?.() }
  }, [id, realtime, user?.id, user?.displayName, user?.userName])
  async function load(older = false) {
    if (mutation.current) return
    active.current?.abort(); const controller = new AbortController(); active.current = controller
    setLoading(true); setError('')
    try {
      const [page, state] = await Promise.all([messagesApi.history(id, older ? next : null, controller.signal), messagesApi.readState(id, controller.signal)])
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
      setPending(null); setBody('')
    } catch (cause) {
      if (!alive.current) return
      setSendError(messagingError(cause))
      if (cause instanceof ApiError && [400, 409].includes(cause.status)) setPending(null)
      if (cause instanceof ApiError && [401, 403, 404].includes(cause.status)) fail(cause)
    } finally { mutation.current = false; if (alive.current) setSending(false) }
  }
  async function markRead() {
    if (!readTarget || mutation.current || busy || unavailable) return
    mutation.current = true; setReading(true); setError('')
    try { const state = await messagesApi.advance(id, readTarget); if (alive.current) setRead(state) }
    catch (cause) { if (alive.current) fail(cause) }
    finally { mutation.current = false; if (alive.current) setReading(false) }
  }
  return <section className="messaging-panel" aria-label="Μηνύματα συνομιλίας"><h3>Μηνύματα</h3>
    <p role="status" aria-label="Κατάσταση live σύνδεσης">{live === 'connected' ? 'Ζωντανή σύνδεση' : live === 'connecting' ? 'Σύνδεση ζωντανών μηνυμάτων…' : live === 'reconnecting' ? 'Επανασύνδεση…' : 'Χωρίς ζωντανή σύνδεση — μπορείς να ανανεώσεις τα μηνύματα.'}</p>
    <button disabled={busy} onClick={() => void load()}>Ανανέωση μηνυμάτων</button>
    {loading && <p role="status">Φόρτωση μηνυμάτων…</p>}
    {error && <p role="alert">{error}</p>}
    {read && <p aria-live="polite">Μη αναγνωσμένα: {read.unreadCount}</p>}
    {readTarget && !unavailable && <><button disabled={busy} onClick={() => void markRead()}>Σήμανση ιστορικού ως αναγνωσμένου</button>
      <p className="hint">Περιλαμβάνει όλα τα μηνύματα έως το νεότερο που φορτώθηκε από το ιστορικό.</p></>}
    {reading && <p role="status">Αποθήκευση ανάγνωσης…</p>}
    {next && !unavailable && <button disabled={busy} onClick={() => void load(true)}>Παλαιότερα μηνύματα</button>}
    {!loading && !error && items.length === 0 && <p>Δεν υπάρχουν ακόμη μηνύματα.</p>}
    <ol className="message-list" aria-label="Ιστορικό μηνυμάτων">{[...items].reverse().map(item => <li key={item.id} data-message-id={item.id}>
      <strong>{item.sender.displayName ?? 'Χρήστης'}</strong> <time dateTime={item.createdAt}>{new Date(item.createdAt).toLocaleString('el-GR')}</time>
      <p className="message-body">{item.body}</p>
    </li>)}</ol>
    {!unavailable && <form onSubmit={send}><div className="field"><label htmlFor={`message-${id}`}>Νέο μήνυμα</label>
      <textarea id={`message-${id}`} value={body} maxLength={4000} disabled={busy || pending !== null} onChange={event => { setBody(event.target.value); setSendError('') }} />
      <p>{body.length}/4000</p></div>
      {sendError && <p role="alert">{sendError}</p>}
      {pending && !sending && <p>Η αποστολή δεν επιβεβαιώθηκε. Επανάλαβε το ίδιο μήνυμα με ασφάλεια πριν γράψεις άλλο.</p>}
      {sending && <p role="status">Αποστολή μηνύματος…</p>}
      <button disabled={busy} type="submit">{pending && !sending ? 'Επανάληψη αποστολής' : 'Αποστολή'}</button>
    </form>}
  </section>
}
