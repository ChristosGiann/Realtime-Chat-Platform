# Ιστορικό μηνυμάτων — #43

GET `/api/conversations/{id}/messages?limit=30&cursor=...` επιστρέφει `{items,nextCursor}`.
Η σειρά είναι CreatedAt DESC, Id DESC (νεότερα πρώτα). Το limit είναι 1–100, default 30.
Κενή συνομιλία: κενά items και null nextCursor. Δεν χρησιμοποιείται OFFSET.

Το PostgreSQL tuple comparison `(CreatedAt,Id) < (cursorTime,cursorId)` χρησιμοποιεί
το υπάρχον IX_Messages_ConversationHistory και την ίδια UUID ταξινόμηση με το ORDER BY.
Διαβάζονται έως limit+1 rows για να αποφασιστεί αν υπάρχει επόμενη σελίδα.
Το cursor προστατεύεται με ASP.NET Data Protection και δεσμεύεται σε έκδοση,
συνομιλία, χρήστη και limit. Παραποιημένο ή ασύμβατο cursor επιστρέφει 400.
Απαιτείται διατήρηση/κοινή χρήση Data Protection keys σε deployments με πολλαπλά instances,
όπως για τα sessions. Με απώλεια keys ο client ξεκινά από την πρώτη σελίδα.

Κάθε request επανελέγχει Identity/membership. Το membership predicate συμπεριλαμβάνεται
και στο SQL των μηνυμάτων ώστε να μην διαρρεύσει ιστορικό μετά από αφαίρεση μέλους.
Μη μέλος/ανύπαρκτη συνομιλία: 404, μη authenticated: 401. Το response είναι no-store.
Το sender περιέχει μόνο userId, τρέχον displayName/avatarUrl· όχι email ή Identity entity.
Παλιό μήνυμα παραμένει ορατό ακόμη κι αν ο αποστολέας έφυγε από τη συνομιλία.

Οι σελίδες δεν αποτελούν σταθερό database snapshot. Νεότερες εισαγωγές δεν παρεμβάλλονται
στη συνέχεια του παλιού cursor· απαιτείται refresh της πρώτης σελίδας. Διαγραφές δεν
δημιουργούν duplicates ή εξάρτηση από υπαρκτό anchor row. Backdated imports/αλλαγή clock
μπορεί να εμφανιστούν σε επόμενη σελίδα ή να απαιτούν refresh, ανάλογα με τη θέση τους.
Δεν υπάρχει API edit/delete, search ή live delivery στο scope αυτού του issue.
