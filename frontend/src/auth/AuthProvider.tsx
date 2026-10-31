import { useCallback, useEffect, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { ApiError, authApi, errorMessage } from '../api/client'
import type { CurrentUser } from '../api/client'
import { AuthContext } from './context'
export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<CurrentUser | null>(null)
  const [status, setStatus] = useState<'loading' | 'ready' | 'error'>('loading')
  const [error, setError] = useState('')
  const generation = useRef(0)
  const load = useCallback(() => {
    const current = ++generation.current
    return authApi.me().then(result => {
      if (current === generation.current) { setUser(result); setStatus('ready'); setError('') }
    }, cause => {
      if (current !== generation.current) return
      setUser(null)
      if (cause instanceof ApiError && cause.status === 401) { setStatus('ready'); setError('') }
      else { setStatus('error'); setError(errorMessage(cause)) }
    })
  }, [])
  const refresh = useCallback(async () => { setStatus('loading'); await load() }, [load])
  useEffect(() => {
    const epoch = generation
    const expire = () => { ++generation.current; setUser(null); setStatus('ready'); setError('') }
    window.addEventListener('session-expired', expire)
    void load()
    return () => { ++epoch.current; window.removeEventListener('session-expired', expire) }
  }, [load])
  async function login(email: string, password: string) { await authApi.login(email, password); await refresh() }
  async function logout() {
    try { await authApi.logout() }
    catch (cause) { if (!(cause instanceof ApiError && cause.status === 401)) throw cause }
    ++generation.current; setUser(null); setStatus('ready')
  }
  function updateProfile(profile: Pick<CurrentUser, 'displayName' | 'bio' | 'avatarUrl'> & { userId: string }) {
    setUser(previous => previous?.id === profile.userId ? { ...previous, displayName: profile.displayName, bio: profile.bio, avatarUrl: profile.avatarUrl } : previous)
  }
  return <AuthContext.Provider value={{ user, status, error, refresh, login, logout, updateProfile }}>{children}</AuthContext.Provider>
}
