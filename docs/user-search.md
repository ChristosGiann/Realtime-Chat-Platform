# Αναζήτηση χρηστών — issue #8

## Συμβόλαιο

`GET /api/users?query={text}&pageSize={size}&cursor={cursor}` απαιτεί ενεργή
συνεδρία (πολιτική AuthenticatedUser). Anonymous πρόσβαση επιστρέφει 401
ProblemDetails χωρίς redirect. Το GET δεν μεταβάλλει δεδομένα και δεν απαιτεί CSRF.
Η απόκριση έχει Cache-Control: no-store.

- query: έως 100 χαρακτήρες πριν το trim. Αναζήτηση υποσυμβολοσειράς χωρίς διάκριση
  πεζών/κεφαλαίων σε username ή display name, μέσω PostgreSQL ILIKE.
  Η συμπεριφορά Unicode εξαρτάται από τη locale/collation της βάσης· δεν γίνεται
  αφαίρεση τόνων ή fuzzy matching. Οι χαρακτήρες %, _ και backslash είναι κυριολεκτικοί.
- Κενό, απόν ή μόνο κενά query: 200 με items: [] και nextCursor: null.
  Το ίδιο ισχύει όταν δεν υπάρχουν αντιστοιχίες.
- pageSize: προεπιλογή 20, αποδεκτές ακέραιες τιμές 1–50. Τιμές εκτός ορίων
  ή μη ακέραιες επιστρέφουν 400 ValidationProblemDetails.
- cursor: προαιρετικό opaque token έως 4.096 χαρακτήρες. Ο client το μεταφέρει
  με URL encoding, διατηρώντας το ίδιο trimmed query. Μπορεί να αλλάξει pageSize.

Η απόκριση έχει τη μορφή:

```json
{
  "items": [
    {
      "userId": "12b869c5-e232-40ec-a277-02c15edb81fe",
      "displayName": "Μαρία",
      "bio": null,
      "avatarUrl": null
    }
  ],
  "nextCursor": "opaque-token"
}
```

Τα items χρησιμοποιούν ακριβώς το PublicProfileResponse του #7. Δεν επιστρέφονται
Identity entities, email, password hash ή security stamp. Το username χρησιμοποιείται
ως πεδίο αναζήτησης, αλλά δεν προστίθεται στο υπάρχον δημόσιο DTO. Bio και avatar URL
είναι δημόσια πεδία απόκρισης, όχι πεδία αναζήτησης. Αποκλείεται ο ίδιος ο συνδεδεμένος
χρήστης. Χρήστες χωρίς profile δεν επιστρέφονται λόγω inner join.

## Σταθερή σελιδοποίηση

Η σειρά είναι αύξουσα ως προς το μοναδικό, αμετάβλητο UUID UserId στη PostgreSQL,
όχι αλφαβητική ή κατά συνάφεια. Κάθε επόμενη σελίδα εφαρμόζει UserId > lastUserId
και δεν χρησιμοποιεί OFFSET. Έτσι ίδια display names δεν προκαλούν αμφισημία,
ούτε μετακινείται το σημείο συνέχισης όταν διαγράφεται η προηγούμενη εγγραφή ή
αλλάζει το όνομά της. Ανακτώνται έως pageSize + 1 εγγραφές μόνο για να διαπιστωθεί
αν υπάρχει επόμενη σελίδα. Στο τέλος το nextCursor είναι null, ακόμη και όταν
η τελευταία σελίδα έχει ακριβώς pageSize αποτελέσματα.

Ο cursor προστατεύεται με ASP.NET Core Data Protection, με ξεχωριστό purpose v1.
Περιέχει το τελευταίο ID, το trimmed query και το ID του χρήστη που έκανε την
αναζήτηση. Αλλοιωμένος cursor ή χρήση του από άλλον χρήστη/με άλλο query επιστρέφει
400 ProblemDetails. Δεν αντικαθιστά το authentication και δεν περιέχει ιδιωτικά
πεδία Identity. Τα instances πρέπει να μοιράζονται τα αποθηκευμένα Data Protection
keys, όπως ήδη απαιτείται για τις συνεδρίες. Μη αναγνώσιμοι παλιοί cursors απαιτούν
επανεκκίνηση της αναζήτησης. Δεν ορίζεται ξεχωριστό TTL.

Δεν παρέχεται snapshot ανάμεσα σε HTTP requests. Νέες εγγραφές με ID πριν από
τον cursor ή εγγραφές που παύουν να ταιριάζουν μπορεί να μην εμφανιστούν. Εγγραφές
με μεγαλύτερο ID που αρχίζουν να ταιριάζουν μπορεί να εμφανιστούν. Η αυστηρή
αύξηση του ID αποτρέπει την επανεμφάνιση ήδη επιστραφέντων χρηστών.

## Ευρετήρια και κόστος

Τα υπάρχοντα primary-key B-tree indexes σε UserProfiles.UserId και AspNetUsers.Id
υποστηρίζουν τη συσχέτιση και τη διάταξη/keyset αναζήτηση. Δεν προστίθεται migration.
Αυτά **δεν** επιταχύνουν την αναζήτηση ILIKE '%text%' σε username/display name.
Ούτε το υπάρχον unique index στο NormalizedUserName επαρκεί για αυτήν τη συνθήκη.
Ο planner μπορεί επομένως να επιλέξει scan/sort· το όριο σελίδας περιορίζει το
μέγεθος της απόκρισης, όχι τον αριθμό εγγραφών που εξετάζονται.

Για μεγαλύτερο όγκο δεδομένων χρειάζεται μέτρηση με EXPLAIN (ANALYZE, BUFFERS)
και αντιπροσωπευτικά δεδομένα. Πιθανή επόμενη βελτιστοποίηση είναι pg_trgm GIN
indexes στα δύο πεδία, με έλεγχο του OR/join query plan και των πολύ μικρών queries.
Δεν εισάγουμε extension ή κόστος ενημέρωσης indexes χωρίς αυτές τις μετρήσεις.
Full-text engines και fuzzy ranking παραμένουν εκτός scope.

## Έλεγχοι

Τα UserSearchTests χρησιμοποιούν πραγματική PostgreSQL μέσω Testcontainers:
matching, πεζά/κεφαλαία και trim, κυριολεκτικά wildcards, αποκλεισμός εαυτού και
missing profiles, ακριβής λίστα δημόσιων πεδίων, μη αναζήτηση email/bio/avatar,
κενά/no-match αποτελέσματα, όρια μεγέθους και input, διαδοχικές σελίδες χωρίς
διπλότυπα, διαγραφή/μετονομασία, cursor tampering και δέσμευση σε χρήστη/query.

```powershell
dotnet build RealtimeChat.sln --configuration Release --no-restore
dotnet test RealtimeChat.sln --configuration Release --no-build
```
