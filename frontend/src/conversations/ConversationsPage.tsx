import { useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { ApiError } from '../api/client'
import { conversationsApi, conversationError } from '../api/conversations'
import type { Conversation, Details } from '../api/conversations'
import type { PublicProfile } from '../api/profiles'
import { useAuth } from '../auth/context'
import { UserPicker } from './UserPicker'

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
  useEffect(() => {
    const controller = new AbortController(); active.current = controller
    void conversationsApi.list(0, controller.signal).then(result => {
      if (!controller.signal.aborted) { setItems(result.items); setNext(result.nextOffset) }
    }).catch(cause => { if (!controller.signal.aborted) setError(conversationError(cause)) })
      .finally(() => { if (!controller.signal.aborted) setBusy(false) })
    return () => { controller.abort(); active.current?.abort() }
  }, [])
  return <aside className="panel conversation-list" aria-label="Λίστα συνομιλιών"><h2>Οι συνομιλίες μου</h2>
    <button disabled={busy} onClick={() => void load()}>Ανανέωση λίστας</button>
    {busy && <p role="status">Φόρτωση συνομιλιών…</p>}
    {error && <p role="alert">{error}</p>}
    {!busy && !error && items.length === 0 && <p>Δεν έχεις ακόμη συνομιλίες.</p>}
    <ul className="results">{items.map(item => <li key={item.id}><a href={`#/conversations/${item.id}`} aria-current={item.id === selectedId ? 'page' : undefined}>{title(item)}</a>
      <p>{item.type === 'Group' ? 'Ομάδα' : 'Προσωπική'} · {item.memberCount} μέλη</p></li>)}</ul>
    {next !== null && <button disabled={busy} onClick={() => void load(next)}>Περισσότερες συνομιλίες</button>}
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
  const [busy, setBusy] = useState(true)
  const [error, setError] = useState('')
  const [unavailable, setUnavailable] = useState(false)
  const active = useRef<AbortController | null>(null)
  async function load(offset = 0) {
    active.current?.abort(); const controller = new AbortController(); active.current = controller
    setBusy(true); setError('')
    try {
      const result = await conversationsApi.details(id, offset, controller.signal)
      if (controller.signal.aborted) return
      setDetails(previous => offset && previous ? { ...result, members: [...new Map([...previous.members, ...result.members].map(x => [x.userId, x])).values()] } : result)
      setUnavailable(false)
    } catch (cause) {
      if (!controller.signal.aborted) { setError(conversationError(cause)); setDetails(null) }
    } finally { if (!controller.signal.aborted) setBusy(false) }
  }
  useEffect(() => {
    const controller = new AbortController(); active.current = controller
    void conversationsApi.details(id, 0, controller.signal).then(result => {
      if (!controller.signal.aborted) setDetails(result)
    }).catch(cause => { if (!controller.signal.aborted) setError(conversationError(cause)) })
      .finally(() => { if (!controller.signal.aborted) setBusy(false) })
    return () => { controller.abort(); active.current?.abort() }
  }, [id])
  async function change(operation: () => Promise<void>, left = false) {
    setBusy(true); setError('')
    try { await operation(); onChanged(left) }
    catch (cause) {
      setError(conversationError(cause))
      if (cause instanceof ApiError && [403, 404].includes(cause.status)) setUnavailable(true)
    } finally { setBusy(false) }
  }
  return <section className="panel conversation-details" aria-label="Στοιχεία συνομιλίας">
    {busy && <p role="status">Φόρτωση ή αποθήκευση…</p>}
    {error && <p role="alert">{error}</p>}
    <button disabled={busy} onClick={() => void load()}>Ανανέωση στοιχείων</button>
    {details && !unavailable && <><h2>{title(details)}</h2><p>{details.type === 'Group' ? 'Ομαδική συνομιλία' : 'Προσωπική συνομιλία'} · {details.memberCount} μέλη</p>
      <h3>Μέλη</h3><ul className="results">{details.members.map(member => <li key={member.userId}>
        <a href={`#/profiles/${member.userId}`}>{member.profile?.displayName ?? member.userId}</a> <span>{member.role === 'Owner' ? 'Ιδιοκτήτης' : 'Μέλος'}</span>
        {details.type === 'Group' && details.myRole === 'Owner' && member.role !== 'Owner' && <button disabled={busy}
          onClick={() => void change(() => conversationsApi.remove(id, member.userId))} aria-label={`Αφαίρεση ${member.profile?.displayName ?? member.userId}`}>Αφαίρεση</button>}
      </li>)}</ul>
      {details.nextOffset !== null && <button disabled={busy} onClick={() => void load(details.nextOffset!)}>Περισσότερα μέλη</button>}
      {details.type === 'Group' && details.myRole === 'Owner' && <><h3>Προσθήκη μέλους</h3>
        <UserPicker disabled={busy} excluded={details.members.map(x => x.userId)} onSelect={person => void change(() => conversationsApi.add(id, person.userId))} />
        <p className="hint">Ο ιδιοκτήτης δεν μπορεί να αποχωρήσει ή να αφαιρεθεί.</p></>}
      {details.type === 'Group' && details.myRole === 'Member' && <button disabled={busy} onClick={() => void change(() => conversationsApi.leave(id), true)}>Αποχώρηση από ομάδα</button>}
    </>}
  </section>
}

export function ConversationsPage({ selectedId }: { selectedId?: string }) {
  const [revision, setRevision] = useState(0)
  function changed(left = false) { setRevision(x => x + 1); if (left) window.location.hash = '/conversations' }
  return <><h1>Συνομιλίες</h1><div className="conversation-actions"><a href="#/conversations/new">Νέα συνομιλία</a><a href="#/conversations">Όλες οι συνομιλίες</a></div>
    <div className="conversation-layout"><ConversationList key={revision} selectedId={selectedId} />
      {selectedId === 'new' ? <CreateConversation onCreated={id => { changed(); window.location.hash = `/conversations/${id}` }} />
        : selectedId ? <ConversationDetails key={`${selectedId}-${revision}`} id={selectedId} onChanged={changed} />
          : <section className="panel"><h2>Επίλεξε συνομιλία</h2><p>Άνοιξε μια συνομιλία από τη λίστα ή δημιούργησε μια νέα.</p></section>}
    </div></>
}
