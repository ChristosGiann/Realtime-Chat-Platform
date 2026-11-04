# Ρόλοι και εξουσιοδότηση συνομιλιών — issue #29

Το scoped `ConversationAuthorization` συγκεντρώνει τους κανόνες πρόσβασης Stage 2.
Χρησιμοποιεί ICurrentUser και PostgreSQL μέσω του ίδιου ChatDbContext. Δεν δέχεται
actor ID από τον caller και δεν διατηρεί cache αποφάσεων. Τα request IDs επιλέγουν
resource/target, ποτέ την ταυτότητα του actor. Platform Identity roles, headers και
query parameters δεν απονέμουν δικαιώματα συνομιλίας.

## Πίνακας δικαιωμάτων

| Συμμετοχή | Read | Add/Remove Member | Leave |
| --- | --- | --- | --- |
| Group Owner | Ναι | Ναι, χωρίς αφαίρεση Owner | Όχι, 409 |
| Group Member | Ναι | Όχι, 403 | Ναι |
| Direct Member | Ναι | Όχι, 409 | Όχι, 409 |
| Non-member / missing conversation | 404 | 404 | 404 |
| Anonymous / deleted Identity user | 401 | 401 | 401 |

Οι ρόλοι παραμένουν Member=1 και Owner=2 όπως στο #25. Ο Owner είναι μία membership
εγγραφή, όχι επιπλέον platform role. Direct Owner δεν είναι έγκυρος ρόλος πρόσβασης.
Άγνωστες ενέργειες απορρίπτονται. Δεν υπάρχουν admin/moderator ή message permissions.

## Χρήση από mutations

Ο ConversationMembershipController κρατά το FOR UPDATE lock του #28 και καλεί
`CheckAsync(conversationId, operation, targetUserId, cancellationToken)` μέσα στην
ίδια transaction, πριν το write. Το service ελέγχει Identity, membership, type,
ρόλο, owner protection και επιστρέφει απόφαση με status/title και server-derived
UserId. Ο controller διατηρεί μόνο validation CSRF, persistence και HTTP mapping.
Δεν υπάρχει πλέον αντίγραφο των κανόνων Owner/Member στον controller.

Το service δεν ανοίγει δική του transaction και δεν αποκτά lock: ο mutation caller
οφείλει να κρατά το conversation lock μέχρι το commit, όπως το υπάρχον endpoint.
Μία θετική απόφαση δεν είναι άδεια για μελλοντικό write έξω από αυτή τη συναλλαγή.
Creation flows #26/#27 επιλέγουν αρχική σύνθεση νέου resource και δεν απαιτούν
membership σε προϋπάρχουσα συνομιλία, επομένως διατηρούν τους δικούς τους ελέγχους.

## Χρήση από προστατευμένες αναγνώσεις

Το `CheckAsync(id, ConversationOperation.Read)` δίνει συνεπές 401/404 για τον caller.
Τα πραγματικά δεδομένα πρέπει να διαβάζονται με projection από
`ReadableConversations()`, ώστε ο έλεγχος χρήστη/membership/ρόλου και η ανάγνωση
να εκτελούνται στο ίδιο SQL statement. Δεν αρκεί ένα CheckAsync ακολουθούμενο από
ανεξάρτητο unfiltered DbSet read: στο ενδιάμεσο το μέλος μπορεί να αφαιρεθεί.

```csharp
var result = await authorization.ReadableConversations()
    .Where(x => x.Id == conversationId)
    .Select(x => new { x.Id, x.Type, x.Name })
    .SingleOrDefaultAsync(cancellationToken);
```

Η query παραμένει IQueryable για server-side projection/pagination του #30.
Η PostgreSQL επιλέγει ορατότητα κατά το statement snapshot· μια ανάγνωση που ήδη
εκτελείται μπορεί να ολοκληρωθεί πριν από concurrent removal. Επόμενες αναγνώσεις
μετά το committed removal δεν επιστρέφουν τη συνομιλία. Δεν υπόσχεται ανάκληση
δεδομένων που έχουν ήδη επιστραφεί.

## Tests και όρια

Τα tests χρησιμοποιούν PostgreSQL 18/Testcontainers, πραγματικά sessions και
test-only controller που καταχωρίζεται αποκλειστικά στο WebApplicationFactory.
Έτσι ελέγχονται προστατευμένα details χωρίς να προστεθεί production details/list
endpoint πριν το #30. Ελέγχεται επίσης ότι οι probe routes απουσιάζουν από την
κανονική εφαρμογή.

Καλύπτονται Group Owner/Member, Direct participants, outsider/anonymous, missing και
deleted resources/users, spoofed headers/query actor IDs, cross-conversation IDs,
πίνακας ενεργειών, unknown operation, removal/re-add/leave και anonymous scoped
service. Όλα τα tests του #28 παραμένουν ενεργά για πραγματικά mutation endpoints,
concurrency, rollback και αρνητικές διαδρομές.

Δεν αλλάζει το schema και δεν απαιτείται migration. Production list/details,
public-profile summaries, pagination, blocking, message-specific permissions και
platform roles παραμένουν εκτός scope.

Στο #41 προστίθεται ρητό SendMessage operation: επιτρέπεται σε Group Owner/Member
και Direct Member. Η αποστολή το ελέγχει μετά το conversation row lock μέσα στην
ίδια transaction με το message write. Οι υπόλοιποι κανόνες παραμένουν ως είχαν.
