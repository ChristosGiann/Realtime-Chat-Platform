# Εγγραφή χρηστών — issue #4

Το POST /api/auth/register δέχεται JSON με email, userName, password και προαιρετικό displayName.
Αν το displayName παραλείπεται ή είναι null, χρησιμοποιείται το username. Τα δημόσια πεδία
περικόπτονται από εξωτερικά κενά· ο κωδικός δεν τροποποιείται. Bio και avatar αρχικοποιούνται ως null.

## Συμβόλαιο

- Email: υποχρεωτικό, έγκυρη μορφή, έως 256 χαρακτήρες.
- Username: υποχρεωτικό, έως 100 χαρακτήρες και οι επιτρεπόμενοι χαρακτήρες του Identity.
- Password: 6–128 χαρακτήρες και η πολιτική Identity (κεφαλαίο, πεζό, ψηφίο, μη αλφαριθμητικό).
- Display name: έως 100 χαρακτήρες, όχι κενό όταν παρέχεται.
- 201: JSON με id, userName, email και displayName. Δεν επιστρέφονται κωδικοί, hashes ή tokens και δεν δημιουργείται session.
- 400: application/problem+json με errors ανά πεδίο για validation ή μη έγκυρο JSON.
- 409: application/problem+json για υπάρχον normalized email ή username.

## Ακεραιότητα και όρια

Η δημιουργία μέσω UserManager και η αποθήκευση profile μοιράζονται το ίδιο DbContext
και PostgreSQL transaction. Αποτυχία πριν το commit αναιρεί και τις δύο εγγραφές.
Οι unique indexes παραμένουν η τελική προστασία σε ταυτόχρονες αιτήσεις:
μόνο παραβιάσεις EmailIndex/UserNameIndex μετατρέπονται σε 409.
Άλλα σφάλματα υποδομής δεν παρουσιάζονται ως λάθη validation.

Ο κωδικός περνά μόνο στο UserManager για hashing. Το request είναι class χωρίς
παραγόμενο ToString που θα περιείχε τον κωδικό, και ο endpoint δεν καταγράφει το σώμα του αιτήματος.
Login, cookies, email confirmation και password reset δεν περιλαμβάνονται.

## Έλεγχοι

Τα RegistrationTests χρησιμοποιούν απομονωμένα PostgreSQL Testcontainers:
επιτυχία και persistence, defaults, ασφαλές DTO, validation, malformed JSON,
case-insensitive duplicates, ταυτόχρονες συγκρούσεις email/username και rollback
με σκόπιμη αποτυχία εγγραφής profile.

Εκτέλεση με ενεργό Docker:

    dotnet build RealtimeChat.sln --configuration Release
    dotnet test RealtimeChat.sln --configuration Release
