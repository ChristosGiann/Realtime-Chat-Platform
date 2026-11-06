# REST messaging UI — #45

Η επιλεγμένη συνομιλία περιλαμβάνει ιστορικό, φόρτωση παλαιότερων σελίδων,
composer έως 4000 UTF-16 μονάδες και unread count. Κείμενο/ονόματα αποδίδονται ως
React text, ποτέ HTML. Η UI σειρά είναι παλαιότερα → νεότερα, με διατήρηση της
ακρίβειας PostgreSQL timestamps και UUID tie-breaker. Τα rows συγχωνεύονται ανά id.

Κάθε λογική αποστολή αποκτά crypto.randomUUID(). Timeout/network/5xx κρατά το ίδιο
κλειδί και body για ρητό retry. Το composer κλειδώνει μέχρι να επιβεβαιωθεί το
μήνυμα, αποτρέποντας κατά λάθος νέο send με νέο key. Επιτυχές 200/201 εμφανίζει
μόνο το persisted DTO. Οριστικό 400/409 επιτρέπει διόρθωση με νέο logical send.
Pending retry διατηρείται μόνο όσο το component παραμένει ανοιχτό, όχι σε offline
queue ή μετά από reload/navigation· τότε ο χρήστης ελέγχει το persisted ιστορικό.

Η ανάγνωση προχωρά με ρητό «Σήμανση ιστορικού ως αναγνωσμένου» έως το νεότερο
μήνυμα που φορτώθηκε από ιστορικό. Το send δεν αλλάζει αυτό το όριο, ώστε να μην
χαρακτηριστούν αυτόματα ως διαβασμένα εισερχόμενα που δεν φορτώθηκαν. Το unread
εμφανίζεται στην επιλεγμένη συνομιλία και ενημερώνεται από server σε refresh/read.
Η φόρτωση παλαιότερων σελίδων δεν μετακινεί το όριο. Η ενέργεια αφορά όλο το prefix.

Τα API requests χρησιμοποιούν υπάρχοντα session/CSRF helpers. 401 ενεργοποιεί
session-expired, 403/404 καθαρίζουν το ιστορικό και κρύβουν composer/read actions.
Ακυρώνονται παλιές αναγνώσεις σε navigation· mutation responses μετά από unmount
δεν ενημερώνουν άλλη συνομιλία. Loading, empty, sending, validation, retry και error
states εμφανίζονται ρητά. Δεν υπάρχουν polling, live delivery, attachments ή offline queue.
