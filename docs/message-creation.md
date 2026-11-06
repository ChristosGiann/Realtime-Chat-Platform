# Durable αποστολή μηνυμάτων — #41

POST /api/conversations/{conversationId}/messages με JSON { "clientMessageId": "UUID", "body": "κείμενο" }
απαιτεί authenticated session και φρέσκο CSRF token. SenderId προκύπτει από το
ICurrentUser μέσω ConversationAuthorization, ποτέ από τον client. Άγνωστα JSON
πεδία (sender/id/timestamp κ.λπ.) απορρίπτονται με 400.

Body: υποχρεωτικό, όχι whitespace-only, έως 4000 UTF-16 code units, χωρίς NUL.
Δεν γίνεται trimming: κενά/νέες γραμμές/κείμενο διατηρούνται ακριβώς. Το API όριο
είναι συντηρητικό ως προς το PostgreSQL varchar(4000), που μετρά χαρακτήρες Unicode.
Το κείμενο δεν θεωρείται HTML. Οι μελλοντικοί clients πρέπει να το αποδίδουν ασφαλώς.

Η ροή ανοίγει transaction, κλειδώνει τη Conversation με FOR UPDATE όπως τα
membership mutations και κατόπιν ελέγχει το νέο SendMessage permission. Επιτρέπεται
σε Group Owner/Member και Direct Member. Outsider/removed member και missing/deleted
conversation επιστρέφουν 404. Anonymous/deleted Identity user 401. CSRF/body errors 400.
Ο έλεγχος και το write παραμένουν στην ίδια transaction μέχρι το commit.

Μόνο μετά το επιτυχές commit επιστρέφεται 201 με id/clientMessageId/conversationId/senderId/body/createdAt.
CreatedAt προέρχεται από το UTC database insertion default. Δεν σειριοποιούνται
Identity entities, emails ή εσωτερικά πεδία. No-store, χωρίς Location προς ανύπαρκτο GET.
Αποτυχία της βάσης δεν μετατρέπεται σε επιτυχία και το disposal κάνει rollback.

Το #42 προσθέτει ασφαλή retries: ίδια λογική αποστολή επιστρέφει 200 με το αρχικό μήνυμα, ενώ ασύμβατη επαναχρησιμοποίηση κλειδιού επιστρέφει 409. Δείτε το [συμβόλαιο idempotency](message-idempotency.md). Δεν προστίθενται history API, SignalR ή attachments.

PostgreSQL/Testcontainers tests καλύπτουν Direct/Group send από τα επιτρεπόμενα μέλη,
safe DTO και committed persistence, body limits/Unicode/NUL, spoofed fields,
anonymous/outsider/deleted/missing, CSRF, forced rollback και πραγματική αναμονή
σε database row lock πριν από concurrent membership removal.
