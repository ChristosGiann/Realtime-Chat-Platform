import { ApiError, errorMessage, mutate, request } from './client'
import type { PublicProfile } from './profiles'
export type Conversation = { id: string; type: 'Direct' | 'Group'; name: string | null; createdAt: string; myRole: 'Owner' | 'Member'; memberCount: number }
export type Member = { userId: string; role: 'Owner' | 'Member'; joinedAt: string; profile: PublicProfile | null }
export type ConversationPage = { items: Conversation[]; nextOffset: number | null }
export type Details = Conversation & { members: Member[]; nextOffset: number | null }
const path = (id: string) => `/api/conversations/${encodeURIComponent(id)}`
export const conversationsApi = {
  list: (offset = 0, signal?: AbortSignal) => request<ConversationPage>(`/api/conversations?offset=${offset}&limit=20`, { signal }, true),
  details: (id: string, offset = 0, signal?: AbortSignal) => request<Details>(`${path(id)}?offset=${offset}&limit=20`, { signal }, true),
  direct: (targetUserId: string) => mutate<{ id: string }>('/api/conversations/direct', 'POST', { targetUserId }),
  group: (name: string, memberIds: string[]) => mutate<{ id: string }>('/api/conversations/group', 'POST', { name, memberIds }),
  add: (id: string, userId: string) => mutate<void>(`${path(id)}/members/${encodeURIComponent(userId)}`, 'PUT'),
  remove: (id: string, userId: string) => mutate<void>(`${path(id)}/members/${encodeURIComponent(userId)}`, 'DELETE'),
  leave: (id: string) => mutate<void>(`${path(id)}/membership`, 'DELETE'),
}
export function conversationError(cause: unknown) {
  if (cause instanceof ApiError) {
    if (cause.status === 404) return 'Η συνομιλία ή ο χρήστης δεν είναι διαθέσιμος. Ανανέωσε τη λίστα.'
    if (cause.status === 403) return 'Δεν έχεις δικαίωμα για αυτή την ενέργεια.'
    if (cause.status === 409) return 'Η ενέργεια δεν επιτρέπεται στην τρέχουσα κατάσταση. Ο ιδιοκτήτης δεν μπορεί να αποχωρήσει ή να αφαιρεθεί.'
  }
  return errorMessage(cause)
}
