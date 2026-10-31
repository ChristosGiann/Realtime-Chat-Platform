import { mutate, request } from './client'
export type PublicProfile = { userId: string; displayName: string; bio: string | null; avatarUrl: string | null }
export type ProfileUpdate = Pick<PublicProfile, 'displayName' | 'bio' | 'avatarUrl'>
export type SearchPage = { items: PublicProfile[]; nextCursor: string | null }
export const profilesApi = {
  get: (id: string, signal?: AbortSignal) => request<PublicProfile>(`/api/profiles/${encodeURIComponent(id)}`, { signal }),
  update: (body: ProfileUpdate) => mutate<PublicProfile>('/api/profiles/me', 'PUT', body),
  search: (query: string, cursor?: string, signal?: AbortSignal) => {
    const parameters = new URLSearchParams({ query })
    if (cursor) parameters.set('cursor', cursor)
    return request<SearchPage>(`/api/users?${parameters}`, { signal }, true)
  },
}
