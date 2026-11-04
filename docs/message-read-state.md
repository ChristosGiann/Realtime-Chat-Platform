# Durable πρόοδος ανάγνωσης — #44

GET `/api/conversations/{id}/read-state` επιστρέφει lastReadMessageId, lastReadAt
και unreadCount. PUT στο ίδιο URL με `{messageId}` προχωρά μόνο τον authenticated
χρήστη έως το συγκεκριμένο μήνυμα και απαιτεί CSRF. Δεν δέχεται userId ή client time.
Απαντά 200 με την τελική κατάσταση ακόμη και για παλαιότερο/ίδιο messageId.
Ανύπαρκτο/ξένο μήνυμα και μη μέλος: 404. Άκυρο σώμα: 400. Χωρίς session: 401.

Το nullable LastReadMessageId ανήκει στο ConversationMember. Composite FK προς
Messages(ConversationId,Id) εγγυάται μήνυμα της ίδιας συνομιλίας. Το RESTRICT
αποτρέπει διαγραφή ενεργού anchor· μελλοντικό delete API χρειάζεται ρητή πολιτική.
Η migration αφήνει null σε όλα τα υπάρχοντα memberships, χωρίς απώλεια μηνυμάτων.

Η θέση συγκρίνεται με PostgreSQL `(CreatedAt,Id)`, ακριβώς όπως το ιστορικό.
Conversation row lock προστατεύει authorization, monotonic update και το response
από concurrent sends, read updates και membership mutations. Ακόμη και το GET
χρησιμοποιεί σύντομη transaction/lock για συνεπή εικόνα. Αυτή η απλή επιλογή
σειριοποιεί τη δουλειά ανά συνομιλία· πιθανή μελλοντική βελτιστοποίηση απαιτεί tests.
Δεν υπάρχει cache/Redis και το count προκύπτει από persisted messages, με το history index.

Unread είναι τα μηνύματα άλλων αποστολέων με θέση μεγαλύτερη από το anchor.
Null anchor σημαίνει όλα τα μηνύματα άλλων χρηστών. Το send δεν μετακινεί τη θέση.
Νέα μέλη βλέπουν όλο το ιστορικό και ξεκινούν χωρίς read progress. Leave/removal
διαγράφει το membership και την πρόοδο· επανένταξη ξεκινά ξανά από null.
Τα μέλη έχουν πλήρως ανεξάρτητη κατάσταση. Δεν εκτίθενται read receipts άλλων μελών.

Η θέση σημαίνει αναγνωσμένο prefix της σειράς και όχι καταγραφή κάθε προβολής.
Backdated imports/μετακίνηση database clock πίσω από anchor δεν μετρούν ως νέα unread.
Η υπόθεση κανονικών UTC insertion timestamps τεκμηριώνεται για το Stage 4.
Downgrade αφαιρεί μόνο την πρόοδο και το υποστηρικτικό alternate key, διατηρώντας μηνύματα.
