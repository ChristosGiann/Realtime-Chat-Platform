# Post-commit message events — #62

Μετά από επιτυχές PostgreSQL commit μιας νέας REST αποστολής, ο server εκπέμπει
`MessageCreated` στο `conversation:{conversationId:N}` μέσω IHubContext<ChatHub>.
Παραλήπτες είναι οι συνδέσεις που έκαναν επιτυχές Subscribe στο #61, μαζί με τον
αποστολέα εφόσον είναι subscribed. Το Hub δεν αποθηκεύει μηνύματα.

Το payload είναι ακριβώς το ασφαλές MessageResponse του REST: id, clientMessageId,
conversationId, senderId, body, createdAt (UTC). Δεν περιλαμβάνει Identity entities,
emails ή connection IDs. Ο client μπορεί να συσχετίσει REST response/event με το
durable id/clientMessageId. Το frontend reconciliation υλοποιείται στο #64.

Μόνο νέα 201 αποστολή προκαλεί event attempt. Safe retry 200 επιστρέφει το αρχικό
μήνυμα χωρίς νέα εκπομπή. Validation/CSRF/authorization failures, idempotency conflict,
SaveChanges failure ή rollback δεν καλούν τον broadcaster. Οι κανόνες αποθήκευσης
και το υπάρχον response contract δεν αλλάζουν.

Η εκπομπή ακολουθεί το CommitAsync, με ξεχωριστό όριο 5 δευτερολέπτων και χωρίς
να εξαρτάται από RequestAborted μετά το commit. Αν αποτύχει/λήξει η εκπομπή,
καταγράφεται warning με το message ID και το REST επιστρέφει ακόμη 201: η durable
αποθήκευση πέτυχε. Δεν υπάρχει rollback του committed μηνύματος ούτε retry event
μέσω επανάληψης του POST. Το SignalR είναι ειδοποίηση, όχι source of truth.

Δεν υπάρχει outbox, delivery acknowledgment ή εγγύηση ακριβώς μίας παράδοσης.
Crash μεταξύ commit και event, αποσύνδεση ή transport failure μπορεί να χάσει το
event. Διαφορετικές ταυτόχρονες αποστολές μπορεί να εκπέμπονται εκτός commit order.
Η durable σειρά παραμένει CreatedAt/Id και η ανάκτηση γίνεται από REST (#65).

Η ανάκληση ήδη ενεργού group μετά από membership removal υλοποιείται στο #63:
δείτε [τη σειρά locks και τις εγγυήσεις ανάκλησης](signalr-revocation.md).
Presence, receipts, Redis/backplane και live UI παραμένουν στα επόμενα scopes.

Τα PostgreSQL/SignalR tests ελέγχουν Direct/Group delivery σε sender/member,
απομόνωση συνομιλιών, unsubscribe, concurrent idempotent sends, μηδενικά events
μετά από rejected/rolled-back writes και διατήρηση 201/200 σε transport failure.
Ανεξάρτητο DbContext στον test publisher αποδεικνύει ότι το row είναι ορατό
πριν αρχίσει η εκπομπή. Barriers αποφεύγουν αυθαίρετα sleeps στα negative checks.
