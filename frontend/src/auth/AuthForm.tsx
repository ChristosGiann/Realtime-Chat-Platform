import { useState } from 'react'
import type { FormEvent } from 'react'
import { ApiError, authApi, errorMessage } from '../api/client'
import { useAuth } from './context'
export function AuthForm({ register }: { register: boolean }) {
  const auth = useAuth()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [fields, setFields] = useState<Record<string, string[]>>({})
  const [created, setCreated] = useState(false)
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const form = event.currentTarget
    const data = new FormData(form)
    setBusy(true); setError(''); setFields({})
    try {
      const email = String(data.get('email')).trim()
      const password = String(data.get('password'))
      if (register) {
        await authApi.register({ email, password, userName: String(data.get('userName')).trim(),
          displayName: String(data.get('displayName')).trim() || undefined })
        form.reset(); setCreated(true)
      } else { await auth.login(email, password) }
    } catch (cause) {
      setError(!register && cause instanceof ApiError && cause.status === 401 ? 'Λανθασμένο email ή κωδικός.' : errorMessage(cause))
      if (cause instanceof ApiError) setFields(cause.fields)
    } finally { setBusy(false) }
  }
  function field(name: string, label: string, type = 'text', maxLength = 100, required = true) {
    const messages = fields[name.toLowerCase()]
    return <div className="field"><label htmlFor={name}>{label}</label>
      <input id={name} name={name} type={type} required={required} maxLength={maxLength}
        minLength={name === 'password' && register ? 6 : undefined}
        autoComplete={name === 'password' ? (register ? 'new-password' : 'current-password') : name === 'email' ? 'email' : name === 'userName' ? 'username' : 'nickname'}
        aria-invalid={!!messages} aria-describedby={messages ? `${name}-error` : undefined} />
      {messages && <p id={`${name}-error`} className="error">{messages.join(' ')}</p>}</div>
  }
  if (created) return <section className="panel"><h1>Ο λογαριασμός δημιουργήθηκε</h1><p role="status">Μπορείς τώρα να συνδεθείς.</p><a href="#/login">Μετάβαση στη σύνδεση</a></section>
  return <section className="panel auth-panel"><p className="eyebrow">Realtime Chat Platform</p>
    <h1>{register ? 'Δημιουργία λογαριασμού' : 'Σύνδεση'}</h1>
    <p>{register ? 'Δημιούργησε το προφίλ σου για να ξεκινήσεις.' : 'Συνδέσου για να διαχειριστείς τον λογαριασμό σου.'}</p>
    <form onSubmit={submit} aria-busy={busy}><fieldset disabled={busy}>
      {field('email', 'Email', 'email', 256)}
      {register && field('userName', 'Όνομα χρήστη')}
      {register && field('displayName', 'Εμφανιζόμενο όνομα (προαιρετικό)', 'text', 100, false)}
      {field('password', 'Κωδικός', 'password', 128)}
      {register && <p className="hint">Τουλάχιστον 6 χαρακτήρες, με κεφαλαίο, πεζό, αριθμό και σύμβολο.</p>}
      {error && <p role="alert" className="error">{error}</p>}
      <button type="submit">{busy ? 'Παρακαλώ περίμενε…' : register ? 'Δημιουργία λογαριασμού' : 'Σύνδεση'}</button>
    </fieldset></form>
    <p>{register ? 'Έχεις ήδη λογαριασμό;' : 'Δεν έχεις λογαριασμό;'} <a href={register ? '#/login' : '#/register'}>{register ? 'Σύνδεση' : 'Εγγραφή'}</a></p>
  </section>
}
