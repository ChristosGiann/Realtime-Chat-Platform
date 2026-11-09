import { useEffect, useState } from 'react'
import type { ReactNode } from 'react'
import { useAuth } from '../auth/context'
import { RealtimeSession } from './RealtimeSession'
import { RealtimeContext } from './realtimeContext'

function SessionConnection({ userId, children }: { userId: string; children: ReactNode }) {
  const [session] = useState(() => new RealtimeSession(userId))
  useEffect(() => { session.start(); return () => { void session.stop() } }, [session])
  return <RealtimeContext.Provider value={session}>{children}</RealtimeContext.Provider>
}
export function RealtimeProvider({ children }: { children: ReactNode }) {
  const { user } = useAuth()
  return user ? <SessionConnection key={user.id} userId={user.id}>{children}</SessionConnection>
    : <RealtimeContext.Provider value={null}>{children}</RealtimeContext.Provider>
}
