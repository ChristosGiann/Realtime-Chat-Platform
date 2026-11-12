# Durable delivery progress — #81

Κάθε `ConversationMember` διαθέτει nullable `LastDeliveredMessageId` και νέο
server-generated `MembershipId`. PostgreSQL είναι source of truth. Delivery
σημαίνει ότι ο authorized client επιβεβαίωσε την παρατήρηση ολόκληρου του durable
prefix μέχρι το anchor· ούτε SignalR SendAsync ούτε connection success αλλάζει D.

## API και compatibility

`GET /api/conversations/{id}/delivery-state` επιστρέφει μόνο την κατάσταση του
τρέχοντος authenticated μέλους:

```json
{
  "membershipId": "guid",
  "lastDeliveredMessageId": null,
  "lastDeliveredAt": null
}
```

`PUT` στο ίδιο endpoint απαιτεί το υπάρχον session cookie, CSRF token και
`{ "messageId": "guid", "membershipId": "guid" }`. Το `membershipId` λαμβάνεται
από φρέσκο authorized snapshot· δεν είναι actor ID. Ο actor προκύπτει από το
server principal. Άγνωστα JSON fields, όπως `userId`, απορρίπτονται.

Η σειρά είναι `(CreatedAt, Id)` όπως ακριβώς στην PostgreSQL. Duplicate/παλαιότερο
ACK επιστρέφει την τρέχουσα κατάσταση χωρίς regression. Foreign/missing message
δίνει 404, non-member 404, anonymous 401, παλιά membership incarnation 409.
Τα `LastDeliveredAt` και υπάρχον `LastReadAt` είναι **CreatedAt των anchors**,
όχι χρόνος ανθρώπινης παράδοσης/ανάγνωσης.

Το read endpoint διατηρεί τη response shape και το legacy `{messageId}` request.
Δέχεται επιπλέον προαιρετικό `membershipId`: όταν υπάρχει ελέγχεται πριν τη mutation.
Το νέο delivery request το απαιτεί. Legacy read request χωρίς precondition
εφαρμόζεται στη membership που υπάρχει όταν εκτελείται, όπως πριν. Ο νέος client
των επόμενων issues πρέπει να στέλνει το incarnation και να ακυρώνει παλιά queues.

## Persistence και Read ⇒ Delivered

Η migration προσθέτει composite same-conversation FK από D προς Messages, με
restrict delete, και σχετικό index. Το MembershipId είναι μοναδικό, non-empty,
με PostgreSQL UUID default και νέα τιμή σε κάθε membership insertion.
Το υπάρχον composite primary key `(ConversationId, UserId)` παραμένει ίδιο.

Οι read και delivery mutations χρησιμοποιούν το ίδιο conversation row lock και
transaction με membership/send. Το κοινό `ReceiptProgress` προχωρά R όπου ζητείται,
και έπειτα προχωρά D τουλάχιστον στο μέγιστο μεταξύ του requested anchor και R.
Η συναλλαγή δημοσιεύει ατομικά το συνεπές αποτέλεσμα ή κάνει rollback και τις δύο
μεταβολές. Η σύγκριση γίνεται με PostgreSQL tuple ordering και κρατά microseconds.

Η migration κάνει backfill `D = R` στα ήδη αναγνωσμένα prefixes. Κενό R διατηρεί
κενό D. Οι νέες mutations επιδιορθώνουν επίσης read floor από legacy writes.
Η cross-table ανισότητα R ≤ D υλοποιείται από την κοινή transactional υπηρεσία,
όχι από CHECK που θα απαιτούσε ανάγνωση άλλου πίνακα. Απευθείας SQL/παλιός writer
εκτός αυτής της υπηρεσίας δεν αποκτά αυτόματα monotonic receipt guarantees.

Leave/removal διαγράφει την membership μαζί με R/D. Rejoin έχει νέο MembershipId,
R=null και D=null, με το ίδιο ορατό ιστορικό όπως προηγουμένως. Το ID επιτρέπει
στα #82/#83 να απορρίπτουν stale receipt snapshots/events αντί να κάνουν max
προόδου διαφορετικών membership incarnations. Downgrade αφαιρεί D/incarnations
χωρίς να διαγράφει ιστορικό/R· επόμενο upgrade ξανακάνει backfill με νέα IDs.

## Έλεγχοι και όρια

Πραγματική PostgreSQL/Testcontainers επαληθεύει migration upgrade/downgrade,
read backfill, ανεξαρτησία μελών, concurrent read/delivery, tuple ties, FK/restrict,
unique/non-empty incarnation, rollback, legacy read floor, spoofing/CSRF και rejoin.
Τα υπάρχοντα read/unread tests διατηρούνται. Δεν υπάρχουν receipt broadcasts ή
automatic client ACK στο #81· αυτά ανήκουν στα #82 και #83 αντίστοιχα.
Η επιβεβαίωση είναι δήλωση του client, όχι απόδειξη ότι άνθρωπος διάβασε περιεχόμενο.
