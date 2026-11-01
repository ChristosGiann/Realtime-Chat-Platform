# Διαχείριση συμμετοχών — issue #28

Οι λειτουργίες απαιτούν authenticated session και έγκυρο CSRF token. Ο actor
προκύπτει από ICurrentUser, ποτέ από request body. Τα IDs στη διαδρομή προσδιορίζουν
συνομιλία και στόχο, όχι την ταυτότητα του χρήστη που εκτελεί την ενέργεια.

| Endpoint | Δικαίωμα / αποτέλεσμα |
| --- | --- |
| PUT /api/conversations/{id}/members/{userId} | Μόνο Group Owner, προσθήκη ως Member, 204 |
| DELETE /api/conversations/{id}/members/{userId} | Μόνο Group Owner, αφαίρεση Member, 204 |
| DELETE /api/conversations/{id}/membership | Group Member αποχωρεί ο ίδιος, 204 |

Το PUT υπάρχοντος μέλους είναι no-op 204 και δεν αλλάζει role ή JoinedAt. Το DELETE
απόντος στόχου από Owner είναι no-op 204. Ο Owner δεν αφαιρείται ούτε αποχωρεί (409).
Δεν υπάρχει ownership transfer σε αυτό το issue· η ομάδα διατηρεί τον Owner της.
Member που θέλει να φύγει χρησιμοποιεί το membership endpoint, όχι owner-only DELETE.

Αν ο actor δεν είναι πλέον μέλος, επιστρέφεται 404 όπως για ανύπαρκτη συνομιλία,
ώστε να μην αποκαλύπτεται η ύπαρξή της. Συνεπώς επαναλαμβανόμενο leave δίνει 404,
παρότι η τελική κατάσταση παραμένει η ίδια. Member που επιχειρεί owner-only ενέργεια
παίρνει 403. Direct συνομιλίες δεν μεταβάλλονται από κανένα από αυτά τα endpoints:
τα μέλη τους παίρνουν 409, outsiders 404. Anonymous/deleted actor παίρνει 401,
άκυρο CSRF ή κενό target Guid 400, άγνωστος χρήστης σε add 404. Τα errors είναι
ProblemDetails και οι αποκρίσεις no-store.

## Συναλλαγές και concurrency

Κάθε αλλαγή ανοίγει PostgreSQL transaction και αποκτά FOR UPDATE lock στη
Conversation πριν διαβάσει membership/permissions. Έτσι οι ροές add/remove/leave
της ίδιας συνομιλίας σειριοποιούνται και επανελέγχουν τα δικαιώματα στην τρέχουσα
κατάσταση. Διαφορετικές συνομιλίες χρησιμοποιούν διαφορετικό row lock.

Το lock κρατιέται έως commit/rollback, μαζί με το membership write. Ακόμη και
στα validation errors/no-ops, το disposal της transaction απελευθερώνει το lock.
Τα PK/FKs και το unique Owner index συνεχίζουν να προστατεύουν την PostgreSQL.
Concurrent διαγραφή του target χρήστη προκαλεί FK failure, rollback και 404.
Άλλα database failures δεν μετατρέπονται σε επιτυχία. Δεν χρησιμοποιούνται
in-process locks ή cache για την εξουσιοδότηση.

Το πρωτόκολλο lock αφορά τις mutation ροές του API. Μελλοντικές αλλαγές owner ή
διαγραφές συνομιλιών πρέπει να το ακολουθούν. Απευθείας SQL που παρακάμπτει το API
δεν καλύπτεται από τους domain κανόνες. Δεν αλλάζει το schema και δεν απαιτείται
migration. Νέες συμμετοχές παίρνουν JoinedAt από το υπάρχον UTC database default.

## Επαλήθευση και scope

Πραγματική PostgreSQL 18/Testcontainers: owner add/remove, duplicate add, repeated
remove, 12 παράλληλα adds/removes, leave, Owner protection, αρνητικά authorization
paths, cross-conversation IDs, immutable Direct membership, unknown/empty target,
anonymous/CSRF και forced database failure με rollback και επόμενη επιτυχή αίτηση.

Δεν προστίθενται invitations, blocking, notifications, messages ή ownership transfer.
Η συγκέντρωση των authorization κανόνων σε reusable service ανήκει στο #29.
