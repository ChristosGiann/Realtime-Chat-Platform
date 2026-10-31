# Δημιουργία Group συνομιλίας — issue #27

Το `POST /api/conversations/group` δημιουργεί group με ασφαλές session και
`X-CSRF-TOKEN`, σύμφωνα με το υπάρχον Identity/authorization architecture.

```json
{
  "name": "Ομάδα ανάπτυξης",
  "memberIds": ["11111111-1111-1111-1111-111111111111"]
}
```

## Validation και αρχική σύνθεση

- Το name είναι υποχρεωτικό, όχι whitespace-only, με μέγιστο μήκος 100 UTF-16
  code units πριν το trimming, όπως τα υπάρχοντα StringLength contracts.
  Αποθηκεύεται χωρίς εξωτερικά κενά. Δεν απαιτείται μοναδικότητα ονόματος.
- Το memberIds είναι υποχρεωτικό array από μη κενά Guid. Κενό array επιτρέπεται:
  δημιουργείται group μόνο με τον Owner, καθώς το issue δεν ορίζει ελάχιστο πλήθος.
- Διπλά IDs στο request απορρίπτονται με 400, χωρίς writes. Αν ο δημιουργός υπάρχει
  μία φορά στη λίστα, παραμένει μία membership εγγραφή με ρόλο Owner.
- Ο δημιουργός προκύπτει αποκλειστικά από ICurrentUser. Όλοι οι άλλοι χρήστες
  αποκτούν Member role. Άγνωστα JSON πεδία απορρίπτονται: ο client δεν επιλέγει
  owner, roles, conversation ID, type ή timestamps.
- Η ύπαρξη χρηστών ελέγχεται στο Identity, όχι στα Profiles. Ένα ή περισσότερα
  άγνωστα IDs επιστρέφουν ενιαίο 404 χωρίς μερική δημιουργία.

## Αποθήκευση και απόκριση

Μία Conversation με Type=Group και null direct pair, η Owner membership και όλα
τα αρχικά μέλη εισάγονται με ένα SaveChangesAsync. Η αυτόματη EF transaction
εγγυάται πλήρες rollback σε αποτυχία. PostgreSQL PK/FKs και το unique owner index
διατηρούν την ακεραιότητα. Τα CreatedAt/JoinedAt χρησιμοποιούν τα υπάρχοντα UTC
database defaults. Δεν αλλάζει το schema ούτε χρειάζεται migration.

Αν χρήστης διαγραφεί μετά το validation, το συγκεκριμένο membership user FK
προκαλεί rollback. Επανελέγχεται ο δημιουργός: αν λείπει επιστρέφεται 401,
διαφορετικά 404 για μη διαθέσιμο αρχικό μέλος. Άλλα database failures δεν
μετατρέπονται σε επιτυχία.

Η απόκριση 201 περιέχει μόνο `id`, `type` (Group), `name`, `createdAt` και
`members` ταξινομημένα κατά UserId. Κάθε member περιέχει μόνο `userId`, `role`
(Owner/Member), `joinedAt`. Δεν εκτίθενται Identity entities ή ιδιωτικά πεδία.
Οι αποκρίσεις είναι no-store. Δεν δίνεται Location header προς μη υλοποιημένο GET.

| Status | Σημασία |
| --- | --- |
| 201 | Νέο group με Owner και όλα τα αρχικά μέλη |
| 400 | Άκυρο body/name/IDs, duplicates ή άκυρο CSRF |
| 401 | Anonymous ή μη υπαρκτός δημιουργός |
| 404 | Μη υπαρκτό αρχικό μέλος |

Τα errors χρησιμοποιούν ProblemDetails/ValidationProblemDetails. Κάθε επιτυχές
POST δημιουργεί διαφορετικό group, ακόμη και με ίδιο όνομα και σύνθεση. Δεν
υπάρχει idempotency/reuse contract όπως στις Direct συνομιλίες.

## Tests και όρια

Τα GroupConversationTests χρησιμοποιούν πραγματική PostgreSQL 18/Testcontainers
και καλύπτουν DTO, UTC, Owner/Member persistence, creator στη λίστα, owner-only
groups, επαναλαμβανόμενα ονόματα, unknown/duplicate/invalid IDs, validation ονόματος,
authentication/CSRF, missing profile και διαγραμμένο δημιουργό.

Το rollback test εισάγει προσωρινό CHECK μόνο στην test database που απορρίπτει
Member INSERT. Επιβεβαιώνει ότι δεν μένουν ούτε group ούτε memberships και αφαιρεί
το constraint σε finally. Δεν χρησιμοποιούνται EF InMemory ή SQLite.

```powershell
dotnet build RealtimeChat.sln --configuration Release --no-restore
dotnet test RealtimeChat.sln --configuration Release --no-build
dotnet ef migrations has-pending-model-changes --project src/Chat.Api --configuration Release --no-build
```

Editing metadata, προσθήκη/αφαίρεση μελών, ownership transfer, γενικές conversation
policies, list/details, frontend, messages και SignalR παραμένουν στα επόμενα issues.
Η ροή creation εγγυάται έναν Owner, αλλά δεν αλλάζει τον τεκμηριωμένο περιορισμό
του #25 για ασυνεπή cross-row δεδομένα από απευθείας SQL writes.
