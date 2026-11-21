import { useEffect, useEffectEvent, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { ApiError } from '../api/client'
import { conversationsApi, conversationError } from '../api/conversations'
import type { Conversation, Details, Member } from '../api/conversations'
import type { PublicProfile } from '../api/profiles'
import { useAuth } from '../auth/context'
import { UserPicker } from './UserPicker'
import { MessagingPanel } from '../messages/MessagingPanel'
import { ConversationDetailsPanel } from './ConversationDetailsPanel'
import type { MembershipConfirmation } from './ConversationDetailsPanel'

const title = (conversation: Conversation) => conversation.name ?? 'Direct συνομιλία'

function ConversationList({ selectedId }: { selectedId?: string }) {
  const [items, setItems] = useState<Conversation[]>([])
  const [next, setNext] = useState<number | null>(null)
  const [busy, setBusy] = useState(true)
  const [error, setError] = useState('')
  const active = useRef<AbortController | null>(null)
  async function load(offset = 0) {
    active.current?.abort(); const controller = new AbortController(); active.current = controller
    setBusy(true); setError('')
    try {
      const result = await conversationsApi.list(offset, controller.signal)
      if (controller.signal.aborted) return
      setItems(previous => offset ? [...new Map([...previous, ...result.items].map(x => [x.id, x])).values()] : result.items)
      setNext(result.nextOffset)
    } catch (cause) { if (!controller.signal.aborted) setError(conversationError(cause)) }
    finally { if (!controller.signal.aborted) setBusy(false) }
  }
  const refreshAfterRecovery = useEffectEvent(() => { void load() })
  useEffect(() => {
    const controller = new AbortController(); active.current = controller
    void conversationsApi.list(0, controller.signal).then(result => {
      if (!controller.signal.aborted) { setItems(result.items); setNext(result.nextOffset) }
    }).catch(cause => { if (!controller.signal.aborted) setError(conversationError(cause)) })
      .finally(() => { if (!controller.signal.aborted) setBusy(false) })
    const recovered = () => { refreshAfterRecovery() }
    window.addEventListener('conversation-resynced', recovered)
    return () => { controller.abort(); active.current?.abort(); window.removeEventListener('conversation-resynced', recovered) }
  }, [])
  return <aside className="conversation-list" aria-label="Λίστα συνομιλιών"><div className="conversation-list-header"><p className="eyebrow">Εισερχόμενα</p><h2>Οι συνομιλίες μου</h2>
    <div className="conversation-list-actions"><a href="#/conversations/new">Νέα συνομιλία</a><a href="#/conversations">Όλες οι συνομιλίες</a></div>
    <button disabled={busy} onClick={() => void load()}>Ανανέωση λίστας</button></div>
    <div className="conversation-list-scroll">
    {busy && <p role="status">Φόρτωση συνομιλιών…</p>}
    {error && <p role="alert">{error}</p>}
    {!busy && !error && items.length === 0 && <p>Δεν έχεις ακόμη συνομιλίες.</p>}
    <ul className="results conversation-items">{items.map(item => <li key={item.id} className={item.id === selectedId ? 'conversation-item conversation-item--active' : 'conversation-item'}><a href={`#/conversations/${item.id}`} aria-current={item.id === selectedId ? 'page' : undefined}>{title(item)}</a>
      <p>{item.type === 'Group' ? 'Ομάδα' : 'Προσωπική'} · {item.memberCount} μέλη</p></li>)}</ul>
    {next !== null && <button disabled={busy} onClick={() => void load(next)}>Περισσότερες συνομιλίες</button>}
    </div>
  </aside>
}

function CreateConversation({ onCreated }: { onCreated: (id: string) => void }) {
  const { user } = useAuth()
  const [mode, setMode] = useState<'Direct' | 'Group'>('Direct')
  const [name, setName] = useState('')
  const [members, setMembers] = useState<PublicProfile[]>([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [nameError, setNameError] = useState('')
  async function direct(person: PublicProfile) {
    setBusy(true); setError('')
    try { onCreated((await conversationsApi.direct(person.userId)).id) }
    catch (cause) { setError(conversationError(cause)) }
    finally { setBusy(false) }
  }
  async function group(event: FormEvent) {
    event.preventDefault(); setNameError(''); setError('')
    if (!name.trim()) { setNameError('Συμπλήρωσε όνομα ομάδας.'); return }
    setBusy(true)
    try { onCreated((await conversationsApi.group(name, members.map(x => x.userId))).id) }
    catch (cause) {
      setError(conversationError(cause))
      if (cause instanceof ApiError && cause.fields.name) setNameError(cause.fields.name.join(' '))
    } finally { setBusy(false) }
  }
  return <section className="panel"><h2>Νέα συνομιλία</h2>
    <div className="conversation-actions"><button disabled={busy} aria-pressed={mode === 'Direct'} onClick={() => { setMode('Direct'); setError('') }}>Προσωπική</button>
      <button disabled={busy} aria-pressed={mode === 'Group'} onClick={() => { setMode('Group'); setError('') }}>Ομάδα</button></div>
    <p>{mode === 'Direct' ? 'Επίλεξε ένα άτομο για να ξεκινήσεις ή να ανοίξεις την υπάρχουσα συνομιλία σας.' : 'Επίλεξε τα αρχικά μέλη. Εσύ θα είσαι ο ιδιοκτήτης της ομάδας.'}</p>
    {error && <p role="alert">{error}</p>}
    {busy && <p role="status">Δημιουργία…</p>}
    <UserPicker key={mode} disabled={busy} excluded={[user!.id, ...(mode === 'Group' ? members.map(x => x.userId) : [])]}
      onSelect={person => { if (mode === 'Direct') void direct(person); else setMembers(previous => [...previous, person]) }} />
    {mode === 'Group' && <><ul className="results">{members.map(person => <li key={person.userId}>{person.displayName}
      <button disabled={busy} onClick={() => setMembers(previous => previous.filter(x => x.userId !== person.userId))} aria-label={`Αφαίρεση επιλογής ${person.displayName}`}>Αφαίρεση επιλογής</button></li>)}</ul>
      <form onSubmit={group}><fieldset disabled={busy}><div className="field"><label htmlFor="group-name">Όνομα ομάδας</label>
        <input id="group-name" required maxLength={100} value={name} onChange={e => setName(e.target.value)} aria-invalid={!!nameError} aria-describedby={nameError ? 'group-name-error' : undefined} />
        {nameError && <p id="group-name-error" role="alert">{nameError}</p>}</div>
        <button>Δημιουργία ομάδας</button></fieldset></form></>}
  </section>
}

function ConversationDetails({ id, onChanged }: { id: string; onChanged: (left?: boolean) => void }) {
  const [details, setDetails] = useState<Details | null>(null)
  const [loading, setLoading] = useState(true)
  const [mutating, setMutating] = useState(false)
  const busy = loading || mutating
  const [error, setError] = useState('')
  const [unavailable, setUnavailable] = useState(false)
  const [confirmation, setConfirmation] = useState<MembershipConfirmation | null>(null)
  const [showDetails, setShowDetails] = useState(() => window.innerWidth >= 1200 || window.innerWidth <= 700)
  const detailsToggle = useRef<HTMLButtonElement | null>(null)
  const detailsPanel = useRef<HTMLElement | null>(null)
  const detailsClose = useRef<HTMLButtonElement | null>(null)
  const confirmButton = useRef<HTMLButtonElement | null>(null)
  const actionTrigger = useRef<HTMLButtonElement | null>(null)
  const mutation = useRef(false)
  const active = useRef<AbortController | null>(null)
  async function load(offset = 0) {
    active.current?.abort(); const controller = new AbortController(); active.current = controller
    setLoading(true); setError('')
    try {
      const result = await conversationsApi.details(id, offset, controller.signal)
      if (controller.signal.aborted) return
      setDetails(previous => offset && previous ? { ...result, members: [...new Map([...previous.members, ...result.members].map(x => [x.userId, x])).values()] } : result)
      setUnavailable(false)
    } catch (cause) {
      if (!controller.signal.aborted) { setError(`Αδυναμία φόρτωσης στοιχείων: ${conversationError(cause)}`); setDetails(null); setConfirmation(null) }
    } finally { if (!controller.signal.aborted) setLoading(false) }
  }
  const refreshAfterRecovery = useEffectEvent(() => { void load() })
  useEffect(() => {
    const controller = new AbortController(); active.current = controller
    void conversationsApi.details(id, 0, controller.signal).then(result => {
      if (!controller.signal.aborted) setDetails(result)
    }).catch(cause => { if (!controller.signal.aborted) setError(`Αδυναμία φόρτωσης στοιχείων: ${conversationError(cause)}`) })
      .finally(() => { if (!controller.signal.aborted) setLoading(false) })
    const recovered = () => { refreshAfterRecovery() }
    window.addEventListener('conversation-resynced', recovered)
    return () => { controller.abort(); active.current?.abort(); window.removeEventListener('conversation-resynced', recovered) }
  }, [id])
  async function change(operation: () => Promise<void>, action: 'add' | 'remove' | 'leave', left = false) {
    if (mutation.current || loading || unavailable) return
    mutation.current = true
    setMutating(true); setError('')
    try { await operation(); onChanged(left) }
    catch (cause) {
      const label = action === 'add' ? 'Αδυναμία προσθήκης μέλους' : action === 'remove' ? 'Αδυναμία αφαίρεσης μέλους' : 'Αδυναμία αποχώρησης'
      setError(`${label}: ${conversationError(cause)}`)
      if (cause instanceof ApiError && [403, 404].includes(cause.status)) { setUnavailable(true); setDetails(null) }
    } finally { mutation.current = false; setMutating(false); setConfirmation(null) }
  }
  function requestConfirmation(action: MembershipConfirmation, trigger: HTMLButtonElement) {
    if (busy || unavailable) return
    actionTrigger.current = trigger
    setConfirmation(action)
    requestAnimationFrame(() => confirmButton.current?.focus())
  }
  function cancelConfirmation() {
    setConfirmation(null)
    requestAnimationFrame(() => actionTrigger.current?.focus())
  }
  function confirmMembershipChange() {
    if (!confirmation || !details || busy || unavailable) return
    if (confirmation.kind === 'remove') {
      if (details.type !== 'Group' || details.myRole !== 'Owner' || !details.members.some(member => member.userId === confirmation.userId && member.role !== 'Owner')) return
      void change(() => conversationsApi.remove(id, confirmation.userId), 'remove')
    } else if (details.type === 'Group' && details.myRole === 'Member') void change(() => conversationsApi.leave(id), 'leave', true)
  }
  function toggleDetails() {
    if (busy) return
    if (showDetails) { setShowDetails(false); setConfirmation(null) }
    else {
      setShowDetails(true)
      requestAnimationFrame(() => (window.innerWidth > 700 && window.innerWidth < 1200 ? detailsClose.current : detailsPanel.current)?.focus())
    }
  }
  return <div className={showDetails ? 'conversation-content conversation-content--details' : 'conversation-content'}>
    <section className="conversation-workspace" aria-label="Ενεργή συνομιλία">
      <header className="conversation-workspace-header"><div><p className="eyebrow">Συνομιλία</p><h2>{details ? title(details) : 'Φόρτωση συνομιλίας…'}</h2>
        {details && !unavailable && <p>{details.type === 'Group' ? 'Ομαδική συνομιλία' : 'Προσωπική συνομιλία'} · {details.memberCount} μέλη</p>}</div>
        <button ref={detailsToggle} type="button" disabled={busy} aria-expanded={showDetails} aria-controls={showDetails ? 'conversation-utility' : undefined} onClick={toggleDetails}>{showDetails ? 'Κλείσιμο στοιχείων' : 'Στοιχεία συνομιλίας'}</button>
      </header>
      {!showDetails && busy && <p className="conversation-workspace-notice" role="status">Φόρτωση ή αποθήκευση…</p>}
      {!showDetails && error && <p className="conversation-workspace-notice" role="alert">{error}</p>}
      {details && !unavailable && <MessagingPanel key={id} id={id} type={details.type} />}
    </section>
    {showDetails && <aside id="conversation-utility" ref={detailsPanel} tabIndex={-1} className="conversation-utility" aria-label="Στοιχεία συνομιλίας">
      <div className="conversation-utility-header"><p className="eyebrow">Πληροφορίες</p><h3>Στοιχεία συνομιλίας</h3>
        <button ref={detailsClose} className="conversation-utility-close" type="button" disabled={busy} onClick={() => { setShowDetails(false); setConfirmation(null); requestAnimationFrame(() => detailsToggle.current?.focus()) }}>Κλείσιμο στοιχείων</button>
        <button disabled={busy} onClick={() => void load()}>Ανανέωση στοιχείων</button></div>
      <ConversationDetailsPanel details={details} busy={busy} loading={loading} error={error} unavailable={unavailable}
        confirmation={confirmation} confirmRef={confirmButton} onRefresh={() => void load()} onMore={offset => void load(offset)}
        onAdd={person => void change(() => conversationsApi.add(id, person.userId), 'add')}
        onRemove={(member: Member, trigger) => requestConfirmation({ kind: 'remove', userId: member.userId, name: member.profile?.displayName ?? member.userId }, trigger)}
        onLeave={trigger => requestConfirmation({ kind: 'leave' }, trigger)} onConfirm={confirmMembershipChange} onCancel={cancelConfirmation} />
    </aside>}
  </div>
}

export function ConversationsPage({ selectedId }: { selectedId?: string }) {
  const [revision, setRevision] = useState(0)
  function changed(left = false) { setRevision(x => x + 1); if (left) window.location.hash = '/conversations' }
  return <div className="conversation-page"><h1 className="visually-hidden">Συνομιλίες</h1>
    <div className="conversation-layout"><ConversationList key={revision} selectedId={selectedId} />
      {selectedId === 'new' ? <div className="conversation-content conversation-content--create"><CreateConversation onCreated={id => { changed(); window.location.hash = `/conversations/${id}` }} /></div>
        : selectedId ? <ConversationDetails key={`${selectedId}-${revision}`} id={selectedId} onChanged={changed} />
          : <section className="conversation-empty"><h2>Επίλεξε συνομιλία</h2><p>Άνοιξε μια συνομιλία από τη λίστα ή δημιούργησε μια νέα.</p></section>}
    </div></div>
}
