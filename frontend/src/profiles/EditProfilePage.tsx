import { useState } from 'react'
import type { FormEvent } from 'react'
import { ApiError, errorMessage } from '../api/client'
import { profilesApi } from '../api/profiles'
import { useAuth } from '../auth/context'
export function EditProfilePage() {
  const auth = useAuth()
  const user = auth.user!
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [saved, setSaved] = useState(false)
  const [fields, setFields] = useState<Record<string, string[]>>({})
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const data = new FormData(event.currentTarget)
    setBusy(true); setSaved(false); setError(''); setFields({})
    try {
      const profile = await profilesApi.update({ displayName: String(data.get('displayName')).trim(),
        bio: String(data.get('bio')).trim() || null, avatarUrl: String(data.get('avatarUrl')).trim() || null })
      auth.updateProfile(profile)
      setSaved(true)
    } catch (cause) { setError(errorMessage(cause)); if (cause instanceof ApiError) setFields(cause.fields) }
    finally { setBusy(false) }
  }
  function field(name: 'displayName' | 'bio' | 'avatarUrl', label: string, maxLength: number) {
    const messages = fields[name.toLowerCase()]
    const props = { id: name, name, defaultValue: user[name] ?? '', maxLength,
      'aria-invalid': !!messages, 'aria-describedby': messages ? `${name}-error` : undefined }
    return <div className="field"><label htmlFor={name}>{label}</label>
      {name === 'bio' ? <textarea {...props} /> : <input {...props} type={name === 'avatarUrl' ? 'url' : 'text'} required={name === 'displayName'} />}
      {messages && <p className="error" id={`${name}-error`}>{messages.join(' ')}</p>}</div>
  }
  return <section className="panel"><h1>Το προφίλ μου</h1><p>Επεξεργάσου τα δημόσια στοιχεία σου.</p>
    <form onSubmit={submit} onChange={() => setSaved(false)} aria-busy={busy}><fieldset disabled={busy}>
      {field('displayName', 'Εμφανιζόμενο όνομα', 100)}{field('bio', 'Βιογραφικό', 1000)}
      {field('avatarUrl', 'URL εικόνας προφίλ (HTTPS)', 2048)}
      <p className="hint">Προαιρετικό HTTPS URL. Δεν υποστηρίζεται μεταφόρτωση αρχείου. Κενό πεδίο αφαιρεί την προηγούμενη τιμή.</p>
      {error && <p role="alert" className="error">{error}</p>}
      {saved && <p role="status">Το προφίλ αποθηκεύτηκε.</p>}
      <button type="submit">{busy ? 'Αποθήκευση…' : 'Αποθήκευση'}</button>
    </fieldset></form><a href={`#/profiles/${user.id}`}>Προβολή δημόσιου προφίλ</a>
  </section>
}
