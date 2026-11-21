import type { RefObject } from 'react'
import type { Details, Member } from '../api/conversations'
import type { PublicProfile } from '../api/profiles'
import { UserPicker } from './UserPicker'

export type MembershipConfirmation = { kind: 'remove'; userId: string; name: string } | { kind: 'leave' }

type Props = {
  details: Details | null
  busy: boolean
  loading: boolean
  error: string
  unavailable: boolean
  confirmation: MembershipConfirmation | null
  confirmRef: RefObject<HTMLButtonElement | null>
  onRefresh: () => void
  onMore: (offset: number) => void
  onAdd: (person: PublicProfile) => void
  onRemove: (member: Member, trigger: HTMLButtonElement) => void
  onLeave: (trigger: HTMLButtonElement) => void
  onConfirm: () => void
  onCancel: () => void
}

const memberName = (member: Member) => member.profile?.displayName ?? member.userId
const roleName = (role: 'Owner' | 'Member') => role === 'Owner' ? 'Ιδιοκτήτης' : 'Μέλος'
const createdDate = (date: string | undefined) => {
  if (!date) return null
  const parsed = new Date(date)
  return Number.isNaN(parsed.getTime()) ? null : new Intl.DateTimeFormat('el-GR', { day: 'numeric', month: 'long', year: 'numeric' }).format(parsed)
}

export function ConversationDetailsPanel({ details, busy, loading, error, unavailable, confirmation, confirmRef,
  onRefresh, onMore, onAdd, onRemove, onLeave, onConfirm, onCancel }: Props) {
  const canRemove = details?.type === 'Group' && details.myRole === 'Owner'
  const canLeave = details?.type === 'Group' && details.myRole === 'Member'
  const validConfirmation = confirmation?.kind === 'remove'
    ? canRemove && details?.members.some(member => member.userId === confirmation.userId && member.role !== 'Owner')
    : confirmation?.kind === 'leave' && canLeave
  const created = details ? createdDate(details.createdAt) : null
  return <>
    {loading && <p className="details-notice" role="status">Φόρτωση στοιχείων…</p>}
    {busy && !loading && <p className="details-notice" role="status">Αποθήκευση αλλαγής…</p>}
    {error && <p className="details-notice details-notice--error" role="alert">{error}</p>}
    {unavailable && <p className="details-notice">Τα στοιχεία και οι ενέργειες αυτής της συνομιλίας δεν είναι πλέον διαθέσιμα.</p>}
    {!details && !loading && !unavailable && <div className="details-empty"><p>Δεν είναι διαθέσιμα στοιχεία συνομιλίας.</p><button disabled={busy} onClick={onRefresh}>Δοκιμή ξανά</button></div>}
    {details && !unavailable && <>
      <section className="details-overview" aria-label="Σύνοψη συνομιλίας">
        <p className="eyebrow">{details.type === 'Group' ? 'Ομάδα' : 'Προσωπική συνομιλία'}</p>
        <p className="details-title">{details.name ?? 'Direct συνομιλία'}</p>
        <div className="details-facts">
          {details.type === 'Group' && <span className="role-badge" aria-label={`Ο ρόλος σου: ${roleName(details.myRole)}`}>{roleName(details.myRole)}</span>}
          <span>{details.memberCount} μέλη</span>
        </div>
        {created && <p className="details-created">Δημιουργήθηκε {created}</p>}
      </section>
      <section className="details-section" aria-labelledby="details-members-title"><div className="details-section-header"><h3 id="details-members-title">Μέλη</h3><span>{details.memberCount}</span></div>
        {details.members.length === 0 ? <p className="details-empty">Δεν υπάρχουν μέλη σε αυτή τη σελίδα.</p> : <ul className="member-list">{details.members.map(member => <li key={member.userId} className="member-row">
          <div className="member-identity"><a href={`#/profiles/${member.userId}`}>{memberName(member)}</a><span className="role-badge role-badge--member">{roleName(member.role)}</span></div>
          {canRemove && member.role !== 'Owner' && <button className="member-remove" disabled={busy || !!confirmation} onClick={event => onRemove(member, event.currentTarget)} aria-label={`Αφαίρεση ${memberName(member)}`}>Αφαίρεση</button>}
        </li>)}</ul>}
        {details.nextOffset !== null && <button className="details-secondary" disabled={busy} onClick={() => onMore(details.nextOffset!)}>Περισσότερα μέλη</button>}
      </section>
      {canRemove && <section className="details-section details-add" aria-labelledby="details-add-title"><h3 id="details-add-title">Προσθήκη μέλους</h3>
        <p className="details-section-help">Βρες άτομα για αυτή την ομάδα.</p>
        <UserPicker disabled={busy || !!confirmation} excluded={details.members.map(member => member.userId)} onSelect={onAdd} />
        <p className="hint">Ο ιδιοκτήτης δεν μπορεί να αποχωρήσει ή να αφαιρεθεί.</p></section>}
      {canLeave && <section className="details-section details-leave" aria-label="Αποχώρηση από ομάδα"><h3>Η συμμετοχή σου</h3>
        <p className="details-section-help">Μπορείς να αποχωρήσεις από αυτή την ομάδα.</p>
        <button className="details-danger" disabled={busy || !!confirmation} onClick={event => onLeave(event.currentTarget)}>Αποχώρηση από ομάδα</button></section>}
      {confirmation && validConfirmation && <div className="membership-confirmation" role="group" aria-label={confirmation.kind === 'remove' ? `Επιβεβαίωση αφαίρεσης ${confirmation.name}` : 'Επιβεβαίωση αποχώρησης'}>
        <h3>{confirmation.kind === 'remove' ? `Αφαίρεση ${confirmation.name};` : 'Αποχώρηση από την ομάδα;'}</h3>
        <p>{confirmation.kind === 'remove' ? `${confirmation.name} θα χάσει την πρόσβαση σε αυτή τη συνομιλία.` : 'Θα χάσεις την πρόσβαση σε αυτή τη συνομιλία.'}</p>
        <div className="membership-confirmation-actions"><button className="details-secondary" disabled={busy} onClick={onCancel}>Ακύρωση</button>
          <button ref={confirmRef} className="details-danger" disabled={busy} onClick={onConfirm}>{confirmation.kind === 'remove' ? 'Επιβεβαίωση αφαίρεσης' : 'Επιβεβαίωση αποχώρησης'}</button></div>
      </div>}
    </>}
  </>
}
