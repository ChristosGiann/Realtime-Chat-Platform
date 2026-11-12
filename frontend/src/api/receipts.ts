import { mutate, request } from './client'
import { messagesApi } from './messages'

export type Anchor = { messageId: string; createdAt: string }
export type MemberReceipt = { userId: string; membershipId: string; delivered: Anchor | null; read: Anchor | null }
export type ReceiptSnapshot = { conversationId: string; conversationType: 'Direct' | 'Group'; rosterVersion: string; myMembershipId: string; items: MemberReceipt[] }
type Page = ReceiptSnapshot & { nextOffset: number | null }
const path = (id: string) => `/api/conversations/${encodeURIComponent(id)}`

export const receiptsApi = {
  async snapshot(id: string, signal: AbortSignal): Promise<ReceiptSnapshot> {
    for (let attempt = 0; attempt < 3; attempt++) {
      const first = await request<Page>(`${path(id)}/receipts?limit=100&offset=0`, { signal }, true)
      const items = [...first.items]
      let offset = first.nextOffset, valid = true
      while (offset !== null && !signal.aborted) {
        const page = await request<Page>(`${path(id)}/receipts?limit=100&offset=${offset}`, { signal }, true)
        if (page.rosterVersion !== first.rosterVersion || page.myMembershipId !== first.myMembershipId) { valid = false; break }
        items.push(...page.items)
        if (page.nextOffset !== null && page.nextOffset <= offset) throw new Error('Invalid receipt cursor')
        offset = page.nextOffset
      }
      signal.throwIfAborted()
      if (valid) return { ...first, items }
    }
    throw new Error('Membership is changing; retry snapshot')
  },
  deliver: (id: string, messageId: string, membershipId: string, signal: AbortSignal) =>
    mutate(`${path(id)}/delivery-state`, 'PUT', { messageId, membershipId }, true, signal),
}

// Observe the complete retained prefix after the persisted delivery floor. Do not ACK
// an isolated live event/latest page when older unseen messages may be missing.
export async function observePrefix(id: string, floor: string | null, signal: AbortSignal) {
  let cursor: string | null = null, newest: string | null = null
  do {
    const page = await messagesApi.history(id, cursor, signal)
    newest ??= page.items[0]?.id ?? null
    cursor = page.nextCursor
    if (floor && page.items.some(item => item.id === floor)) break
  } while (cursor !== null && !signal.aborted)
  signal.throwIfAborted()
  return newest
}

// Canonical UTC timestamps preserve PostgreSQL microseconds; UUID lexical order matches PostgreSQL.
export const anchorKey = (value: Anchor) => value.createdAt.slice(0, 19)
  + (value.createdAt.includes('.') ? value.createdAt.split('.')[1].replace('Z', '') : '').padEnd(7, '0') + value.messageId.toLowerCase()
export const covers = (anchor: Anchor | null, message: { id: string; createdAt: string }) =>
  anchor !== null && anchorKey(anchor) >= anchorKey({ messageId: message.id, createdAt: message.createdAt })
