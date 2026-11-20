# N3 — dijagnostika i pouzdanost prikupljanja

20.11.2026. **N3 je u tijeku, nije operativno zatvoren.** Implementirana su fazna mjerenja i ispravljen je potvrđen problem flush roka. Stvarni feed otkriva zaseban zastoj u spremanju quality stanja u SQLite; njegov dublji uzrok i trajno uklanjanje tek treba potvrditi. FX-12, 25/30/31 ostaju otvoreni, bez naloga ili odobrenja modela.

## Što je promijenjeno

- `collection-timing-v2` jednom u minuti bilježi raspodjele p50/p95/p99 i maksimum za vrijeme do primitka, red prije obrade, quality provjeru (čekanje stream locka, čitanje, procjenu, spremanje), čekanje dataset reda i agregaciju. Fazna mjerenja koriste monotoni sat; market event/receive vremena i njihove tolerancije nisu promijenjeni.
- IBKR događaj nosi process-local monotoni enqueue timestamp, isključen iz JSON-a i Parqueta. `QueueMonotonic` mjeri vrijeme od enqueuea do preuzimanja, a postojeći `DequeueMinusReceipt` ostaje wall-clock dijagnostika. Negativne korekcije sata nisu prikrivene nulom. Receive-minus-event nije čista mrežna latencija jer IBKR event vrijeme ima rezoluciju sekunde.
- Histogram ima fiksni broj logaritamskih bucketova, obuhvaća sve nenegativne uzorke minute i daje **gornje granice kvantila**, s do približno 10% razlike bucketova; maksimum je stvarno opažen. Negativni wall-clock uzorci nisu uključeni u nenegativni histogram. Broj uzoraka ostaje vidljiv. Kvantile različitih minuta ne smije se prosječiti ili proglasiti globalnim p99.
- Uz tri spora primjera čuva se zaseban stvarni maksimum pipelinea i reda s vremenima istog događaja. Quality ukupno uključuje svoje podfaze: ne zbrajati ih još jednom u ukupno trajanje. Zapis nema cijene, veličine naloga, račune ni API ključeve.
- Writer posebno mjeri čekanje do dovršenog zapisa, ukupni flush, store lock, čitanje manifesta, Parquet serijalizaciju i zapis manifesta. `EnqueueToDurable` znači povratak uspješnog `WriteBatchAsync`, ne novi dokaz otpornosti diska na nestanak napajanja. Storage metrika je zajednička svim instrumentima tog writera.
- CPU procesa, working set, alokacije, GC delte, thread-pool pending, slobodan disk, veličina logova i status veze/pretplate daju korelacijski kontekst. Nisu dokaz uzročnosti. Manje od 5 GiB slobodnog diska ili više od 2 GiB logova daje WARN. Ništa se automatski ne briše i kolektor se time ne isključuje.
- SQL pozivi i početak/commit transakcije od barem 250 ms imaju zasebni WARN s trajanjem i context ID-em; komande dodaju samo vrstu operacije i naziv tablice. Nema SQL teksta, parametara ili connection stringa. Nije promijenjena trajnost SQLitea, timeout, risk ili pravilo prihvata ticka.

## Potvrđena greška i ispravak

Stari writer na svakom dolasku događaja stvarao je novi `Task.Delay(FlushInterval)`. Stalni sporiji tok koji ne napuni blok mogao je stalno odgađati vremenski flush, uz napuštene timer/wait zadatke. Rok se sada računa od **prvog zapisa u trenutačnom bloku**; novi dolazak ga ne produljuje. Čekanje ima otkazivi timer koji se odlaže nakon buđenja. Brz tok i dalje flush-a na veličini bloka.

Uredan stop zatvara producer stranu, dovršava započeti file/manifest zapis i prazni red u ograničenim blokovima. Stop token više ne prekida započeti zapis između datoteke i manifesta. Neuspjeh stvarnog I/O i dalje se prijavljuje; ovo nije zaštita od nasilnog gašenja procesa ili kvarova diska. Test s blokiranim zapisom potvrđuje jedan dovršen pokušaj bez cancellation/retry duplikata; test stalnog trickle toka potvrđuje flush prije popunjavanja bloka i očuvanje svih zapisa pri stopu.

## Dosadašnji stvarni nalazi

Prije izmjena najsporija dnevna minuta sadrži pipeline **2.005,76 ms**. Zastoj na callback primitku i lokalni zastoj nisu isto. Nove faze uhvatile su npr. **1.910,88 ms ukupno, od čega 1.910,37 ms `QualitySave`**, uz 0,41 ms quality čitanja i približno 0,01 ms za enqueue/agregaciju. Kasnije je opaženo i **1.946,96 ms ukupno / 1.946,32 ms spremanja**. Ostale kotacije zatim čekaju u redu. Iz ovoga se može zaključiti gdje se troši vrijeme, ali **ne još je li uzrok lock, commit/fsync, I/O ili drugi konkurentni rad**.

Zabilježen je i spor startup SELECT istraživačkih kandidata te spor UPDATE quality stanja. Cooldown upit već koristi postojeći pokrivajući indeks; ne dodajemo naslijepo drugi indeks niti isključujemo istraživanje radi ljepšeg rezultata. Nakon početnog zagrijavanja jedan storage prozor imao je 12 uspješnih flushova, maksimum 333,63 ms i maksimum enqueue-to-write 5,59 s. To potvrđuje kratki uzorak ispravljenog rasporeda, a ne cijelu sesiju bez zastoja. Početni cold flush imao je 2,54 s i ukupno čekanje do zapisa 8,28 s; taj outlier ostaje zapisan.

Windows Time je uključen i sinkroniziran; tri uzorka u 19:51 Zagreb daju približno **+26–28 ms** prema time.windows.com. To je opažena razlika prema tom izvoru, ne dokaz trajne točnosti ni objašnjenje dvosekundnog `SaveChanges` zastoja. Slobodno je oko 48 GiB, postojeći logovi ukupno oko 1,48 GiB, pretežno stariji SQL log. Nisu brisani.

Tijekom ugradnje dijagnostike kolektor je više puta uredno restartan kroz Ctrl+C uz provjeru identiteta procesa i završetka prije publish-a. To su **planirane rupe u prikupljanju**, nisu dokaz nenadziranog reconnecta i ne brišu se iz coverage evidencije. Novi proces automatski se pretplatio na postojeći paper TWS. Broker naloge blokiraju postojeći read-only i trading-off argumenti. SQLite provjera nalazi nula naloga, izvršenja, tradeova, rezervacija i modela. N3 nije mijenjao postojeću operativnu konfiguraciju, baznu shemu ili povijesne podatke.

Lokalni artefakti su `TestResults/n3-20261120`; sažeti dokaz je [N3 evidence](evidence/n3-collection-latency-2026-11-20.json). Dio kratkog uzorka prikupljen je uz restore/build/test i offline replay na istom računalu: ne uspoređivati ga kao izolirani benchmark s ranijim tržišnim uvjetima.

## Provjera koda

Puna regresija: **780/780**, uz 2 testa read-only izvještaja. Testovi pokrivaju histogram bez gubitka repa, neovisne async scopeove, wall-clock korekciju naspram monotone queue metrike, odvajanje najsporijeg pipelinea od queue outliera, vremenski flush, drain pri stopu i SQL log bez privatnih parametara. Postojeći stock/FX, overflow/reconnect i live/replay testovi ostaju aktivni. Obvezni IBKR publish/DLL guard mora proći prije objave commita.

Ponovljeni N2 dvosatni EUR/USD replay ima **isti input i output hash**. To potvrđuje nepromijenjene offline odluke/rezultate na istom ulazu. Ne dokazuje da je raspored live radnika pri zagušenju jednak offline rasporedu; to ostaje zasebna provjera.

## Ponovljiva prihvatna sesija — još potrebna

1. Zadržati paper TWS i računalo budnima. Nakon posljednjeg restarta odvojiti prve dvije minute kao startup, pa snimiti **barem 60 minuta aktivnog feeda**, poželjno 60–120 minuta u dnevnoj sesiji. Ne miješati različite PID-ove ili više instrumenata kao jedan uzorak. Zapisati namjerno opterećenje/backtest/build i prekide; kasnije ponoviti usporedivu sesiju.
2. Unaprijed zadani ciljevi za regularni dio: pipeline p99 svake minute ≤50 ms i maksimum ≤250 ms; monotoni queue p99 ≤100 ms i maksimum ≤500 ms; flush maksimum ≤2.000 ms i enqueue-to-write maksimum ≤7.500 ms pri konfiguriranom 5s flushu. Nula processing/storage grešaka i queue dropova. Startup, poznati reconnect i source-age outlieri prikazuju se zasebno, ne brišu. To su **operativni istraživački ciljevi**, ne novi trading gateovi.
3. Napraviti vremenski ograničen read-only izvještaj, bez prepisivanja starog rezultata:

```powershell
python scripts/analyze-collection-latency.py --log C:/PrivateGit/aiTradingBot/data/eurusd-collection/logs/trading-2026-11-20.log --from 2026-11-20T19:20:00Z --to 2026-11-20T20:20:00Z --instrument-id FX-CASH-EUR-USD-IDEALPRO --output C:/research/n3-session-new.json
```

Navedeni sati su primjer; koristiti stvarni raspon nakon završnog restarta. Alat ne šalje naloge, ne mijenja log/bazu i odbija postojeću izlaznu datoteku. `worstWindow...P99` je najviši minutni kvantil, ne globalni kvantil. Duplikati logova i drugi instrumenti ne povećavaju broj tick prozora. Nedostatak tick/storage dokaza ne može proći pragove. Sam `localLatencyThresholdsMet` **ne zatvara N3**.

4. Uz izvještaj provjeriti `TickCoverageGapRecords`, collection status, stanje sata, rast/manifest Parqueta, disk i logove. Označiti stvarne i planirane prekide. Iz istih zamrznutih kotacija ponovno usporediti featuree/signale, bez promjene vremena ili tolerancija radi prolaza.
5. Za svaki preostali `QualitySave` outlier povezati isti UTC interval s SQL i transaction zapisom te opterećenjem. Ako SQL vrijeme ne pokriva cijeli `SaveChanges`, dodatno izmjeriti transaction/reader completion i I/O. Popraviti potvrđeni uzrok i ponoviti istu proceduru. **Ne spuštati kriterije niti proglasiti problem riješenim zato što je prosjek dobar.**

N4 nije započet. Za zatvaranje N3 još nedostaju objašnjeni outlieri i reprezentativan ponovljeni operativni dokaz nakon popravka.
