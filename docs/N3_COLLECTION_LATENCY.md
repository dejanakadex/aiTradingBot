# N3 — dijagnostika i pouzdanost prikupljanja

23.11.2026. **N3 je u tijeku, nije operativno zatvoren.** Implementirana su fazna mjerenja, ispravljen flush rok i smanjen manifest. Petkov ETL potvrđuje čekanje storage dovršetka, a nova dnevna sesija ponovno bilježi SQLite spremanje do 1,915 s. Specifičan okidač i trajno uklanjanje ostaju nepotvrđeni. FX-12, 25/30/31 ostaju otvoreni, bez naloga ili odobrenja modela.

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

## Stabilna večernja sesija i dopuna SQL mjerenja — 20.11.2026.

Za proces 4388 analiziran je raspon **19:20–21:05 UTC (20:20–22:05 Zagreb)**. Log daje 104 minutna tick prozora / 155.604 događaja, bez processing grešaka, source-age >2 s ili negativnih monotonic delta. SQLite u tom rasponu nema coverage gapova, dropova ni quality incidenata. Najviši minutni pipeline p99 je 11,56 ms, ali **17 minuta ima maksimum >250 ms**, najviše **1.883,22 ms**, od čega **1.882,60 ms QualitySave**. Monotoni red doseže 1.897,14 ms. Pragovi N3 zato **nisu prošli**. Storage maksimum flush-a 1.263,66 ms i enqueue-to-write 6.272,27 ms prolaze zadane pragove; storage grešaka nema. CPU kolektora najviše 0,462% računala ne objašnjava SQL repove.

Zamrznuta kopija ima 2.296 datoteka / 155.548 manifest redaka, sve s valjanim hashom. Unutar točnog audit raspona nalazi se **155.544 quote događaja / 77.772 potpuna para**, bez nepotpunih i zastarjelih parova. Dva događaja su 4,1911 ms ispred primitka, unutar postojeće tolerancije; vremena nisu mijenjana. Offline rekonstrukcija daje **1.259/1.259 finalnih 5s barova**, 1.234 framea s najmanje 26 barova te **419/419 finalnih 15s barova**, 394 zagrijana framea. Nema praznih ili potisnutih intervala. Broj događaja iz minutnih logova nije točan quote-range count: prozori logova prelaze rubove raspona; manifest uključuje rubne retke datoteka. Ne izjednačavati te brojeve niti prikazivati offline rekonstrukciju kao dokaz potpunog live signal pariteta.

**Potvrđena praznina u dijagnostici:** vrijeme ExecuteReader završava nakon početnog rezultata; kasniji Read/NextResult rad nije bio obuhvaćen SQL upozorenjem. Dodan je `Phase=ReaderLifetime` na EF `DataReaderDisposing`, uz postojeći `Phase=Execute`. Oba vremena mogu se preklapati i ne zbrajaju se. Lifetime je vrijeme do poziva disposal hooka, ne uključuje rad poslije tog hooka; kod SELECT-a uključuje i potrošačevo čitanje/materijalizaciju, pa nije čisto engine vrijeme. Nema novih SQL tekstova, parametara ili podataka računa u logu.

Nakon urednog restarta u 21:16 UTC novi proces 23344 uhvatio je UPDATE `MarketDataStreamStateRecords`: završetak **21:20:05.2689234 UTC**, ReaderLifetime **1.552,60 ms**. Isti market događaj primljen je u **21:20:03.7155497 UTC**, pipeline 1.555,53 ms / QualitySave 1.555,06 ms. Time je gotovo cijeli zastoj smješten u kasniji dio rada SQL readera, a ne procjenu kvalitete ili agregaciju. Sam hook još ne razlikuje SQLite commit/sync, disk/driver i čekanje raspoređivanja. Pregled [Microsoft.Data.Sqlite 10.0.12 readera](https://raw.githubusercontent.com/dotnet/efcore/v10.0.12/src/Microsoft.Data.Sqlite.Core/SqliteDataRecord.cs) potvrđuje zasebne `sqlite3_step` / `sqlite3_reset` pozive nakon početnog retka; to objašnjava prazninu Execute mjerenja, ali nije dokaz koji je native poziv čekao u ovoj sesiji.

Pasivno čitanje WAL-index brojača s korakom 20 ms u početnih sedam minuta uhvatilo je osam checkpoint intervala od približno 20 ms, ali nijedan spori save u tom intervalu. Nakon restarta, oko navedenog sporog UPDATE-a brojači ostaju bez checkpoint napretka; zapis se pojavljuje na mxFrame 843, ispod uobičajenog praga 1.000. To **ne potvrđuje automatski checkpoint kao uzrok**. Promjene uzorkovane izvana nisu potpun engine trace. [SQLite WAL dokumentacija](https://www.sqlite.org/wal.html) opisuje i checkpoint u commit niti; [synchronous dokumentacija](https://www.sqlite.org/pragma.html#pragma_synchronous) razlikuje trajnost FULL i NORMAL. Nisu promijenjeni checkpoint, pooling, busy timeout ili trajnost baze na temelju pretpostavke.

Drugi UPDATE završava u **21:25:21.5536681 UTC**, ReaderLifetime **758,74 ms**. Unutar njegova intervala Windows PhysicalDisk(_Total) bilježi **6 zahtjeva u redu u 21:25:21.1700938 UTC**, dok WAL ostaje bez checkpoint napretka (mxFrame 514 → 520, nBackfill/nBackfillAttempted 0). To je vremenska korelacija s diskovnim čekanjem, ne dokaz da su svi zahtjevi kolektorovi niti identifikacija konkretnog procesa, filtra ili diska. Jednosekundni prosjek vremena pisanja nije trajanje pojedinačnog flush-a. Dijagnostički uzorak nakon restarta uključuje lokalni build/test/audit i nije nova prihvatna sesija.

Windows detaljno DiskIO/FileIO snimanje odbijeno je s **0x80070005 (Access denied)** jer zahtijeva administratorski proces. Pasivni brojači diska ostaju dostupni, ali ne identificiraju pojedinačni SQLite flush. Za sljedeći nivo dokaza potrebno je kratko administratorsko snimanje preko Windows Performance Recorder-a, uz UTC korelaciju istog ReaderLifetime upozorenja. ETL s lokalnim putanjama zadržati lokalno, ne commitati ga. Ne isključivati antivirus niti oslabljeti trajnost radi boljeg mjerenja.

Provjera ove dopune: **781/781 .NET testova**, 2 testa izvještaja, obvezni IBKR publish/DLL guard prolaze. Novi test namjerno usporava tek drugi SQL rezultat i potvrđuje da ga Execute mjerenje propušta, a ReaderLifetime bilježi. Odluke/trading logika nisu mijenjane; prethodni N2 paritet ostaje prethodni dokaz, ne novo mjerenje. Kolektor je uredno restartan radi dijagnostike; planirana rupa ostaje u evidenciji. Naloga, izvršenja, tradeova, rezervacija i modela u operativnoj bazi i dalje je nula.

Artefakti: `TestResults/n3-stable-20261120`; sažetak [stabilne sesije](evidence/n3-stable-session-2026-11-20.json). **N3 ostaje otvoren:** nedostaje potvrđen niži uzrok sporog SQL dovršavanja, ciljani popravak i usporediva prihvatna sesija nakon njega. N4 nije započet.

## ETL potvrda diskovnog čekanja — 20.11.2026.

Korisnik je u administratorskom prozoru potvrdio aktivno DiskIO/FileIO snimanje i uspješno ga spremio. Prethodna neprivilegirana provjera `wpr -status` nije ga vidjela; njezin rezultat nije bio pouzdan dokaz da administratorsko snimanje ne radi. **Administratorska blokada prikupljanja ETL-a je riješena.** Analizirana snimka obuhvaća **21:29:50.608–21:36:52.324 UTC (22:29:50–22:36:52 Zagreb)**, bez izgubljenih događaja/buffera.

Pet primarnih SQL zastoja vremenski odgovara zapisu u `trading.db-wal` procesa kolektora 23344, na fizičkom disku 0:

| Kraj SQL operacije (Zagreb) | SQL faza | SQL ms | Najsporiji pripadajući WAL write ms |
| --- | --- | ---: | ---: |
| 22:30:42.652 | ReaderLifetime | 1.377,968 | 1.314,066 |
| 22:31:37.719 | ReaderLifetime | 965,087 | 963,177 |
| 22:33:02.759 | ReaderLifetime | 562,531 | 561,162 |
| 22:34:13.104 | Commit | 705,897 | 701,172 |
| 22:35:48.953 | Commit | 1.558,483 | 1.554,364 |

Za prvi slučaj kernel `FileIoFlush` iste WAL datoteke traje **1.377,808 ms** (SQL 1.377,968 ms). Isti 12 KiB write povezan je kroz originalni IRP s StorPort dispatch/completion intervalom **1.314,044 ms**, `RequestDuration` **1.314,036 ms**, dok BuildIo/StartIo traju samo **0,0037/0,0077 ms**. Zahtjev završava uspješno. Čekanje je tako dokazano i ispod datotečnog sustava, u storage putu. Istodobno čekaju drugi zapisi na istom disku. Dodatna Begin/Execute čekanja drugih contexta preklapaju se s tim zastojima, što je u skladu s posljedicom SQLite single-writer zaključavanja, a ne zasebnom potvrdom sporog SELECT-a.

**Zaključak:** neposredni uzrok tih dugih save/commit poziva jest čekanje završetka diskovnog zapisa/flush-a. To nije dokaz kvara SSD-a, antivirusnog skeniranja ni konkretnog firmware buga. Xperf `Disk SrvT` se ne smije tumačiti izolirano kao fizičko vrijeme svake paralelne operacije; u ovom nalazu korišteni su ukupni intervali i povezani StorPort zahtjev. [Microsoftov opis storage mjerenja](https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/high-cpu-usage-issue-smb-server) uključuje i vrijeme reda upravljačkog programa.

Snimka sama piše **1.799.358.464 bajta** svojih ETL podataka, naspram 45.043.712 bajtova WAL writeova. Zato učestalost i opterećenje ovog intervala nisu neopterećeni benchmark. Zastoji su dokazani i prije WPR-a, ali snimanje može utjecati na raspored/opterećenje. Sirovih 1,84 GB ETL-a i CSV izvozi s lokalnim putanjama ostaju izvan Gita; [sažeti dokaz i hash](evidence/n3-storage-trace-2026-11-20.json) omogućuju ponovnu provjeru.

### Hipoteza i kontrolirani sljedeći test

Disk je **KINGSTON OM8SEP4512Q-AA**, firmware **SBI00111**, na Acer Aspire A517-58GM. Računalo je na mrežnom napajanju s planom Balanced: primarna/sekundarna NVMe tolerancija 15/100 ms, PCIe ASPM 2. U izdvojenom intervalu StorNVMe šalje Set Features za FID 2 s traženim PS3 pa PS0 neposredno uz zastoj. [Power Management polje](https://learn.microsoft.com/en-us/windows/win32/api/nvme/ns-nvme-nvme_cdw11_feature_power_management) označava traženo power stanje; vremenska blizina još ne dokazuje uzročnost. [Microsoftova NVMe pravila](https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/power-management-for-storage-hardware-devices-nvme) opisuju odabir idle stanja prema tim tolerancijama; 0/0 ms odgovara AC tolerancijama performance plana.

Pripremljen je opt-in `scripts/test-nvme-power.ps1`:

```powershell
# Pregled bez promjena:
./scripts/test-nvme-power.ps1
# Samo u administratorskom PowerShellu, na punjaču i uz aktivne kotacije:
./scripts/test-nvme-power.ps1 -Run -Minutes 10
```

Skripta kopira aktivni plan u jedinstveni privremeni plan, mijenja **samo dvije AC NVMe tolerancije na 0 ms**, aktivira kopiju i nakon mjerenja vraća izvorni plan. Izvorne postavke, battery/DC, PCIe ASPM, zaštite, SQLite i bot ne mijenjaju se. Ako korisnik tijekom testa sam odabere drugi plan, taj se izbor čuva. Briše se samo vlastita neaktivna testna kopija. JSON s UTC početkom/krajem i potvrdom cleanup-a nalazi se u `TestResults/nvme-power-<guid>/session.json`. Prozor treba ostati otvoren; nasilno gašenje procesa/računala može preskočiti finally. Za takav slučaj skripta unaprijed ispisuje točnu naredbu za vraćanje izvornog plana.

Syntax/preview provjere prolaze; četiri izolirana mock scenarija potvrđuju normalan povratak, pogrešku prije aktivacije, pogrešku nakon aktivacije i očuvanje naknadnog korisnikova izbora. To nije dokaz da konkretni Windows prihvaća/primjenjuje testni plan; stvarni test i njegov rezultat moraju se zasebno potvrditi. Pri usporedbi koristiti potpune minutne prozore unutar testa, svjež feed i isti PID, bez WPR-a/builda/backtesta. Kratko poboljšanje nije dokaz trajnog rješenja: potreban je povratni uzorak nakon restore-a i kasnije reprezentativna dnevna prihvatna sesija. Ako nema aktivnih kotacija, rezultat je **nedovoljan uzorak**, nikad prolaz.

Botov kod i persistence pravila u ovoj dopuni nisu mijenjani. Nema razloga oslabiti `synchronous`, gasiti antivirus ili naslijepo mijenjati checkpoint/indexe. **N3 ostaje otvoren:** dokaz diskovnog čekanja postoji; okidač na razini SSD-a/napajanja i provjereno rješenje još nedostaju.

## Usporedni NVMe test — 20.11.2026.

Stvarni administratorski test trajao je **22:01:05–22:11:05 UTC (23:01–23:11 Zagreb)**. Potvrđene su ciljane AC tolerancije 0/0 ms; DC vrijednosti 50/100 ms ostale su iste. Skripta je uredno vratila izvorni **Balanced** plan s AC 15/100 ms i uklonila vlastitu testnu kopiju, bez greške. Nema trajne promjene postavki.

FX sesija se zatvorila u **22:00 UTC**; posljednji tick je 21:59:59 UTC. Zato odsutnost sporih quality zapisa nakon početka testa **nije dokaz poboljšanja bota**: novih kotacija više nema. N3 prihvatna sesija i live/replay paritet time nisu provjereni.

Umjesto toga izveden je izolirani storage pokus na istom C: disku, bez pristupa operativnoj bazi/brokeru. Python SQLite 3.53.1, jedna mala testna baza, isti UPDATE + commit svakih 500 ms, **WAL + synchronous FULL** u obje faze. Samo testna baza ima auto-checkpoint 0 kako checkpoint ne bi pao u samo jednu fazu; WAL je ograničen trajanjem i malim brojem zapisa. To ne mijenja operativne SQLite postavke. Ista konekcija/baza koristi se u oba petominutna intervala, prvo B (testni plan), zatim A (izvorni plan nakon automatskog povratka). Prvih 30 sekundi svake faze isključeno je unaprijed. Sve periodične provjere potvrđuju očekivani plan i AC napajanje.

| Faza | Uzorci nakon zagrijavanja | Medijan ms | p95 ms | p99 ms | Maksimum ms | Zapisi >250 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| B — NVMe AC 0/0 | 539 | 1,77 | 2,31 | 3,99 | 829,41 | 1 |
| A — izvorni Balanced | 539 | 9,77 | 10,72 | 12,40 | 13,11 | 0 |

Rezultat pokazuje razliku u uobičajenoj latenciji ovog malog workload-a. Međutim, u B fazi postoji **829,41 ms** commit u **22:07:33.910–22:07:34.739 UTC**, pa sama promjena NVMe tolerancija **nije potpuno uklonila zastoj**. To nije mjerenje EF Core pipelinea, nema randomiziranog ponavljanja niti kontrole svih pozadinskih Windows operacija. Povoljan p99 ili mali broj outliera ne dokazuje uzročnost/rješenje i ne zatvara N3.

[Strukturirani rezultat i hashovi](evidence/n3-nvme-comparison-2026-11-20.json); lokalni ulazi i skripta: `TestResults/n3-power-20261120`. Nakon usporedbe testna konekcija se zatvara; operativni kolektor ostaje aktivan s izvornim postavkama i zabranom naloga.

Sljedeći smisleni koraci: tijekom aktivne dnevne sesije ponoviti reprezentativno mjerenje; preostali outlier povezati s kratkom storage snimkom pri poznatim power postavkama. Tek potom razdvojiti NVMe idle, PCIe ASPM, firmware/driver i drugo I/O opterećenje. Ovaj pokus nije razlog za trajno mijenjanje plana, isključivanje antivirusne zaštite, slabljenje SQLite trajnosti ili naslijepo prepisivanje persistence sloja. **N3 ostaje otvoren.**

## Kompaktni manifest i NVMe dovršavanje — 20.11.2026.

Dodatno povezivanje postojeće snimke preko IRP/SRB/CID daje 649 završenih NVMe naredbi u izdvojene tri sekunde, bez neuparenih početaka. WAL write CID 49 (opcode 1) predan je na relativnih 50.665.225 µs i dovršen na 51.979.240 µs: **1.314,015 ms**, uz uspješan status. Čekanje se, dakle, vidi i između StorNVMe predaje i dovršetka naredbe. To još ne razdvaja firmware/hardver, napajanje i raspoređivanje dovršetka. Šest obližnjih deallocate naredbi završava za 1,57–3,72 ms; nema dokaza da je sam TRIM blokirao 1,5 s i nema razloga isključivati ga.

U istoj sedmominutnoj snimci fizički zapisi kolektora u manifest iznose **423.047.168 bajtova** kroz 6.543 diskovna zahtjeva, naspram 45.043.712 bajtova WAL zapisa. Manifest se pri svakoj trajno spremljenoj grupi ponovno serijalizira u cijelosti. Prethodno formatiranje s uvlačenjima povećavalo je taj trošak s rastom inventara. WPR također piše vlastite podatke; ovo nije neopterećen benchmark niti dokaz da manifest uzrokuje WAL zastoje.

`ParquetMarketDatasetStore` sada zapisuje kompaktni JSON. Na kopiji operativnog inventara s **11.895 datoteka**, ista struktura smanjuje se sa **7.482.927 na 6.269.611 bajtova**: **1.213.316 bajtova / 16,2% manje**. To je ušteda veličine manifesta u tom uzorku, ne izmjereno ubrzanje SQLite-a ili ukupnog diska. Potpuno prepisivanje manifesta ostaje; veća rekonstrukcija pohrane nije dio ove izmjene.

Čitanje starih formatiranih manifesta ostaje podržano. Idempotentno ponavljanje iste grupe ne prepisuje stare bajtove; tek normalno dodavanje nove grupe zapisuje novi kompaktni manifest i njegov odgovarajući SHA256. Nema promjene Parquet datoteka, kanonskih hashova zapisa, sheme, flush ritma ni SQLite trajnosti. Hash samog manifesta ovisi o bajtovima i zato se pri formatiranju mijenja; zamrznuti izvorni skupovi nisu preformatirani.

Provjere: **782/782 testova**, uključujući stari manifest → idempotentni retry → novi append s očuvanim starim Parquet bajtovima/hashovima; obvezni IBKR publish/DLL guard prolazi. Na zasebnoj kompaktnoj kopiji stvarnog snapshot-a provjereno je **2.296 datoteka / 155.548 redaka**, bez pogreške. Cijeli rezultat collection audita jednak je izvornome: **77.772 potpuna para**, 1.259 finalnih 5s barova i 1.234 zagrijana framea. Izvorni snapshot hash ostaje očuvan. To nije novi full-trade replay niti prihvatna sesija.

Kolektor 23344 uredno je zaustavljen u **22:38:23 UTC (23:38:23 Zagreb)** i zamijenjen verzijom s kompaktnim zapisom, PID 19572. Hash deployanog Infrastructure DLL-a odgovara testiranom buildu, health vraća 200, broker je spojen i pretplate aktivne izvan sesije. ReadOnly API, onemogućeni trading, FX paper execution i automatski signali ostaju potvrđeni. Nije ručno prepisan operativni manifest; novi format primjenjuje se na sljedeći normalni zapis. Planirani restart nije dokaz neprekinute prihvatne sesije.

[Strukturirani dokaz](evidence/n3-manifest-io-2026-11-20.json), lokalni artefakti `TestResults/n3-storage-deep-20261120`. **N3 ostaje otvoren:** ovo uklanja izmjereno nepotrebno zapisivanje, ali ne potvrđuje uklanjanje dugih NVMe/WAL čekanja. Sljedeći korak je aktivna dnevna sesija s izvornim Balanced planom, bez WPR-a/builda/backtesta, usporedba QualitySave/ReaderLifetime/commit/queue repova i integriteta prikupljanja. Ako zastoj ostane, povezati novi događaj sa storage snimkom; ne mijenjati trajnost ni vremenske pragove kako bi test izgledao uspješno. N4 nije započet.

## Dnevna provjera nakon kompaktnog manifesta — 23.11.2026.

Kolektor PID **2440**, commit `1e0f7e2`, pokrenut je oko **09:17 Zagreb**. Sat je tada zaostajao oko **550–558 ms**, više od FX future tolerancije 250 ms. To je uzrokovalo ponavljane quality odbijenice i ponovne tick pretplate. Običan resync i restart servisa nisu uklonili opaženo odstupanje. Korisnikova jednokratna korekcija sata oko **09:30:22**, s automatskim vraćanjem `MaxAllowedPhaseOffset=1`, smanjila ga je na oko 0,1 ms. Windows je u 09:30:39 ponovno prijavio uspješnu sinkronizaciju bez greške. Posljednja ponovna pretplata je 09:30:33; kumulativni broj reconnectova 33 kasnije se nije povećao. Početni interval nije prihvatni uzorak i ostaje u logovima. Pri analizi oko 11 sati tri NTP uzorka pokazuju **37–40 ms**; to nije kontinuirano mjerenje niti usporedba s brokerovim satom.

Zamrznuta je kopija podataka za **08:32–09:58 UTC (09:32–10:58 Zagreb)**, bez restarta kolektora. Time su isključene korekcija sata, početni reconnecti i prva puna minuta nakon oporavka. Provjera **1.908 Parquet datoteka / 173.794 reda** prolazi sve hashove. U točnom rasponu nalazi se **173.608 quote događaja / 86.804 potpuna para**, jedna callback sesija, bez nepotpunih parova, future/stale kotacija ili resetiranja audita. Rubne datoteke sadrže nešto događaja izvan raspona i audit ih filtrira.

- 5s: **1.031/1.031** finalnih barova, **1.006** frameova s najmanje 26 barova i spremnim cjenovnim featureima.
- 15s: **343/343** finalnih barova, **318** takvih frameova.
- SQLite u intervalu: **0 coverage gapova, 0 dropova, 0 quality incidenata**. Operativnih naloga, izvršenja, tradeova, rezervacija i modela i dalje je nula.

Log daje **85 minutnih tick prozora / 172.280 događaja**; njihove granice nisu iste kao točan audit raspon. Nema processing/storage grešaka, source-age >2 s ni negativnih queue clock delta. **17/85** prozora ipak ima pipeline maksimum iznad 250 ms.

| Mjera | Petak 20.11., 105 min | Ponedjeljak 23.11., 86 min | Prihvatni prag |
| --- | ---: | ---: | ---: |
| Najviši minutni pipeline p99, ms | 11,56 | 11,56 | ≤50 |
| Pipeline maksimum, ms | 1.883,22 | **1.915,50** | ≤250 |
| QualitySave maksimum, ms | 1.882,60 | **1.914,67** | unutar pipelinea |
| Monotoni queue maksimum, ms | 1.897,14 | **1.942,36** | ≤500 |
| Flush maksimum, ms | 1.263,66 | 358,02 | ≤2.000 |
| Enqueue-to-write maksimum, ms | 6.272,27 | 5.446,65 | ≤7.500 |

Najviši minutni queue p99 također ne prolazi: **1.806,64 ms**, prag 100 ms. Najveći CPU je 0,521% cijelog računala. Najsporiji pipeline počinje u **09:26:39.789 UTC**; pripadajući UPDATE `MarketDataStreamStateRecords` ima ReaderLifetime **1.912,104 ms**, gotovo cijelo QualitySave trajanje. U rasponu postoji 19 ReaderLifetime, 9 Commit, 8 Execute i 4 Begin sporih SQL upozorenja; to nisu nužno neovisni zastoji i brojevi se ne zbrajaju kao broj jedinstvenih incidenata.

**Zaključak:** kompaktni manifest nije uklonio duga SQLite čekanja. Kraći flush u ovom uzorku nije dokaz uzročnog poboljšanja jer se razlikuju trajanje sesije, količina/ritam kotacija i inventar. Nova sesija ima isti SQL timing obrazac kao raniji storage problem; nema novog ETL-a kojim bi se svaki današnji outlier zasebno pripisao NVMe-u. **N3 ne prolazi prihvatne pragove.** Offline agregati jesu potpuni, ali ovo nije live feature/signal paritet, novi trading replay ili potvrda profitabilnosti.

### Sljedeći kontrolirani pokus uz stvarne kotacije

Računalo je na mrežnom napajanju, aktivan je izvorni Balanced plan (primarna NVMe AC tolerancija 15 ms). Postojeći provjereni `scripts/test-nvme-power.ps1` spreman je za **20 minuta**, uz isti PID i aktivni feed. Korisnik ga mora pokrenuti u administratorskom PowerShellu. Nakon automatskog povratka izvornog plana ostaviti još 20 minuta aktivnog prikupljanja. Usporediti potpune minute prije/tijekom/poslije, izostaviti rubne minute i zabilježiti promjenu quote ratea, sve repove latencije i integritet. Tijekom testa ne pokretati WPR, build, audit ili replay. Ni povoljan dvadesetominutni rezultat sam ne zamjenjuje prihvatnu sesiju od barem 60 minuta. Petkov izolirani SQLite test bez feeda ostaje koristan prethodni dokaz, ali nije ovaj pokus. **Naknadno dovršeno istog dana; rezultat je u sljedećem odjeljku.**

Ova dopuna mijenja samo dokumentaciju/dokaze. Kolektor ostaje aktivan s istom verzijom i zabranom naloga, bez promjena SQLite trajnosti ili quality pragova. Dva testa izvještaja prolaze; audit je izvršen iz već provjerenog buildu s 782 testa. Ne navoditi novi lokalni full-suite run: kod nije promijenjen. [Strukturirani rezultat](evidence/n3-day-session-2026-11-23.json); lokalni log, snapshot, audit i hashovi su u `TestResults/n3-day-20261123`.

## NVMe usporedba uz aktivni feed — 23.11.2026.

Korisnik je pokrenuo postojeću reverzibilnu skriptu na 20 minuta. Test traje **10:23:29.649–10:43:30.452 UTC (11:23:29–11:43:30 Zagreb)**. Potvrđene su AC NVMe tolerancije **0/0 ms**, uz nepromijenjene DC 50/100 ms. Skripta je automatski vratila izvorni Balanced plan i AC **15/100 ms**, uklonila svoju privremenu kopiju i završila bez greške. Kolektor **PID 2440** nije restartan. Povratno prikupljanje trajalo je barem do **11:03:30 UTC (12:03:30 Zagreb)**.

Lagane provjere jednom u minuti potvrđuju mrežno napajanje, aktivnu pretplatu i nepromijenjen broj reconnectova 33. Tijekom mjerenja nisu pokretani lokalni build, audit, replay ili WPR; dva puta pročitana je mala završna sekcija postojećeg loga. Statusne HTTP provjere stvaraju uobičajene log zapise i nisu potpuno bez utjecaja na opterećenje. Nema novog ETL-a niti dokaza da svaki konkretni zastoj pripada istom nižem storage mehanizmu kao petkom.

Iz svake dvadesetominutne faze isključena je prva i zadnja minuta. Zatim su uzeti samo **cijeli reporting prozori** čiji početak (`AsOfUtc − ElapsedSeconds`) i kraj ulaze u preostali raspon: **17 tick prozora i 16 storage prozora po fazi**. Tick trajanje dolazi iz log zaglavlja, storage trajanje iz JSON metrike. Ista stroža selekcija na ranijem jutarnjem uzorku daje 84 prozora / 16 sporih minuta, naspram ranijih 85/17 po samom vremenu izvještaja; izvorni raniji izvještaj nije prepisan. Kvantili ostaju maksimumi minutnih gornjih granica, ne globalni percentili.

| Mjera | Raniji Balanced (A0) | NVMe AC 0/0 (B) | Vraćeni Balanced (A2) |
| --- | ---: | ---: | ---: |
| Potpuni tick prozori | 84 | 17 | 17 |
| Quote događaji u tim prozorima | 168.933 | 23.094 | 25.414 |
| Događaji/s | 33,30 | 22,47 | 24,70 |
| Pipeline maksimum, ms | 1.915,50 | **985,98** | **908,59** |
| QualitySave maksimum, ms | 1.914,67 | **985,19** | **907,53** |
| Monotoni queue maksimum, ms | 1.942,36 | **1.046,63** | **919,53** |
| Najviši minutni pipeline p99, ms | 11,56 | 3,68 | 12,72 |
| Najviši minutni queue p99, ms | 1.806,64 | **1.019,80** | **919,53** |
| Pipeline minute >250 ms | 16 | **2** | **3** |
| Flush maksimum, ms | 358,02 | 224,28 | 1.097,10 |
| Enqueue-to-write maksimum, ms | 5.446,65 | 5.330,93 | 6.216,67 |

**Obje faze ne prolaze N3:** pipeline maksimum, queue maksimum i queue p99 prelaze unaprijed definirane pragove. Najsporiji B događaj započinje u **10:35:06.112 UTC** i gotovo cijelih 986 ms provodi u QualitySave. Najsporiji A2 događaj počinje u **10:52:17.583 UTC**, uz 907,53 ms QualitySave. Nema processing/storage grešaka, source-age >2 s ni negativnih queue delta u odabranim prozorima. Četiri spora SQL upozorenja u svakoj eksperimentalnoj fazi nisu četiri nužno neovisna storage incidenta.

Naknadno zamrznut cijeli 40-minutni raspon ima **876 provjerenih datoteka / 56.946 redaka**, od čega se **56.788 quote događaja / 28.394 potpuna para** nalazi unutar zadanih granica. Potvrđeno je svih **479 očekivanih 5s barova**, s **454** framea spremna za cjenovne featuree; 15s ima **159/159** barova i **134** takva framea. SQLite nema coverage gapova, dropova ili quality incidenata u rasponu. Nalozi, izvršenja, tradeovi, rezervacije i modeli ostaju nula. Završni NTP uzorci odstupaju 35–43 ms. Ovo je audit spremljenih podataka, ne potvrda live signalnog vremena ili paper izvršenja.

**Zaključak:** postavljanje tih dviju NVMe tolerancija na 0 nije uklonilo zastoje. Manji maksimum u odnosu na jutro nije dovoljan za pripisivanje poboljšanja postavkama: i povratna faza ima manji maksimum, tržišni tok je slabiji, a uzorci su kratki i sekvencijalni. Nisu ispitani svi PCIe/driver/firmware mehanizmi, pa rezultat ne isključuje svaku hipotezu napajanja. **Nema osnove za trajnu promjenu plana. N3 ostaje otvoren.**

Sljedeći programski korak je pregled i priprema ciljane promjene blokirajućeg spremanja izvedenog quality stanja: sada svaki događaj čeka `SaveChangesAsync` prije nastavka pipelinea. Prije implementacije treba eksplicitno odrediti redoslijed, ponašanje nakon restarta, ograničenje reda, greške i trenutačnu blokadu neispravnih podataka. Trajnost naloga/pozicija, sirove kotacije i incidenti ostaju zaštićeni; ne uvoditi neograničeno pozadinsko spremanje niti popuštati pragove da bi test prošao. To nije već implementiran popravak i ne uklanja zasebnu otvorenu istragu drivera/firmwarea.

[Strukturirani rezultat i hashovi](evidence/n3-live-nvme-comparison-2026-11-23.json); lokalni artefakti `TestResults/n3-live-power-20261123`. Ova dopuna mijenja samo dokaze/status, bez novog deploya ili naloga. Kolektor ostaje na izvornom planu. N4 nije započet.
