export type CurrentUser = {
  id: string; userName: string; email: string
  displayName: string | null; bio: string | null; avatarUrl: string | null
}
export type Registration = { email: string; userName: string; password: string; displayName?: string }
type Problem = { errors?: Record<string, string[]> }
export class ApiError extends Error {
  status: number
  fields: Record<string, string[]>
  constructor(status: number, fields: Record<string, string[]> = {}) {
    super(status === 401 ? 'Απαιτείται σύνδεση.' : status === 409 ? 'Το email ή το όνομα χρήστη χρησιμοποιείται ήδη.'
      : status === 400 ? 'Έλεγξε τα στοιχεία της φόρμας και δοκίμασε ξανά.'
      : status === 404 ? 'Το προφίλ δεν βρέθηκε.' : 'Η υπηρεσία δεν είναι διαθέσιμη. Δοκίμασε ξανά.')
    this.status = status
    this.fields = Object.fromEntries(Object.entries(fields).map(([key, value]) => [key.toLowerCase(), value]))
  }
}
export function errorMessage(error: unknown) {
  return error instanceof ApiError ? error.message : 'Δεν ήταν δυνατή η επικοινωνία με τον server. Δοκίμασε ξανά.'
}
const baseUrl = (import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5080').replace(/\/$/, '')
export async function request<T>(path: string, init: RequestInit = {}, protectedRequest = false): Promise<T> {
  const response = await fetch(`${baseUrl}${path}`, { ...init, credentials: 'include', cache: 'no-store' })
  if (!response.ok) {
    if (response.status === 401 && protectedRequest) window.dispatchEvent(new Event('session-expired'))
    const problem = await response.json().catch(() => ({})) as Problem
    // Only field validation is shown; never expose server details or stack traces.
    throw new ApiError(response.status, response.status === 400 || response.status === 409 ? problem.errors : {})
  }
  return response.status === 204 ? undefined as T : response.json() as Promise<T>
}
export async function mutate<T>(path: string, method: string, body?: unknown, protectedRequest = true): Promise<T> {
  // Obtain a fresh identity-bound token for each mutation, including after login.
  const { requestToken } = await request<{ requestToken: string }>('/api/auth/csrf')
  return request<T>(path, { method, headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': requestToken },
    body: body === undefined ? undefined : JSON.stringify(body) }, protectedRequest)
}
export const authApi = {
  me: () => request<CurrentUser>('/api/auth/me'),
  register: (body: Registration) => request<{ id: string; userName: string; email: string; displayName: string }>('/api/auth/register',
    { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }),
  login: (email: string, password: string) => mutate<void>('/api/auth/login', 'POST', { email, password }, false),
  logout: () => mutate<void>('/api/auth/logout', 'POST'),
}
