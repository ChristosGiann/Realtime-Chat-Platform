import { createContext, useContext } from 'react'
import type { CurrentUser } from '../api/client'
export type Session = {
  updateProfile: (profile: Pick<CurrentUser, 'displayName' | 'bio' | 'avatarUrl'> & { userId: string }) => void
  user: CurrentUser | null; status: 'loading' | 'ready' | 'error'; error: string
  refresh: () => Promise<void>; login: (email: string, password: string) => Promise<void>; logout: () => Promise<void>
}
export const AuthContext = createContext<Session | null>(null)
export function useAuth() {
  const context = useContext(AuthContext)
  if (!context) throw new Error('AuthProvider is required')
  return context
}
