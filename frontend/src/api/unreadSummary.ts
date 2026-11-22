import { mutate } from './client'

export type UnreadSummary = {
  fromReadMessageId: string | null
  throughMessageId: string
  throughCreatedAt: string
  unreadMessagesSummarized: number
  overview: string
  keyPoints: string[]
  actionItems: string[]
  unresolvedQuestions: string[]
}

export const unreadSummaryApi = {
  create: (id: string, signal?: AbortSignal) =>
    mutate<UnreadSummary>(`/api/conversations/${encodeURIComponent(id)}/unread-summary`, 'POST', undefined, true, signal),
}
