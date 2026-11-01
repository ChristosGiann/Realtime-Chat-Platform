# Conversation και membership persistence — issue #25

## Όρια και σχέση με Stage 1

Το PostgreSQL παραμένει source of truth. Προστίθενται δύο entities στο module
Chat.Api.Conversations και δύο πίνακες στο schema conversations, μέσα στο υπάρχον
ChatDbContext. Identity και Profiles διατηρούν τις υπάρχουσες ευθύνες και mappings.
Τα foreign keys αναφέρονται στο identity.AspNetUsers.Id, όχι στο δημόσιο profile.
Δεν προστίθενται endpoints, creation flows, membership mutations, authorization
services, messages ή SignalR. Τα entities δεν είναι API DTOs.

## Entities και αναπαράσταση

| Entity / πεδίο | Απόφαση |
| --- | --- |
| Conversation.Id | Guid / uuid, μη κενό primary key, δημιουργείται στην εφαρμογή, ValueGeneratedNever |
| Type | Direct=1, Group=2, αποθήκευση integer με CHECK επιτρεπόμενων τιμών |
| Name | Group: υποχρεωτικό με τουλάχιστον έναν μη-whitespace χαρακτήρα, έως 100 χαρακτήρες· Direct: null |
| CreatedAt | DateTime UTC / PostgreSQL timestamptz, υποχρεωτικό, default CURRENT_TIMESTAMP |
| DirectUserLowId / DirectUserHighId | Guid? FK προς Identity, ταξινομημένο ζεύγος μόνο για Direct |
| ConversationMember | Σύνθετο PK (ConversationId, UserId), χωρίς περιττό surrogate ID |
| Role | Member=1, Owner=2, integer με CHECK· δεν είναι platform-wide Identity role |
| JoinedAt | DateTime UTC / timestamptz, υποχρεωτικό, default CURRENT_TIMESTAMP |

Ο Owner είναι επίσης μέλος μέσω της ίδιας εγγραφής. Δεν χρειάζεται ξεχωριστό
CreatedBy/OwnerId στη Conversation για τα δηλωμένα κριτήρια Stage 2. Δεν προστίθεται
Admin/moderator role, επειδή τα #27–#29 ζητούν Owner/Member. Η ακριβής εξουσιοδότηση
θα εφαρμοστεί server-side στο #29. Δεν προστίθενται soft delete, leave-history,
UpdatedAt ή message-related timestamps πριν προκύψει σχετική απαίτηση.

## Direct pair και ακεραιότητα

Η Direct Conversation έχει Name=null, δύο υπαρκτά και διαφορετικά Identity user IDs
και DirectUserLowId < DirectUserHighId με τη σύγκριση uuid της PostgreSQL.
Το CHECK απορρίπτει αντίστροφη σειρά, self-pairs και ελλιπή ζεύγη. Ο μελλοντικός
caller πρέπει να ταξινομεί το ζεύγος πριν από την αποθήκευση. Η σύγκριση Guid.CompareTo
που χρησιμοποιείται στα tests επιβεβαιώνεται από την πραγματική PostgreSQL.
Δεν χρησιμοποιείται hash/string key, ώστε οι συμμετέχοντες να προστατεύονται με FKs.

Το unique partial index UX_Conversations_DirectPair στα δύο IDs με Type=1 αποτρέπει
διπλές συνομιλίες, ακόμη και σε ταυτόχρονες εγγραφές. Στο #26 η δημιουργία θα πρέπει
να αντιμετωπίζει unique conflicts και να επιστρέφει την υπάρχουσα συνομιλία.
Οι Groups έχουν και τα δύο Direct IDs null και δεν υπόκεινται σε μοναδικότητα ονόματος.

Το PK των memberships αποτρέπει δεύτερη συμμετοχή του ίδιου χρήστη στην ίδια
συνομιλία. Ένας χρήστης μπορεί να συμμετέχει σε πολλές συνομιλίες. Τα FKs αποτρέπουν
orphan memberships. Το partial unique index UX_ConversationMembers_Owner με Role=2
επιτρέπει το πολύ έναν Owner ανά συνομιλία. Μελλοντικό ownership transfer χρειάζεται
συναλλαγή που υποβιβάζει πρώτα τον παλιό Owner και μετά προάγει τον νέο.

## Ποιοι κανόνες μένουν στις επόμενες συναλλαγές

Τα παραπάνω είναι row-level constraints/uniqueness. Δεν υπάρχουν triggers ή
κυκλικά deferred FKs. Το schema επιτρέπει προσωρινά μια συνομιλία χωρίς memberships.
Τα #26–#29 πρέπει να επιβάλουν ατομικά:

- Direct: ακριβώς δύο memberships, με UserIds ίσα με το canonical pair και Role=Member.
- Group: ο δημιουργός εισάγεται ως Owner και υπάρχουν τα αρχικά μέλη πριν το commit.
- Κάθε ενεργό Group έχει Owner· το index επιβάλλει «το πολύ έναν», όχι «τουλάχιστον έναν».
- Direct membership/ρόλοι δεν τροποποιούνται από group-only λειτουργίες.
- Αποχώρηση/αφαίρεση Owner, authorization και αλλαγές ownership ακολουθούν τις
  αποφάσεις των αντίστοιχων issues και χρησιμοποιούν κατάλληλη συναλλαγή/κλείδωμα.
- Οι ενδεχόμενοι χρονικοί κανόνες μεταξύ JoinedAt και CreatedAt, καθώς και η
  αμεταβλητότητα Type/pair/IDs, θα ελέγχονται στις ροές μεταβολής.

Άρα ένα απευθείας SQL write μπορεί ακόμη να δημιουργήσει Direct με τρίτο μέλος ή
Owner, Group χωρίς Owner, ή συμμετοχή που δεν αντιστοιχεί στο direct pair. Αυτό είναι
ρητός περιορισμός του persistence baseline, όχι ολοκληρωμένη domain authorization.
Δεν εισάγουμε creation flow στο #25 για να καλύψουμε αυτά τα επόμενα ζητούμενα.

## UTC και timestamps

Το timestamptz αποθηκεύει χρονικό στιγμιότυπο, όχι το αρχικό timezone του χρήστη.
Το Npgsql διαβάζει DateTimeKind.Utc και απαιτεί UTC για μη-default DateTime writes.
Η session timezone αλλάζει την SQL εμφάνιση, όχι το αποθηκευμένο στιγμιότυπο.
Το CURRENT_TIMESTAMP είναι database/transaction time και εφαρμόζεται όταν η τιμή
παραλείπεται. Τα isfinite CHECKs αποκλείουν PostgreSQL infinity/-infinity.
Τα tests επιβεβαιώνουν defaults, ρητά UTC instants, διαφορετική session timezone,
απόρριψη μη-UTC από τον provider και infinity από τη βάση. Δεν χρησιμοποιούμε local time.

## Indexes και delete behavior

- PK Conversations(Id): σταθερή ταυτότητα/lookup.
- Unique partial DirectPair(low, high): αντιστοίχιση/μοναδικότητα Direct ζεύγους.
- Index DirectUserHighId: δημιουργείται για το δεύτερο user FK.
- PK ConversationMembers(ConversationId, UserId): λίστα μελών και membership lookup.
- Index (UserId, ConversationId): μελλοντική λίστα συνομιλιών χρήστη.
- Unique partial Owner(ConversationId): αποτροπή πολλών owners.

Δεν προστίθεται speculative index ταξινόμησης activity/time πριν σχεδιαστούν οι queries
στο #30. Το partial direct-pair index δεν είναι γενικό index για κάθε FK scan σε
DirectUserLowId χωρίς Type predicate· μεγάλης κλίμακας διαγραφές/queries θέλουν EXPLAIN
και πιθανό πρόσθετο index. Τα ονόματα Group δεν είναι unique.

Διαγραφή Conversation: CASCADE μόνο στα memberships. Οι χρήστες/προφίλ διατηρούνται.
Διαγραφή Identity user που αναφέρεται σε membership ή direct pair: RESTRICT. Έτσι
δεν αλλοιώνεται σιωπηρά η σύνθεση/ταυτότητα συνομιλιών. Μελλοντική διαγραφή λογαριασμού
χρειάζεται ρητή πολιτική cleanup/anonymization. Για χρήστες χωρίς τέτοιες αναφορές,
το υπάρχον cascade User → Profile παραμένει ως είχε και καλύπτεται από Stage 1 tests.
Διαγραφή membership δεν διαγράφει Conversation ή Identity user.

## Migration και επαλήθευση

Η 20261031110209_AddConversationPersistence προσθέτει μόνο το νέο schema/tables,
constraints και indexes. Δεν αλλάζει ή μεταφέρει δεδομένα Identity/Profile.
Το Down αφαιρεί πρώτα memberships και μετά conversations· χάνει τα δεδομένα Stage 2
και δεν πρέπει να χρησιμοποιείται σε χρήσιμη βάση χωρίς ξεχωριστό σχέδιο επαναφοράς.
Δεν εκτελούμε migrations αυτόματα κατά την κανονική εκκίνηση της εφαρμογής.

```powershell
dotnet tool restore
dotnet build RealtimeChat.sln --configuration Release
dotnet test RealtimeChat.sln --configuration Release --no-build
dotnet ef migrations has-pending-model-changes --project src/Chat.Api --configuration Release --no-build
```

Με ρυθμισμένο connection string σε κατάλληλη βάση, η εφαρμογή γίνεται με
`dotnet ef database update --project src/Chat.Api`. Τα tests χρησιμοποιούν αποκλειστικά
προσωρινή PostgreSQL 18 μέσω Testcontainers, όχι τη βάση ανάπτυξης.

Καλύπτονται fresh migration, αναβάθμιση από Stage 1 με υπάρχοντα user/profile,
downgrade/reapply με διατήρηση Stage 1 δεδομένων, μηδενικές pending migrations,
συμφωνία model/snapshot, persistence, constraints/unique/FKs, concurrent direct pair,
UTC, cascade/restrict και transactional rollback όταν membership παραβιάζει constraint.
Όλα τα προϋπάρχοντα Stage 1 tests πρέπει να συνεχίζουν να περνούν.
