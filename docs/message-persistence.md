# Persistence μηνυμάτων — #40

Το Message αποθηκεύεται στο messages.Messages του υπάρχοντος ChatDbContext.
Id/ConversationId/SenderId είναι Guid/uuid. Το Id δημιουργείται στην εφαρμογή
και προστατεύεται με PK και CHECK μη κενού Guid. Δεν αποτελεί client retry key.

Το Body είναι υποχρεωτικό varchar(4000), χωρίς trimming ή HTML interpretation.
Κενό ή αποκλειστικά whitespace περιεχόμενο απορρίπτεται και στη βάση. Το CHECK
καλύπτει τα whitespace code points που αναγνωρίζει το .NET, με ρητά chr(...)
ώστε να μην εξαρτάται από PostgreSQL locale. Μη ορατοί format χαρακτήρες όπως
zero-width space δεν θεωρούνται whitespace σε αυτό το contract.
Η PostgreSQL μετρά Unicode χαρακτήρες· το μελλοντικό API μπορεί να εφαρμόζει το
αυστηρότερο όριο 4000 UTF-16 code units όπως τα υπόλοιπα StringLength contracts.
NUL δεν είναι έγκυρος χαρακτήρας PostgreSQL text και πρέπει να απορρίπτεται στο API.

CreatedAt είναι timestamptz, διαβάζεται ως DateTimeKind.Utc και έχει default
clock_timestamp(): χρόνος εισαγωγής από τη βάση, όχι έναρξης transaction πριν
από αναμονή για conversation lock. Το isfinite CHECK αποκλείει infinity.
Δεν προστίθεται update/edit timestamp ή soft delete πριν ζητηθεί.

## Σχέσεις και διατήρηση ιστορικού

ConversationId → conversations.Conversations και SenderId → identity.AspNetUsers
με RESTRICT. Διαγραφή χρήστη ή συνομιλίας με μηνύματα απορρίπτεται αντί να χάνεται
σιωπηρά ιστορικό. Μελλοντικό account/conversation deletion απαιτεί ρητή πολιτική
διατήρησης/anonymization/cleanup. Το #40 δεν δημιουργεί τέτοια λειτουργία.

Δεν υπάρχει FK προς ConversationMember: leave/removal πρέπει να διατηρεί παλιά
μηνύματα. Η απαίτηση ενεργού membership κατά το send ανήκει στην εξουσιοδοτημένη
transaction του #41. Το database FK εγγυάται υπαρκτό sender, όχι ενεργό member.
Δεν υπάρχει navigation που να σειριοποιεί Identity entities σε responses.

## Ιστορικό και migration

Index IX_Messages_ConversationHistory (ConversationId ASC, CreatedAt DESC, Id DESC)
υποστηρίζει per-conversation traversal με stable tie-break. Το #43 θα χρησιμοποιήσει
bounded keyset pagination, όχι OFFSET. Timestamp/Guid ordering δεν είναι παγκόσμιο
commit sequence· αλλαγές database clock/μελλοντικά imports απαιτούν ξεχωριστή
αντιμετώπιση στα history/read-state contracts.

Η 20261103161543_AddMessagePersistence προσθέτει μόνο το νέο schema/table,
constraints, FKs και indexes. Το Down αφαιρεί messages, χάνοντας αυτά τα δεδομένα,
και διατηρεί Stage 1/2. Δεν εκτελείται αυτόματα κατά την κανονική εκκίνηση.

Τα PostgreSQL 18/Testcontainers tests ελέγχουν fresh migration, upgrade από Stage 2,
downgrade/reapply, IDs, μήκος/whitespace/null, UTC/infinity/non-UTC, index/tie-break,
FKs, RESTRICT και διατήρηση ιστορικού μετά από membership removal. Τα υπάρχοντα
Stage 1/2 tests παραμένουν ενεργά. Δεν προστίθενται send endpoint, idempotency,
history API, unread state ή SignalR στο #40.
