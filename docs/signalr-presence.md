# Εξουσιοδοτημένη παρουσία συνομιλίας — #79

Η παρουσία είναι ephemeral, single-instance projection του `ConversationConnections`.
Online σημαίνει τουλάχιστον μία γνωστή, authenticated και μη ληγμένη σύνδεση του
χρήστη στην API instance. Δεν σημαίνει ότι ο χρήστης βλέπει κάποια συνομιλία.
Δεν προστίθεται PostgreSQL state, migration, LastSeen ή global presence endpoint.

## Συμβόλαια

Ο authenticated hub εκθέτει `GetPresence(conversationId, offset, limit)` με
`offset >= 0` και `1 <= limit <= 100`. Επιστρέφει:

```json
{
  "conversationId": "guid",
  "items": [{ "userId": "guid", "isOnline": true }],
  "nextOffset": null,
  "version": 42
}
```

Η σελίδα έχει σταθερή PostgreSQL ταξινόμηση UserId και περιλαμβάνει μόνο τρέχοντα
μέλη. Ο παρατηρητής πρέπει να είναι μέλος· άγνωστη/απαγορευμένη συνομιλία δίνει
το ίδιο generic hub error. Snapshot δεν απαιτεί Subscribe. Για live ενημερώσεις
χρειάζεται το υπάρχον authorized Subscribe. Ο εμφανιζόμενος χρήστης μπορεί να
είναι online στην αρχική σελίδα χωρίς να είναι subscribed στη συνομιλία.

Το `PresenceChanged` περιέχει `{ conversationId, userId, isOnline, version }`.
Η πρώτη σύνδεση δίνει online, η τελευταία έγκυρη σύνδεση που κλείνει δίνει offline.
Πρόσθετα tabs δεν δημιουργούν δεύτερο logical online transition. Αργές αλλαγές
που έχουν ήδη αντικατασταθεί από νεότερη κατάσταση δεν δημοσιεύονται.

Το `version` είναι instance-local monotonic watermark, όχι timestamp, αριθμός
συνδέσεων ή durable receipt. Snapshot supersedes παλαιότερα events για τους
χρήστες της συγκεκριμένης σελίδας. Consumer κρατά version ανά χρήστη και
αγνοεί παλαιότερες ενημερώσεις. Δεν χρησιμοποιεί watermark μιας σελίδας για να
απορρίψει events χρηστών άλλης σελίδας. Reconnect/restart απαιτεί νέο snapshot
και reset του ephemeral cache/version. Η UI ενσωμάτωση ανήκει στο #83.

## Authorization και lifecycle

Snapshot, subscription, membership mutation και presence publication μοιράζονται
το conversation row lock. Κάθε publication επανελέγχει ότι ο εμφανιζόμενος
χρήστης παραμένει μέλος και στέλνει μόνο σε ενεργές subscriptions τρεχόντων μελών.
Αφαίρεση μέλους αποκλείει μελλοντική έκθεση τόσο ως παρατηρητή όσο και ως subject.
Rejoin δεν επαναφέρει αυτόματα παλιά subscription.

Τα hub connect/invocations διατηρούν την υπάρχουσα server-side session validation.
Γνωστή invalidation αφαιρεί τη σύνδεση από το registry πριν επιστρέψει το error.
Τα ticket expiry deadlines αποκλείουν ληγμένες συνδέσεις από snapshots και
recipients ακόμη και αν δεν έχει ολοκληρωθεί το transport cleanup.
Το `CloseOnAuthenticationExpiration` παραμένει ενεργό.

Το production `ClientTimeoutInterval` είναι 30 δευτερόλεπτα και το server
`KeepAliveInterval` 15 δευτερόλεπτα. Δεν υπάρχει επιπλέον application grace period.
Abrupt disconnect ανιχνεύεται στο επόμενο SignalR timeout/heartbeat check μετά
την περίοδο σιωπής· δεν υπόσχεται ακριβή φυσική αποσύνδεση στο δευτερόλεπτο 30.
Οι δοκιμές επαληθεύουν τη ρύθμιση 30s και εκτελούν πραγματικό silent-client
timeout με συντομευμένη test-only τιμή 1s.

Το logout του τρέχοντος React tab σταματά την κοινή σύνδεση (#78). Παραμένει
η υπάρχουσα πολιτική cookie logout: δεν εισάγεται global sign-out/revocation
όλων των tabs ή περιοδικός DB έλεγχος idle WebSockets. Security stamp/deletion
αναγνωρίζεται στους υπάρχοντες ελέγχους, όχι με νέα συνεχή revalidation.

## Ανθεκτικότητα και έλεγχοι

Snapshot ανακτά την τρέχουσα κατάσταση μετά από χαμένο event. Αποτυχία ephemeral
publication καταγράφεται και δεν εμποδίζει cleanup ή μεταβάλλει durable δεδομένα.
Τα connection records και τα τελευταία presence versions μένουν μόνο στη μνήμη·
restart τα μηδενίζει. Τα τελευταία user versions διατηρούνται για τη ζωή της
instance ώστε να απορρίπτονται παλιές concurrent publications.

PostgreSQL/Testcontainers και πραγματικοί SignalR clients ελέγχουν multi-tab
aggregation, pagination, non-members, subject χωρίς subscription, revocation
παρατηρητή/subject, snapshot που περιμένει membership lock, invalid security
stamp, ticket expiration και silent-client timeout. Typing/receipts και
Redis/backplane παραμένουν εκτός του #79.
