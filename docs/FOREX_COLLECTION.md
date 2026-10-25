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
