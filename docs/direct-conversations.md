# Δημιουργία Direct συνομιλίας — issue #26

## Συμβόλαιο

POST /api/conversations/direct απαιτεί AuthenticatedUser session και έγκυρο
X-CSRF-TOKEN, όπως οι υπόλοιπες μεταβολές του API. Το JSON body είναι:

```json
{ "targetUserId": "11111111-1111-1111-1111-111111111111" }
```

Ο initiator προκύπτει αποκλειστικά από το ICurrentUser. Το target ID είναι
υποχρεωτικό, μη κενό Guid. Άγνωστα JSON πεδία απορρίπτονται: ο client δεν επιλέγει
initiator, conversation ID, type ή membership role. Self-conversation απορρίπτεται.
Ο target ελέγχεται στο Identity, όχι στα Profiles: χρήστης χωρίς profile παραμένει
έγκυρος target. Δεν δημιουργούνται groups, μηνύματα ή SignalR events.

Η ασφαλής απόκριση περιέχει μόνο id, type ("Direct"), createdAt (UTC), memberIds
(δύο ταξινομημένα IDs). Δεν σειριοποιούνται Identity entities, email, password hashes
ή security stamps. Δεν υπάρχει ακόμη details GET endpoint· δεν κατασκευάζεται
Location header προς ανύπαρκτο resource route.

| Status | Σημασία |
| --- | --- |
| 201 | Δημιουργήθηκε νέα συνομιλία και δύο memberships |
| 200 | Επιστρέφεται η υπάρχουσα συνομιλία για το ίδιο ζεύγος |
| 400 | Άκυρο body, self-target ή άκυρο/missing CSRF |
| 401 | Anonymous/μη έγκυρη συνεδρία ή μη υπαρκτός initiator |
| 404 | Μη υπαρκτός target |
| 409 | Ασυνεπές υπάρχον membership ή concurrent αλλαγή που απαιτεί retry |

Τα errors είναι ProblemDetails/ValidationProblemDetails. Οι αποκρίσεις είναι no-store.
Η ίδια διαδρομή χρησιμοποιείται για τα δύο μέρη: Alice → Bob και Bob → Alice
επιλύονται στο ίδιο canonical ζεύγος και στο ίδιο conversation ID.

## Ατομικότητα και concurrency

Το ζεύγος ταξινομείται με Guid.CompareTo όπως στο persistence baseline #25.
Γίνεται αναζήτηση μόνο Direct με το συγκεκριμένο low/high pair. Υπάρχον Group,
ακόμη και με τους ίδιους χρήστες, δεν επαναχρησιμοποιείται.

Η νέα Conversation και δύο Member εγγραφές προστίθενται στο ίδιο DbContext και
αποθηκεύονται με ένα SaveChangesAsync. Η αυτόματη EF transaction εγγυάται ότι είτε
γράφονται όλα είτε τίποτα. CreatedAt/JoinedAt προέρχονται από τα database UTC defaults.
Δεν χρησιμοποιούνται locks στη μνήμη, Redis ή κατανεμημένη cache.

Σε ταυτόχρονες αιτήσεις, το unique index UX_Conversations_DirectPair αποφασίζει τον
νικητή. Μόνο το συγκεκριμένο PostgreSQL unique violation αντιμετωπίζεται ως race:
μετά το rollback καθαρίζεται το tracker και γίνεται νέα ανάγνωση του committed
νικητή. Ο νικητής παίρνει 201 και οι υπόλοιποι 200 με το ίδιο ID. Αν η εγγραφή έχει
στο μεταξύ διαγραφεί, επιστρέφεται 409 για retry. Άλλα database failures δεν
μετατρέπονται σιωπηρά σε επιτυχία.

Αν χρήστης διαγραφεί ανάμεσα σε validation και INSERT, τα συγκεκριμένα user FKs
αποτρέπουν orphan δεδομένα. Μετά το rollback επανελέγχεται initiator/target και
επιστρέφεται 401/404 αντίστοιχα ή 409 αν απαιτείται νέα προσπάθεια.

Πριν επιστραφεί υπάρχουσα συνομιλία ελέγχεται ότι έχει ακριβώς δύο memberships,
με τα σωστά IDs και Member role. Το #25 επιτρέπει απευθείας SQL writes με ασυνεπή
cross-row δεδομένα· εδώ αυτά επιστρέφουν 409 χωρίς έκθεση ή αυτόματη επιδιόρθωση.
Η εξουσιοδότηση αυτού του endpoint βασίζεται στην ταυτότητα του initiator και στο
pair που τον περιλαμβάνει. Δεν αποτελεί το γενικό authorization service του #29.

## Έλεγχοι

Τα DirectConversationTests χρησιμοποιούν πραγματική PostgreSQL 18/Testcontainers.
Καλύπτουν νέα δημιουργία, ακριβή DTO πεδία, persistence και UTC, επανάληψη/αντίστροφη
κατεύθυνση, 20 παράλληλα αιτήματα, anonymous/CSRF, self/unknown/invalid targets,
απαγόρευση επιπλέον πεδίων, missing profile, μη επαναχρησιμοποίηση Group, διαγραμμένο
initiator και ασυνεπή υπάρχουσα συνομιλία.

Για rollback εισάγεται προσωρινό test-only CHECK στο membership table, ώστε η βάση
να απορρίψει το child INSERT αφού εισαχθεί ο parent. Επιβεβαιώνεται ότι δεν παραμένει
ούτε Conversation ούτε membership και το CHECK αφαιρείται σε finally. Δεν αφορά
τη βάση ανάπτυξης ή τη migration του προϊόντος.

```powershell
dotnet build RealtimeChat.sln --configuration Release --no-restore
dotnet test RealtimeChat.sln --configuration Release --no-build
dotnet ef migrations has-pending-model-changes --project src/Chat.Api --configuration Release --no-build
```

Δεν αλλάζει το schema και δεν απαιτείται νέα migration. Group creation, membership
management, γενικές conversation policies, list/details queries, frontend, blocking,
messages και realtime delivery ανήκουν στα επόμενα issues.
