import { useEffect, useId, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { profilesApi } from '../api/profiles'
import type { PublicProfile } from '../api/profiles'
import { conversationError } from '../api/conversations'

export function UserPicker({ excluded, disabled, onSelect }: { excluded: string[]; disabled: boolean; onSelect: (user: PublicProfile) => void }) {
  const id = useId()
  const [input, setInput] = useState('')
  const [query, setQuery] = useState('')
  const [items, setItems] = useState<PublicProfile[]>([])
  const [cursor, setCursor] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [searched, setSearched] = useState(false)
  const [error, setError] = useState('')
  const active = useRef<AbortController | null>(null)
  useEffect(() => { const pending = active; return () => pending.current?.abort() }, [])
  async function search(text: string, next?: string) {
    active.current?.abort()
    const controller = new AbortController(); active.current = controller
    setBusy(true); setError(''); setSearched(true)
    if (!next) { setItems([]); setCursor(null); setQuery(text) }
    try {
      const result = await profilesApi.search(text, next, controller.signal)
      if (controller.signal.aborted) return
      setItems(previous => next ? [...new Map([...previous, ...result.items].map(x => [x.userId, x])).values()] : result.items)
      setCursor(result.nextCursor)
    } catch (cause) { if (!controller.signal.aborted) setError(conversationError(cause)) }
    finally { if (!controller.signal.aborted) setBusy(false) }
  }
  function submit(event: FormEvent) { event.preventDefault(); void search(input.trim()) }
  const available = items.filter(x => !excluded.includes(x.userId))
  return <div className="user-picker">
    <form className="search-form" onSubmit={submit}><div className="field"><label htmlFor={id}>Αναζήτηση ατόμων</label>
      <input id={id} type="search" value={input} maxLength={100} onChange={e => setInput(e.target.value)} /></div>
      <button disabled={disabled || busy}>Αναζήτηση ατόμων</button></form>
    {busy && <p role="status">Αναζήτηση…</p>}
    {error && <p role="alert">{error}</p>}
    {searched && !busy && !error && available.length === 0 && <p role="status">Δεν βρέθηκαν διαθέσιμα άτομα.</p>}
    <ul className="results">{available.map(user => <li key={user.userId}><span>{user.displayName}</span>
      <button disabled={disabled} onClick={() => onSelect(user)} aria-label={`Επιλογή ${user.displayName}`}>Επιλογή</button></li>)}</ul>
    {cursor && <button disabled={disabled || busy} onClick={() => void search(query, cursor)}>Περισσότερα άτομα</button>}
  </div>
}
