# Ανάγνωση και ενημέρωση προφίλ — issue #7

## Δημόσια ανάγνωση

Το GET /api/profiles/{userId} είναι ρητά AllowAnonymous. Το userId είναι Guid.
Επιστρέφει μόνο userId, displayName, bio και avatarUrl. Το query προβάλλει απευθείας
αυτά τα πεδία, χωρίς φόρτωση ή serialization του Identity entity.

Email, username, password hashes, security stamps και λοιπά στοιχεία authentication
δεν περιλαμβάνονται στο δημόσιο DTO. Το userId αποτελεί το δημόσιο αναγνωριστικό προφίλ.
Αν δεν υπάρχει ο χρήστης ή το profile, επιστρέφεται 404 application/problem+json με
τίτλο Profile not found. Τα responses έχουν no-store ώστε οι αλλαγές να μην κρύβονται από cache.

## Ενημέρωση του δικού μας προφίλ

Το PUT /api/profiles/me απαιτεί την AuthenticatedUser policy και έγκυρο X-CSRF-TOKEN.
Ο browser ακολουθεί τη ροή CSRF/login του browser-sessions.md και ανανεώνει το token μετά το login.

Το ID προκύπτει αποκλειστικά από το ICurrentUser. Δεν υπάρχει update route που να δέχεται
ID άλλου χρήστη. Άγνωστα JSON πεδία, όπως userId, απορρίπτονται ως 400 αντί να γίνονται
σιωπηρά bind σε entity. Query parameters δεν επιλέγουν τον χρήστη.

Το σώμα περιέχει:

- displayName: υποχρεωτικό, όχι μόνο κενά, έως 100 χαρακτήρες.
- bio: προαιρετικό, έως 1.000 χαρακτήρες.
- avatarUrl: προαιρετικό, έως 2.048 χαρακτήρες, απόλυτο HTTPS URL χωρίς ενσωματωμένα credentials.

Τα όρια ελέγχονται πριν την περικοπή εξωτερικών κενών. Το displayName αποθηκεύεται trimmed.
Bio/avatarUrl που παραλείπονται, είναι null ή περιέχουν μόνο κενά αποθηκεύονται ως null.
Το PUT αντικαθιστά ολόκληρο το επεξεργάσιμο profile· δεν είναι μερικό PATCH.

Επιτυχία: 200 με το δημόσιο DTO που αποθηκεύτηκε.
Άκυρα δεδομένα ή JSON: 400 ValidationProblemDetails.
Άκυρο CSRF: 400 ProblemDetails. Χωρίς session: 401 ProblemDetails.
Αν λείπει το profile: 404 ProblemDetails. Δεν δημιουργείται profile σιωπηρά από το update endpoint.

Το avatar URL αποθηκεύεται ως metadata. Ο server δεν κατεβάζει το περιεχόμενο ούτε
επιβεβαιώνει ότι ο προορισμός είναι εικόνα. Δεν περιλαμβάνεται file upload.
Το bio είναι απλό κείμενο· οι clients πρέπει να το εμφανίζουν με escaping, όχι ως HTML.

## Δοκιμές

Τα ProfileTests χρησιμοποιούν πραγματική PostgreSQL και πραγματική εγγραφή/login με cookies.
Καλύπτουν:

- Αποθήκευση, ανάγνωση από νέο scope και ενημέρωση της απόκρισης /api/auth/me.
- Δημόσια πρόσβαση με ακριβή λίστα επιτρεπόμενων πεδίων.
- Εκκαθάριση των προαιρετικών πεδίων με PUT.
- Απόρριψη anonymous update και missing/invalid CSRF χωρίς αλλαγή της βάσης.
- Απόπειρες στόχευσης δεύτερου χρήστη μέσω body, route και query.
- Όρια μήκους, κενό display name, μη ασφαλή/σχετικά avatar URLs και credentials σε URL.
- Αποδοχή των μέγιστων επιτρεπόμενων μηκών.
- Malformed/missing JSON και συνεπές 404 για ανύπαρκτο profile.

    dotnet build RealtimeChat.sln --configuration Release
    dotnet test RealtimeChat.sln --configuration Release

Δεν απαιτείται migration ή νέο πακέτο. Αναζήτηση χρηστών, blocking και moderation
παραμένουν εκτός του issue #7.
