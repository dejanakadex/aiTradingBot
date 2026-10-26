# Operativno EUR/USD prikupljanje

25.10.2026.: pripremljen je i pokrenut zaseban **ForexCollection** profil postojećeg web hosta. Prikupljanje ne zahtijeva model, izvršne limite ni dopuštenje trgovanja. To nije završen FX-11 model shadow ili FX-12 paper trade test.

## Konfiguracija i lokacije

[appsettings.ForexCollection.json](../TradingBot.Web/appsettings.ForexCollection.json) učitava se preko standardnog ASP.NET environment profila, nakon osnovnog `appsettings.json`. Koristi cijeli niz instrumenata: sačuvani SPY s privremeno isključenim prikupljanjem u ovom profilu te aktivni EUR/USD `CASH/IDEALPRO`, strategiju `eurusd-patterns` i minutnu povijest. Osnovni stock profil i njegova konfiguracija nisu promijenjeni.

- `TradingSettings.Enabled=false`, `AnalysisOnly`, `LiveTradingExplicitlyEnabled=false`, `ForexPaperExecutionEnabled=false` i `AutomaticPaperSignalsEnabled=false`.
- `IbkrSettings.ReadOnlyApi=true` prenosi se u postojeću adapterovu blokadu **submit/modify/cancel** za sve instrumente. To je procesna postavka koja se mijenja restartom; Read-Only API može ostati uključen i u TWS-u. Za kasniji paper test potreban je drugi pregledani izvršni profil, ne uklanjanje blokade u kolektoru.
- EUR/USD `BidAsk` tickovi te MIDPOINT barovi; povijesni lookback je 3 dana u segmentima od jednog dana, s postojećim pacingom od najmanje 11 sekundi. Kratki agregati su 5s/15s, uz postojeća pravila kašnjenja, uparivanja i kontinuiteta.
- Pattern/signal profili pripremljeni su za audit. Odobrenja signalnih verzija ostaju prazna; istraživački i izvršni profili nisu izmišljeni. Uspjela geometrija može biti zapisana kao blokirani kandidat; nema model shadow odobrenja ili naloga.
- SQLite je `C:\PrivateGit\aiTradingBot\data\eurusd-collection\trading.db`, Parquet i manifest su u susjednoj mapi `datasets`, aplikacijski paket u `app`, a privatni logovi u `logs`. Cijela runtime mapa je isključena iz Gita. Baza i podaci ostaju nakon restarta i ne šalju se na GitHub.

## Pokretanje i provjera

Na Windowsu s PowerShellom 7, .NET SDK-om i prijavljenim paper TWS-om na 7497:

```powershell
cd C:\PrivateGit\aiTradingBot
pwsh -File .\scripts\start-forex-collection.ps1 -Background
```

Za paper Gateway koristiti `-PaperPort 4002`. Zadani client ID je 96, web port 5098; moguće ih je promijeniti parametrima. Skripta objavljuje stvarni obvezni IBKR adapter, koristi stalni radni direktorij, dodaje eksplicitne command-line read-only/trading-off zaštite i odbija drugu instancu istog kolektora ili zauzeti web port. `-RuntimeDirectory` služi za eksplicitnu apsolutnu lokaciju podataka; ne koristiti više procesa nad istom bazom/datasetom. Ne pokretati druge bot instance za ovaj pilot.

Kolektor je već pokrenut tijekom ove pripreme; nije potrebno ponovno ga pokretati. Status:

- [Instrumenti](http://127.0.0.1:5098/instruments)
- [Stanje prikupljanja](http://127.0.0.1:5098/api/market-data/collection)
- [Backfill](http://127.0.0.1:5098/api/historical-backfill/jobs)
- [Parquet integritet](http://127.0.0.1:5098/api/datasets/verify)

`IdleOutsideSession` (JSON vrijednost 4), uz `brokerConnected=true` i `subscribed=true`, znači da pretplata čeka sesiju. To nije dokaz primljenih tickova. Nakon otvaranja provjeriti heartbeat/event vrijeme, paired bid/ask, rast Parquet redaka tipa bid/ask, quality/gap dijagnostiku i obradu kratkih barova. Zelen web/database health sam po sebi ne potvrđuje sve radnike, potpunost sheme ili tok podataka.

Proces ostaje u pozadini dok radi računalo. Nije instaliran Windows servis ili zadatak za automatski restart nakon restarta računala. TWS mora ostati prijavljen, API dostupan, a računalo budno. Prema aplikacijskom kalendaru sljedeće otvaranje 25.10.2026. je u 21:15 UTC / **22:15 Europe/Zagreb**. Brokerova dostupnost i stvarni tickovi tek se tada provjeravaju; nije postavljeno zasebno automatsko obavještavanje.

Bez `-Background` kolektor radi u terminalu i Ctrl+C omogućuje uredno zaustavljanje i flush. U pozadini se PID i lokacije nalaze u `collector.json`. Prije zaustavljanja tog procesa provjeriti identitet; prisilni stop može izgubiti redove koji još čekaju flush (zadano 5s), a restart mora zabilježiti prekid. Ne brisati bazu, manifest ili Parquet radi restarta. Zaustavljanje kolektora nije funkcija zatvaranja pozicija.

## Stvarni dokaz i granice

TWS API 10.49.2.0 / server 225 potvrdio je vezu na 7497. Aktivne su EUR/USD 1m i TICK pretplate. Spremljeno je **1.425 jedinstvenih MIDPOINT barova** u SQLite i 1.425 Parquet redaka u četiri datoteke; hash/manifest provjera prolazi. Broj primljenih povijesnih barova je 4.183 jer broker vraća preklapajuće podatke; to nije broj jedinstvenih spremljenih barova.

Backfill ima jednu stvarnu nepokrivenu dionicu: **22.10.2026. 19:28–21:00 UTC, 92 minutna bara**. Status je CompletedWithGaps; povijest se ne označava potpunom. Uzrok nepotpunog odgovora treba provjeriti ciljanim povijesnim zahtjevom i ponoviti gap provjeru prije research readinessa. To ne zaustavlja nove tickove. Minutni backfill ne nadomješta povijesne bid/ask tickove.

Stvarni startup otkrio je dvije stare migracije bez EF metadata: rollout i post-trade tablice nisu nastajale u novoj migriranoj bazi. Nova forward migracija `20261025193000_EnsureOperationalEvidenceTables` stvara nedostajuće tablice i indekse uz očuvanje postojećih redaka. Prije primjene nad operativnom bazom napravljena je konzistentna SQLite kopija `before-evidence-schema-repair.db`. Test sada provjerava **svaku mapiranu tablicu i stupac**, restart te očuvanje postojećeg rollout dokaza; prethodni testovi nekoliko tablica nisu bili dostatni.

Nakon popravka rollout procjena radi i zadržava blokade zbog gapova, zatvorene sesije i nedostatka istraživačkog/model dokaza. Nalozi, izvršenja, tradeovi, rezervacije i modeli ostaju prazni. [Anonimizirani dokaz](evidence/fx12-collection-startup-2026-10-25.json) odvojen je od privatnih runtime podataka. Za stvarni FX-12 još su potrebni kvalitetan quote dataset, eksplicitne provjerene istraživačke pretpostavke, evaluacija/trening/odobrenje modela, FX-11 shadow uzorak i pregledani broker/risk uvjeti iz [paper protokola](FX12_PAPER_PROTOCOL.md).

## Provjera 26. listopada 2026.

U 08:09 UTC / 09:09 Zagreb Web kolektor je aktivan, ali broker veza je `Failed`. Zadnji quote je **25.10.2026. 22:45:00 UTC / 23:45 Zagreb**. Tada `SocketException 10054` bilježi da je udaljena strana zatvorila vezu. Od 22:45:03 UTC reconnect dobiva `10061`, odnosno lokalni 7497 odbija spajanje. U jutarnjoj provjeri nema TWS/Java procesa ni listenera na tom portu. To potvrđuje nedostupan TWS API; ne dokazuje je li uzrok ručno zatvaranje, TWS auto-logoff ili pad aplikacije. Kolektor nastavlja reconnect. Ne postoji cjelonoćni uzorak.

Spremljeno: **10.596 uparenih callbackova = 21.192 bid/ask događaja**, tri pretplatničke sesije, 1.514 minutnih barova. Svih 623 Parquet datoteka / 22.706 redaka prolazi manifest/SHA-256 provjeru. Nema nepotpunih parova u spremljenom uzorku. Dva ranija heartbeat timeouta i reconnect intervali ostaju tick coverage praznine; to nije isto što i svaki tihi 5s interval. Otvoreni prekid nakon 22:45 nije još zapisan kao cijela završena gap dionica jer čeka reconnect. [Anonimizirani izvještaj](evidence/fx12-collection-audit-2026-10-26.json) ne sadrži privatne quote redove ni račun.

Zašto je malo nizova od 26 barova, na rasponu 21:15:00–22:45:02 UTC (računaju se samo potpuno završeni prozori dostupni prije završnog vremena):

| Mjera | 5 sekundi | 15 sekundi |
| --- | ---: | ---: |
| Očekivani završeni intervali u otvorenoj sesiji | 1.080 | 360 |
| Intervali bez spremljenog quotea | 283 | 28 |
| Intervali s quoteom, ali bez finalnog bara | 17 | 16 |
| Od tih: s brokerovim vremenom ispred primitka | 16 | 15 |
| Od tih: s kotacijom starijom od 2 sekunde | 1 | 1 |
| Finalni barovi u offline rekonstrukciji | 780 | 316 |
| Najdulji uzastopni niz u feature inputu | 28 | 41 |
| Frameovi s barem 26 barova / spremnim price featureima | 3 | 27 |

Agregator namjerno ne popunjava prazne intervale prethodnom cijenom. Jedna vremenski nevaljana kotacija potiskuje cijeli još otvoreni bar; prekid kontinuiteta ponovno pokreće feature warmup. Zato 10.596 callbackova, neravnomjerno raspoređenih, nije dokaz dugog uzastopnog niza. Prazan interval dokazuje odsutnost spremljene kotacije, ali sam ne razlikuje mirno tržište od neprimljenog podatka. Nema dokaza queue dropa u zabilježenim gapovima.

**Vremenska provjera:** 77 parova (154 događaja) ima event vrijeme do 135,0782 ms ispred lokalnog primitka; jedan par kasni 3,962 sekunde. Medijan receive-minus-event razlike je 724 ms, p95 1.278 ms. Brokerova vremena imaju rezoluciju sekunde, pa te razlike nisu čisto mrežna latencija. `w32tm /query /status` prijavljuje `0x80070426`, servis nije pokrenut. Sinkronizacija Windows sata prvi je korak; tek novi uzorak može razdvojiti lokalni clock skew, izvorno timestampiranje i druga kašnjenja. Nisu izmijenjeni izvorni datumi, tolerancije ni broj potrebnih barova.

**Live/replay ograničenje:** baza ima 778 obrađenih 5s barova i 7.780 pattern odluka; samo tri framea bez 26-bar blokade. Dva kandidata prošla su geometriju, ali su blokirana neodobrenim signalnim verzijama; 7.778 odluka odbijeno je na hard uvjetima. Offline rekonstrukcija koristi receive clock, a ne raspored live radnika. Ima dva dodatna bara s krajem `22:01:15Z` i `22:45:00Z`. Potonji bi postao dostupan u `22:45:02Z`, nakon prekida veze; za raniji nedostaje dokaz točnog uzroka. Asinkrona obrada dviju polovica i finalizacija timerom mogu se razlikovati od offline rasporeda, ali to nije potvrđeni uzrok tog događaja. Paritet nije proglašen riješenim. Nema naloga, izvršenja, tradeova, risk rezervacija ili modela.

### Ponovljiva offline dijagnostika

`fx-collection-audit` ne treba SQLite bazu niti pokreće host, TWS ili order servis. Čita eksplicitnu konfiguraciju (bez environment/user-secrets spajanja), provjerava manifest prije i nakon čitanja te rekonstruira barove i featuree postojećim klasama. Promjena manifesta tijekom čitanja odbija izvještaj: za aktivni kolektor koristiti stabilnu kopiju dataseta. Ograničenje je sedam dana i 200.000 događaja u odabranim datotekama. Broji samo session-open prozore cijele unutar raspona, uz finality delay; odbija legacy/nekompatibilne ulaze. Prazan uzorak prikazuje nule i prazne prozore, nikad readiness. Povratni kod 0 znači uspješnu dijagnostiku, ne dopuštenje trgovanja.

```powershell
dotnet run --project TradingBot.ModelOps -c Release -- fx-collection-audit --config C:\PrivateGit\aiTradingBot\TradingBot.Web\appsettings.ForexCollection.json --dataset C:\PrivateGit\aiTradingBot\data\eurusd-collection\datasets --instrument-id FX-CASH-EUR-USD-IDEALPRO --from 2026-10-25T21:15:00Z --to 2026-10-25T22:45:02Z --output C:\PrivateGit\aiTradingBot\TestResults\fx-audit-new.json
```

Izlazna mapa mora postojati, datoteka mora biti nova. Razlozi odbačenih prozora mogu se preklapati. Rekonstrukcija vidi promjene callback sesije, ali iz quoteova ne može znati svaki eksplicitni live reset bez novog callbacka. Rezultati 15s služe usporedbi; aktivni 5s pattern/signal profil nije automatski promijenjen i 26 × 15s predstavlja drugi vremenski kontekst.

### Sljedeći koraci za FX-12

1. Ponovno prijaviti paper TWS na 7497. Provjeriti TWS automatsku odjavu/restart i da računalo ne ide u sleep; stvarni uzrok gašenja još nije poznat. Kolektor treba nastaviti sam, uz postojeću zabranu naloga.
2. U Windows `Vrijeme i jezik → Datum i vrijeme` provjeriti automatsko vrijeme i uspješno izvršiti `Sinkroniziraj sada`. Ako ne uspije, popraviti Windows Time servis/dozvole; ne pomicati ručno vrijeme radi prolaza testa.
3. Prikupiti barem 60–120 minuta dnevne otvorene sesije kao sljedeći dijagnostički uzorak. To nije zajamčena količina za trening: ponovno izmjeriti kontinuitet, vremenska odbijanja, gapove, live naspram offline barova i dostatan broj patterna. Ako ponovno nedostaju barovi uz potpune pravovremene quoteove, instrumentirati kašnjenje obrade/finalizacije prije odobrenja pariteta.
4. Popraviti postojeći historical gap ciljanim backfillom kada je API dostupan, zatim pripremiti provjerene FX-10 research/rule/cost pretpostavke, replay/labele, vremensku evaluaciju i model. Tek odgovarajući stvarni FX-11 model shadow i pregledani FX-9 uvjeti otvaraju put nadziranom paper testu. Ne odobravati model na temelju ove male nedjeljne serije niti premostiti blokade radi naloga.

Lokalna dopuna prolazi 703/703 testova (sedam novih audit slučajeva) i obvezni IBKR publish/guard. Postojeći background test imao je race zbog jedne SQLite veze dijeljene između worker/poll konteksta; testni factory je ispravljen na odvojene veze u imenovanu memorijsku bazu. Runtime kolektor i strategije ovom dijagnostičkom dopunom nisu restartani ni promijenjeni.
