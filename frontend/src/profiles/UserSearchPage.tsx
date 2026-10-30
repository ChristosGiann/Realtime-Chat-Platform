import { useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { errorMessage } from '../api/client'
import { profilesApi } from '../api/profiles'
import type { PublicProfile } from '../api/profiles'
export function UserSearchPage() {
  const [input, setInput] = useState('')
  const [query, setQuery] = useState<string | null>(null)
  const [items, setItems] = useState<PublicProfile[]>([])
  const [cursor, setCursor] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const active = useRef<AbortController | null>(null)
  useEffect(() => { const pending = active; return () => pending.current?.abort() }, [])
  async function search(text: string, next?: string) {
    active.current?.abort()
    const controller = new AbortController()
    active.current = controller
    setBusy(true); setError('')
    if (!next) { setItems([]); setCursor(null); setQuery(text) }
    try {
      const page = await profilesApi.search(text, next, controller.signal)
      if (controller.signal.aborted) return
      setItems(previous => next ? [...new Map([...previous, ...page.items].map(item => [item.userId, item])).values()] : page.items)
      setCursor(page.nextCursor)
    } catch (cause) { if (!controller.signal.aborted) setError(errorMessage(cause)) }
    finally { if (!controller.signal.aborted) setBusy(false) }
  }
  function submit(event: FormEvent<HTMLFormElement>) { event.preventDefault(); void search(input.trim()) }
  return <section className="panel"><h1>Αναζήτηση χρηστών</h1>
    <form className="search-form" onSubmit={submit}><div className="field"><label htmlFor="query">Όνομα χρήστη ή εμφανιζόμενο όνομα</label>
      <input id="query" value={input} onChange={event => setInput(event.target.value)} maxLength={100} type="search" /></div>
      <button type="submit" disabled={busy}>Αναζήτηση</button></form>
    {query === null && <p>Πληκτρολόγησε ένα όνομα για να βρεις άλλους χρήστες.</p>}
    {query !== null && <p>Αποτελέσματα για: {query || 'κενή αναζήτηση'}</p>}
    {busy && <p role="status">Φόρτωση αποτελεσμάτων…</p>}
    {error && <p role="alert" className="error">{error} Μπορείς να επαναλάβεις την αναζήτηση.</p>}
    {!busy && !error && query !== null && items.length === 0 && <p role="status">{query ? 'Δεν βρέθηκαν χρήστες.' : 'Πληκτρολόγησε ένα όνομα για αναζήτηση.'}</p>}
    <ul className="results">{items.map(item => <li key={item.userId}><a href={`#/profiles/${item.userId}`}>{item.displayName}</a>
      {item.bio && <p className="bio">{item.bio}</p>}</li>)}</ul>
    {cursor && <button disabled={busy} onClick={() => void search(query!, cursor)}>Περισσότερα αποτελέσματα</button>}
  </section>
}
