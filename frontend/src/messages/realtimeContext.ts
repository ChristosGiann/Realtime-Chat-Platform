import { createContext, useContext } from 'react'
import type { RealtimeSession } from './RealtimeSession'

export const RealtimeContext = createContext<RealtimeSession | null>(null)
export const useRealtime = () => useContext(RealtimeContext)
