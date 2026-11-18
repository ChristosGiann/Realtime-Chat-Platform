# Redis infrastructure και δύο-instance topology — #97

Το έγγραφο καταγράφει το scope του #97. Για την ολοκληρωμένη Stage 6 τοπολογία δείτε το [production handoff](stage6-production-handoff.md).

Foundation του [Stage 6 design](stage6-distributed-semantics.md). Δεν ενεργοποιεί
SignalR backplane, distributed registry/presence/typing/revocation ή production scale-out.
PostgreSQL παραμένει η durable πηγή αλήθειας. Τα Stage 1–5 semantics δεν αλλάζουν.

## Dependencies και local Redis

Compose και Testcontainers χρησιμοποιούν pinned `redis:8.4.8-alpine3.22` από το
[official Redis image](https://hub.docker.com/_/redis). Ο API client είναι
[StackExchange.Redis 3.2.0](https://www.nuget.org/packages/StackExchange.Redis/3.2.0),
με `Testcontainers.Redis 4.16.0` μόνο στα tests. SignalR Redis package/registration
δεν χρειάζεται ακόμη και παραμένει στο #98. Δεν έγιναν άσχετα package upgrades.

```powershell
docker compose up -d postgres redis
docker compose ps
docker compose exec redis redis-cli ping
```

Το υπάρχον `POSTGRES_PASSWORD` παρέχεται από local environment/ignored `.env`.
Redis development port είναι `127.0.0.1:6379` (override `REDIS_PORT`), χωρίς published
external interface. Δεν υπάρχουν hard-coded passwords. Το local Redis είναι χωρίς
authentication, μόνο για trusted development host. Production χρειάζεται private
network, ACL/credentials και TLS όπου απαιτείται. Δεν μεταφέρεται το Compose ως
production security configuration. Δεν αποθηκεύεται Redis volume: RDB/AOF disabled,
128MB/noeviction ώστε memory pressure να αποτυγχάνει αντί για silent key eviction.
Μην εκτελείτε `down -v` σε υπάρχον development PostgreSQL volume.

## Configuration

Η configuration μπορεί να δοθεί με environment variables ή .NET user-secrets.
Κανένα connection string με secrets δεν χρειάζεται να μπει σε tracked JSON.

| Setting | Default / νόημα |
| --- | --- |
| `Redis:Enabled` | false· η υπάρχουσα εφαρμογή ξεκινά χωρίς Redis |
| `Redis:ConnectionString` | Κενό· απαιτείται όταν enabled, StackExchange.Redis format |
| `Redis:ApplicationName` | realtime-chat |
| `Redis:Deployment` | Απαιτείται όταν enabled, π.χ. local ή staging-eu |
| `Redis:ProtocolVersion` | v1 |
| `Redis:RunId` | shared για κοινό deployment· μοναδικό GUID ανά test topology |
| `Redis:OperationTimeoutMilliseconds` | 2000, επιτρεπτό 100–10000 |
| `RealtimeInstance:InstanceId` | Server-generated random ID, μπορεί να δοθεί instance-a/b· configuration μόνο |
| `SharedDataProtection:KeyRingPath` | Absolute shared persistent filesystem path, προαιρετικό για single-instance |
| `SharedDataProtection:ApplicationName` | Ίδιο discriminator στις instances· απαιτείται μαζί με KeyRingPath |

Namespace segments: 1–64 ASCII letters/digits/underscore/hyphen, χωρίς colon ή
wildcards. Validation στο startup απορρίπτει invalid enabled config χωρίς να
τυπώνει connection secrets. Prefix:
`application:deployment:protocol:run:coordination:` ή `...:backplane:`.
Το `RedisNamespace` παρέχει prefixes, δεν δημιουργεί production keys/channels.
Μελλοντικοί consumers οφείλουν να τα χρησιμοποιούν ρητά· ο multiplexer μόνος του
δεν κάνει automatic key rewriting. Redis database number **δεν** απομονώνει Pub/Sub.
Οι A/B του ίδιου topology έχουν κοινό RunId, διαφορετικά runs διαφορετικό RunId.

## Health και degraded mode

| Endpoint | Έλεγχος |
| --- | --- |
| `/health/live` | Process liveness, χωρίς dependency probes |
| `/health/ready` | Μόνο υπάρχουσα PostgreSQL readiness |
| `/health/realtime` | Redis PING connectivity: 200 healthy, 503 unavailable ή disabled |

Το νέο endpoint είναι **infrastructure connectivity**, όχι πιστοποίηση distributed
realtime safety ή ενεργού backplane. Η default απάντηση είναι μόνο status, χωρίς
endpoints/credentials. Health failures καταγράφονται από το health framework με
sanitized description, όχι Redis exception details. Ο multiplexer είναι singleton
ανά API host, lazy, χωρίς startup network dependency, με bounded waits και χωρίς
queueing disconnected commands. Reconnect του client επιτρέπει health recovery·
δεν αποτελεί CoordinationEpoch recovery implementation.

Μη χρησιμοποιείτε `/health/realtime` ως μοναδικό load-balancer gate για το durable
REST API. Redis outage δεν επηρεάζει PostgreSQL readiness ή committed data. Στο
#97 το runtime παραμένει Stage 5 single-instance: δεν προστίθεται νέο fail-closed
revocation protocol ούτε αλλάζει η λειτουργία των υπαρχόντων hubs. Τα #98–#103
πρέπει να εφαρμόσουν το πλήρες failure model πριν production multi-instance χρήση.

## Τοπικά API A και API B

Κάντε Release build και migrations μία φορά, όπως στο
[Stage 5 verification](stage5-verification.md). Σε δύο shells θέστε την ίδια
`ConnectionStrings__ChatDatabase` για την local PostgreSQL και τις κοινές τιμές:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:DOTNET_ENVIRONMENT = 'Development'
$env:Redis__Enabled = 'true'
$env:Redis__ConnectionString = 'localhost:6379'
$env:Redis__Deployment = 'local'
$env:SharedDataProtection__KeyRingPath = Join-Path $env:LOCALAPPDATA 'RealtimeChat/shared-keys'
$env:SharedDataProtection__ApplicationName = 'RealtimeChat-local'
```

Shell A:

```powershell
$env:RealtimeInstance__InstanceId = 'instance-a'
dotnet run --project src/Chat.Api --configuration Release --no-build --no-launch-profile --urls http://localhost:5081
```

Shell B:

```powershell
$env:RealtimeInstance__InstanceId = 'instance-b'
dotnet run --project src/Chat.Api --configuration Release --no-build --no-launch-profile --urls http://localhost:5082
```

Επιλέξτε ρητά `http://localhost:5081` ή `http://localhost:5082`. Δεν υπάρχει random
load balancing. Οι παραπάνω HTTP URLs/Development cookies είναι μόνο local setup.
Το frontend δεν αλλάζει configuration σε αυτό το issue.

## Shared Data Protection και deployment

Ο shared key ring είναι persistent filesystem/shared mount, **όχι ephemeral Redis**.
Όλες οι APIs χρησιμοποιούν ίδιο path/discriminator και cookie conventions. Στο
local ίδιο host αρκεί κοινός φάκελος· σε διαφορετικά hosts απαιτείται κοινό mounted
storage με περιορισμένα permissions, backup και κατάλληλη encryption-at-rest
διαχείριση. Η filesystem επιλογή δεν προσθέτει αυτόματα encryption keys στο repo.
Production encryption/provider provisioning παραμένει deployment responsibility.

Το frontend κάνει normal SignalR negotiation με credentials. Reverse proxy χρειάζεται
WebSocket support, HTTPS/WSS και session affinity για όλο το connection flow,
όπως περιγράφει το [Stage 6 design](stage6-distributed-semantics.md). Shared cookies
δεν αντικαθιστούν affinity. Δεν προστίθεται load balancer ούτε skipNegotiation.

## Reusable test topology και CI

[MultiInstanceTopology](../tests/Chat.Api.IntegrationTests/MultiInstanceTopology.cs)
ξεκινά PostgreSQL 18, Redis και δύο ανεξάρτητους WebApplicationFactory hosts με
πραγματικό Kestrel/TCP (.NET 10 UseKestrel), μέσα στο test process. Δεν είναι δύο
ξεχωριστά OS processes· process crash/partition verification ανήκει στο #102.

Ο OS δεσμεύει ελεύθερες ports για ασφαλή παράλληλα runs. Μετά το startup οι σταθερές
ανά run `AUrl`/`BUrl` αντιστοιχούν ονομαστικά στις A/B: deterministic επιλογή host,
όχι fixed global ports που συγκρούονται. Browser/E2E consumers μπορούν να στοχεύουν
αυτά τα πραγματικά URLs όσο ο fixture ζει. Τα containers, factories και μοναδικός
temporary key-ring φάκελος καθαρίζονται στο teardown. Migrations εκτελούνται μία
φορά πριν ξεκινήσει η B. Τα Testcontainers host ports επίσης είναι δυναμικά.

```powershell
dotnet test RealtimeChat.sln --configuration Release --filter FullyQualifiedName~RedisInfrastructureTests
dotnet test RealtimeChat.sln --configuration Release
```

Τα tests αποδεικνύουν actual Redis PING, shared PostgreSQL registration, cookie και
CSRF token που εκδίδονται στη μία API και χρησιμοποιούνται στην άλλη, κοινό Redis
probe value, independent namespace keys και literal Pub/Sub channels στο ίδιο DB,
pause/unpause Redis (μη αποκρινόμενο service στο ίδιο endpoint) με observable 503 και επιτυχή durable write ακόμη και από νέο API
host κατά outage. Τα Pub/Sub probes ελέγχουν isolation, όχι SignalR routing.

Το υπάρχον backend CI ήδη έχει Docker και τρέχει ολόκληρη τη suite, άρα τρέχει και
τα νέα Testcontainers tests χωρίς δεύτερο Redis service ή αλλαγή workflow. Τα
frontend/UI/Stage 1–5 Playwright jobs παραμένουν αμετάβλητα. Δεν αλλάζει EF model.

## Επόμενα βήματα

#98: backplane και routing. #99: registry/presence/ownership. #100: typing leases.
#101: revocation/authorization invalidation. #102: restart/partition/contention.
#103: πλήρες scale-out browser E2E. Κανένα από αυτά, ούτε CoordinationEpoch runtime
ή protected recipient refactor, δεν υλοποιείται εδώ. Το #88 παραμένει deferred.
