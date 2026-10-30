import { useEffect, useState } from 'react'
import { errorMessage } from '../api/client'
import { profilesApi } from '../api/profiles'
import type { PublicProfile } from '../api/profiles'
function safeAvatar(value: string | null) {
  if (!value) return null
  try { const url = new URL(value); return url.protocol === 'https:' && !url.username && !url.password ? url.href : null }
  catch { return null }
}
export function ProfileDetails({ profile }: { profile: PublicProfile }) {
  const avatar = safeAvatar(profile.avatarUrl)
  return <><h1>{profile.displayName}</h1><p className="bio">{profile.bio || 'Δεν έχει προστεθεί βιογραφικό.'}</p>
    {avatar && <a href={avatar} target="_blank" rel="noopener noreferrer">Άνοιγμα εικόνας προφίλ</a>}</>
}
export function PublicProfilePage({ id }: { id: string }) {
  const [profile, setProfile] = useState<PublicProfile | null>(null)
  const [error, setError] = useState('')
  const [attempt, setAttempt] = useState(0)
  useEffect(() => {
    const controller = new AbortController()
    void profilesApi.get(id, controller.signal).then(setProfile, cause => {
      if (!controller.signal.aborted) setError(errorMessage(cause))
    })
    return () => controller.abort()
  }, [id, attempt])
  return <section className="panel"><p className="eyebrow">Δημόσιο προφίλ</p>
    {error ? <><p role="alert" className="error">{error}</p><button onClick={() => { setError(''); setAttempt(value => value + 1) }}>Δοκιμή ξανά</button></>
      : profile ? <ProfileDetails profile={profile} /> : <p role="status">Φόρτωση προφίλ…</p>}
  </section>
}
