import { ApiError, errorMessage, mutate, request } from './client'

export type Message = { id: string; clientMessageId: string; conversationId: string; senderId: string; body: string; createdAt: string }
export type HistoryMessage = Message & { sender: { userId: string; displayName: string | null; avatarUrl: string | null } }
export type HistoryPage = { items: HistoryMessage[]; nextCursor: string | null }
export type ReadState = { lastReadMessageId: string | null; lastReadAt: string | null; unreadCount: number }
export type SendRequest = { clientMessageId: string; body: string }
const path = (id: string) => `/api/conversations/${encodeURIComponent(id)}`
export const messagesApi = {
  history: (id: string, cursor: string | null = null, signal?: AbortSignal) => request<HistoryPage>(`${path(id)}/messages?limit=30${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`, { signal }, true),
  send: (id: string, body: SendRequest) => mutate<Message>(`${path(id)}/messages`, 'POST', body),
  readState: (id: string, signal?: AbortSignal) => request<ReadState>(`${path(id)}/read-state`, { signal }, true),
  advance: (id: string, messageId: string, membershipId?: string) => mutate<ReadState>(`${path(id)}/read-state`, 'PUT', { messageId, membershipId }),
}

// Follow stable REST cursors until the last synchronized durable anchor (or the end).
export async function recoverMessages(id: string, anchor: string | null, signal: AbortSignal): Promise<HistoryPage> {
  const items: HistoryMessage[] = []
  let cursor: string | null = null
  do {
    const page = await messagesApi.history(id, cursor, signal)
    items.push(...page.items); cursor = page.nextCursor
    if (anchor && page.items.some(item => item.id === anchor)) break
  } while (cursor && !signal.aborted)
  return { items, nextCursor: cursor }
}
export function messagingError(error: unknown) {
  if (error instanceof ApiError) {
    if (error.status === 404 || error.status === 403) return 'Δεν έχεις πλέον πρόσβαση σε αυτή τη συνομιλία ή το μήνυμα δεν είναι διαθέσιμο.'
    if (error.status === 409) return 'Το αναγνωριστικό αποστολής χρησιμοποιήθηκε για άλλο μήνυμα. Ανανέωσε το ιστορικό πριν συνεχίσεις.'
    if (error.status === 400) return 'Το μήνυμα ή η σελίδα ιστορικού δεν είναι έγκυρη. Έλεγξε το κείμενο ή ανανέωσε το ιστορικό.'
  }
  return errorMessage(error)
}
