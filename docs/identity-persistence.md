# Identity και Profiles — issue #3

Το ASP.NET Core Identity διαχειρίζεται credentials, normalized email/username και password hashes. Το ApplicationUser χρησιμοποιεί Guid ID. Τα δημόσια στοιχεία ανήκουν στο ξεχωριστό UserProfile (display name, bio, avatar URL).

## PostgreSQL

Οι πίνακες Identity βρίσκονται στο schema `identity` και ο πίνακας `UserProfiles` στο `profiles`. Το UserId είναι ταυτόχρονα primary key και foreign key προς τον χρήστη: δεν επιτρέπονται orphan profiles ή δεύτερο profile για τον ίδιο χρήστη. Διαγραφή χρήστη διαγράφει και το profile. Η υποχρεωτική δημιουργία profile μαζί με τον λογαριασμό θα υλοποιηθεί στη συναλλαγή registration του #4· το schema από μόνο του επιτρέπει χρήστη χωρίς profile.

Τα normalized email και username είναι υποχρεωτικά και έχουν unique indexes, ώστε ακόμη και ταυτόχρονες εγγραφές να μην δημιουργούν διπλότυπα. Οι εγγραφές λογαριασμών πρέπει να περνούν από UserManager για normalization, validation και password hashing.

## Τοπική migration

Με ενεργό Docker και ρυθμισμένο POSTGRES_PASSWORD στο τοπικό .env:

```powershell
docker compose up -d postgres
# Ρύθμισε το ConnectionStrings:ChatDatabase με dotnet user-secrets ή environment variable.
dotnet ef database update --project src/Chat.Api
```

Το connection string πρέπει να συμφωνεί με τη βάση του compose. Μην καταχωρίζεις credentials στο Git. Δεν εκτελούμε migrations αυτόματα κατά το startup.

## Έλεγχοι

```powershell
dotnet build RealtimeChat.sln --configuration Release
dotnet test RealtimeChat.sln --configuration Release
```

Τα integration tests χρειάζονται Docker. Το Testcontainers δημιουργεί προσωρινή PostgreSQL 18 με δυναμική θύρα, εφαρμόζει την πραγματική migration και καθαρίζει το container. Δεν χρησιμοποιεί τη βάση ανάπτυξης. Ελέγχονται persistence, Identity validation, database uniqueness, σχέση profile/user, cascade delete και health endpoints. Το CI τρέχει επίσης για dev και codex branches/PRs.

Registration, login, cookies και UI ακολουθούν στα επόμενα issues.
