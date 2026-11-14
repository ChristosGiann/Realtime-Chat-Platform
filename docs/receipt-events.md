# Durable receipt snapshots και events — #82

PostgreSQL παραμένει η μοναδική durable πηγή. Τα delivery/read PUTs χρησιμοποιούν
το κοινό transaction του #81. Μόνο πραγματική αλλαγή και επιτυχημένο commit
προκαλούν `ReceiptAdvanced`. Same/older ACKs δεν εκπέμπουν transition.
Read προχωρά ατομικά και το delivery floor. Αποτυχία SQL/commit δεν εκπέμπει event.

Ο publisher ανοίγει ανεξάρτητο DbContext, ξαναπαίρνει το conversation row lock,
ελέγχει την ίδια `MembershipId` και διαβάζει το πιο πρόσφατο ζεύγος R/D. Αν η
membership αφαιρέθηκε/αντικαταστάθηκε, δεν δημοσιεύει την παλιά incarnation.
Recipients είναι μόνο ενεργές subscriptions τρεχόντων μελών. Το ίδιο lock με
membership mutations αποκλείει broadcast που προσπερνά επιτυχημένη revocation.
Αποτυχία ή timeout notification (5s) καταγράφεται χωρίς να αναιρεί REST success.

## Contract

`ReceiptAdvanced`:

```json
{
  "conversationId": "guid",
  "member": {
    "userId": "guid",
    "membershipId": "guid",
    "delivered": { "messageId": "guid", "createdAt": "UTC timestamp" },
    "read": null
  }
}
```

Κάθε anchor είναι nullable. Το timestamp είναι η δημιουργία του message, όχι ώρα
ανθρώπινης ανάγνωσης/παράδοσης. Δεν περιλαμβάνονται bodies, emails, Identity entities,
connection IDs ή client-supplied actor IDs. Παράλληλα commits μπορεί να συμπτυχθούν
στο ίδιο τελευταίο snapshot και να παράγουν duplicates. Ο receiver συγκρίνει
`(CreatedAt, Id)` με ακρίβεια PostgreSQL, όχι JavaScript milliseconds, μόνο εντός
ίδιας membership incarnation. Δεν κάνει max ανάμεσα σε παλιά και νέα membership.

`GET /api/conversations/{id}/receipts?offset=0&limit=100` απαιτεί current membership,
επιστρέφει no-store και περιλαμβάνει:

- `conversationId`, `conversationType` (`Direct`/`Group`), `myMembershipId`.
- `items`: per-member R/D όπως στο event, ταξινομημένα κατά PostgreSQL UserId.
- `nextOffset`: null στην τελευταία σελίδα. Limit 1–100, offset ≥0.
- `rosterVersion`: SHA-256 των current membership IDs, σταθερό όσο το cohort δεν αλλάζει.

Ο client συνδυάζει σελίδες μόνο με ίδια rosterVersion και myMembershipId.
Αλλαγή cohort απαιτεί επανεκκίνηση της λήψης. Κάθε σελίδα είναι συνεπής υπό lock·
πολλαπλές σελίδες δεν αποτελούν ένα κοινό χρονικό snapshot της προόδου. Monotonic
merge για την ίδια incarnation διατηρεί νεότερα live updates. Άγνωστη incarnation
οδηγεί σε νέο REST snapshot, όχι αυθαίρετη εισαγωγή μέλους. Reconnect αντικαθιστά
το cohort από REST και ανακτά missed updates χωρίς replay.

## Membership invalidation

Μετά από committed add/remove/leave εκπέμπεται `ConversationMembershipChanged`
με μόνο conversationId στους current subscribed members. Αποτελεί εντολή για
φρέσκο authorized snapshot των receipt/presence views, όχι νέο durable state.
Removal στέλνει `ConversationAccessRevoked {conversationId, membershipId}` στις
ενεργές connections του αφαιρεμένου χρήστη, ακόμη και αφού ανακληθεί το group.
Ο receiver καθαρίζει μόνο την αντίστοιχη incarnation· delayed revoke δεν πρέπει
να καθαρίσει νεότερο rejoin. No-op/failed membership changes δεν ειδοποιούν.

Ήδη queued bytes δεν ανακαλούνται. Τα events είναι best-effort και single-instance.
Μετά από χαμένη invalidation, reconnect ή επόμενο authorized REST request
αποκαθιστά πρόσβαση/cohort. Δεν υπάρχει durable replay, Redis, event persistence
ή UI implementation στο #82. Η React σύνθεση ανήκει στο #83.

## Έλεγχοι

Πραγματική PostgreSQL/Testcontainers και SignalR ελέγχουν post-commit visibility
από ανεξάρτητο context, rollback και μηδενικά events, transport failure με durable
success, no-op ACK, actor/CSRF isolation, missed-event REST recovery, pagination,
membership revocation και αποκλεισμό παλιάς incarnation μετά από rejoin.
Δεν αλλάζει EF model ή migration.
