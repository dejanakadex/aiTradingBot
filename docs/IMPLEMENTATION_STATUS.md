# Implementation status

Ovaj dokument je checkpoint za nastavak rada na branchu `trading-bot-v2`. Svaka točka mora završiti testovima, zelenim CI-jem i jasnim izvještajem prije prelaska na sljedeću.

## Trenutno stanje

| Točka | Status | Sažetak |
| --- | --- | --- |
| 0 — Razvojni baseline | Završeno | .NET 10 LTS, clean repository, GitHub Actions, Dependabot i početnih 195/195 testova. Realni IBKR adapter još treba zaseban build sa službenim DLL-om. |
| 1 — Multi-instrument temelj | Završeno | Nova `Instruments` konfiguracija, legacy `Symbols` fallback, per-instrument timeframeovi/metadata, stabilni signal/correlation identiteti i verzije pipeline ugovora. CI: 200/200 testova, 0 warninga i 0 grešaka. |
| 2 — Registry i onboarding | Završeno | Persistirani registry, auditirane tranzicije, broker metadata, idempotentan startup sync i per-instrument execution gate. CI: 206/206 testova, 0 warninga i 0 grešaka. |
| 3 — Canonical market data | Završeno | Verzija `market-data-v2`: instrument/event/receive/source identitet, bid/ask/trade/bar događaji, finalnost, persistirani stream statusi i fail-closed quality gate. CI: 213/213 testova, 0 warninga i 0 grešaka. |
| 4 — Historical backfill | Završeno | Trajni segmenti/checkpointi, broker-wide pacing, exponential retry, idempotentni candle upis, gap report i automatske onboarding tranzicije. |
| 5 — Dataset storage | Završeno | Particionirani Parquet raw/research storage, bounded writer, reproduktivan read path te manifest/schema/SHA-256 provjera bez punjenja operativnog SQLitea tickovima. CI: 219/219 testova, 0 warninga i 0 grešaka. |
| 6 — Neovisna collection pouzdanost | Završeno | Per-stream heartbeat/lag/status, izolirani reconnect i pacing-safe automatski gap-fill neovisni o trading/AI readinessu. CI: 224/224 testova, 0 warninga i 0 grešaka. |
| 7 — Deterministički replay | Završeno | Parquet event-time replay s fiksnim input hashom/verzijama, istim feature/pattern kodom kao live, trajnim checkpointom, brzinom i pause/resume kontrolom. CI: 230/230 testova, 0 warninga i 0 grešaka. |
| 8 — Canonical featurei | Završeno | Verzija `features-v2` s konfiguracijskim fingerprintom, eksplicitnim `AsOfUtc`, quote freshnessom, režimom i normaliziranim liquidity/volatility vrijednostima; isti engine koriste live, Parquet backfill i replay. CI: 237/237 testova. |
| 9 — Pattern engine | Završeno | `patterns-v2`, puni signal identitet, strukturirani uvjeti/scoreovi/razlozi, long/short detekcija i izolirana deduplikacija. CI: 248/248 testova, 0 warninga i 0 grešaka. |
| 10 — Kandidati i labele | Završeno | Svi prihvaćeni, odbijeni i blokirani pattern kandidati ulaze u verzionirani research zapis; šest as-of horizonata računa direction-aware MFE/MAE, target/stop-first i neto povrat nakon troška. CI: 255/255 testova, 0 warninga i 0 grešaka. |
| 11 — Evaluacija | Završeno | Verziona walk-forward evaluacija s purged vremenskim granicama, netaknutim završnim holdoutom, expectancy/profit-factor/drawdown odabirom, cost stressom i out-of-sample segmentima. CI: 262/262 testova, 0 warninga i 0 grešaka. |
| 12 — Kalibracija/rangiranje | Završeno | `calibration-v1` izotoničke vjerojatnosti, fold-stabilan prag, baseline usporedba, auditirana ručna potvrda i fail-closed rangiranje prilika. CI: 269/269 testova, 0 warninga i 0 grešaka. |
| 13 — Portfolio risk | Završeno | R01–R02, session trade history, ispravan paper/live račun, atomske trajne rezervacije, gross/net i globalni/per-instrument/per-strategy/cooldown/korelacijski limiti. CI: 276/276 testova. |
| 14 — Signal arbitration | Završeno | Trajna deduplikacija, `Reject`/`Priority` politika bez tihog netiranja, atomski execution claim, virtualna atribucija po strategiji i zaštita izlazne količine. CI: 285/285 testova. |
| 15 — Order/position lifecycle | Završeno | R03–R08 i R12: trajni intenti, partial fillovi i provizije, koordinirani izlazi, potvrđeni cancel/modify, trajne kontrole te fail-closed restart reconciliation. CI: 290/290 testova. |
| 16 — Scalping execution | Završeno | Finalni bid/ask, latency i risk refresh; usporedba market/limit/marketable-limit troška, edge-after-cost gate i holding u sekundama. CI: 295/295 testova. |
| 17 — Shadow i paper rollout | Završeno | Trajni per-instrument rollout scorecard, automatski research/shadow/paper prijelazi, stvarni fill/slippage/latency kriteriji, fail-closed suspenzija i ručno live odobrenje. CI: 298/298 testova. |
| 18 — Numerički model i LLM | Završeno | Verzijski ML.NET LightGBM kandidat, purged walk-forward i netaknuti holdout nakon troškova, ručno odobrenje te lokalna fail-closed inferencija bez LLM poziva u entry putu. CI: 302/302 testova. |
| 19 — Stvarni IBKR adapter i dijagnostika | Implementirano i lokalno provjereno | Ponovljiv build sa službenim DLL-om, odvojena dijagnostika adaptera/veze/računa/contracta/feeda i broker `reqContractDetails` provjera. Stvarna TWS paper veza još nije potvrđena. |
| 20 — Tick-by-tick feed i trajno prikupljanje | Implementirano i lokalno provjereno | Per-instrument `AllLast`/`BidAsk` pretplate, brokersko i lokalno vrijeme, veličine, bounded Parquet tok te trajni zapis prekida tick pokrivenosti. Lokalno 308/308 testova u realnom i fallback buildu; stvarna TWS paper sesija još nije provjerena. |

## Točka 1 — izvedeno

Verifikacija: [GitHub Actions run 35847439813](https://github.com/dejanakadex/aiTradingBot/actions/runs/35847439813) — .NET 10 Release build, 200/200 testova, bez warninga i grešaka.

- Dodani su `InstrumentSettings` i normalizirani `ConfiguredInstrument`.
- Instrument definira stabilni ID, symbol, exchange, currency, security type, enabled/trading zastavice, dopuštene smjerove, strategije, timeframeove i opcionalne limite.
- Startup odbija prazne, duplicirane, nepodržane ili kontradiktorne definicije.
- Pretplate koriste sve omogućene instrumente i njihove timeframeove; onemogućeni instrument se ne pretplaćuje.
- Stari `Symbols` ostaje kompatibilni fallback dok migracija pozivatelja ne završi.
- `PipelineContext` nosi `CorrelationId`, deterministički `SignalId`, `InstrumentId`, `StrategyId` i verzije market-data/feature/pattern/strategy ugovora.
- Kontekst se prenosi s pattern kandidata u strategy i risk odluku te ulazi u postojeće JSON audit zapise bez migracije baze.
- Zadani `appsettings.json` ostaje fail-closed: SPY prikupljanje je omogućeno, a instrument-level trading dopuštenje je `false`.

## Odluke i ograničenja točke 1

- Trenutni IBKR boundary još prima `symbol`, ne cijeli broker contract. Zato validator privremeno ne dopušta dva konfigurirana instrumenta s istim simbolom na različitim burzama.
- `TradingEnabled` je konfiguracijski temelj; njegovo povezivanje s persistiranim readiness statusom pripada točki 2.
- Trenutna implementirana strategija nosi ID `deterministic-patterns`. Izvršavanje više stvarnih strategija po instrumentu dolazi kroz točke 9 i 14.
- Signal ID je deterministički iz instrumenta, strategije, patterna, timeframea i vremena signala; correlation ID je jedinstven za konkretan pipeline prolaz.
- Ova točka ne popravlja nalaze R01–R16 i ne omogućuje live trading.

## Točka 2 — izvedeno

Verifikacija: [GitHub Actions run 35850209592](https://github.com/dejanakadex/aiTradingBot/actions/runs/35850209592) — .NET 10 Release build, 206/206 testova, bez warninga i grešaka.

- Dodane su persistirane tablice `InstrumentRegistryRecords` i `InstrumentStatusTransitionRecords` s EF Core migracijom, indeksima i audit poviješću statusa.
- Startup sinkronizira cijelu `TradingSettings.Instruments` konfiguraciju. Ponovljeni startup bez promjene ne mijenja status, verziju ni vremenske oznake.
- Novi omogućeni instrument počinje u `BackfillPending`; onemogućeni instrument počinje i ostaje u `Disabled`.
- Restart nastavlja zadnji status i čuva potvrđeni broker contract ID/primary exchange.
- Promjena identiteta, timeframeova, strategija, smjerova ili limita vraća instrument na `BackfillPending` i briše zastarjele broker metadata podatke.
- Uklonjeni instrument ostaje u registru radi audita, ali mu se gase collection i trading dopuštenja.
- Statusne tranzicije su eksplicitno ograničene, bilježe razlog i trigger te koriste očekivani status/verziju za zaštitu od zastarjelih upisa.
- `TradingEnabled` predstavlja samo korisnikov zahtjev. Efektivni paper/live nalog dodatno zahtijeva odgovarajući persistirani readiness status.
- `TradingExecutionGuard` fail-closed provjerava registry za svaki instrument prije paper/live slanja naloga.
- Read-only `GET /api/instruments` prikazuje konfiguraciju, onboarding status, broker metadata i izvedena readiness dopuštenja.

## Odluke i ograničenja točke 2

- Startup ne može automatski postaviti `LiveEnabled`; potreban je eksplicitan slijed readiness tranzicija i `TradingEnabled=true`.
- Stvarni historical backfill, njegovi checkpointi i automatsko napredovanje statusa dolaze u točki 4. Zato novi omogućeni instrument zasad ostaje u `BackfillPending`.
- Dohvat i provjera broker contracta još nisu automatizirani; registry sada ima sigurno spremište i concurrency zaštitu za taj podatak.
- Statusne promjene namjerno nisu izložene kao javni mutacijski API. Orkestracija onboardinga bit će dodana uz workere koji mogu dokazati kriterij pojedine tranzicije.
- Zadana konfiguracija i dalje koristi globalni `AnalysisOnly`, a SPY nema instrument-level trading dopuštenje.

## Točka 3 — izvedeno

Verifikacija: [GitHub Actions run 35914113645](https://github.com/dejanakadex/aiTradingBot/actions/runs/35914113645) — .NET 10 Release build, 213/213 testova, bez warninga i grešaka.

- Dodan je canonical `market-data-v2` ugovor za `Bid`, `Ask`, `Trade` i `Bar`; svaki događaj nosi `EventId`, stabilni `InstrumentId`, symbol, event/receive vrijeme, source, opcionalni sequence i finalnost.
- IBKR 1m subscription u realnom adapteru uz završene barove traži level-one bid/ask/last podatke i šalje ih kroz isti canonical quality gate.
- Candle persistence sada sprema instrument ID, receive vrijeme, source, finalnost i quality status uz postojeći event timestamp/OHLCV.
- `MarketDataQualityService` provjerava konfigurirani instrument, shape, buduće/stale vrijeme, finalnost, duplikate, sequence/event-time redoslijed i gapove po streamu.
- Stream stanje i svaki incident trajno se spremaju u `MarketDataStreamStateRecords` i `MarketDataQualityIncidentRecords`, uključujući stanje nakon restarta.
- Stale, future, non-final, duplicate, invalid i out-of-order događaji ne ulaze u candle/trading pipeline. Gap bar se smije spremiti za research, ali ne smije pokrenuti pattern ili trade.
- Pattern worker ima dodatnu obranu i odbija svaki candle koji nije finalan i `Healthy`.
- Ispravljena je stara rupa u kojoj se već spremljeni duplikat mogao ponovno objaviti analysis pipelineu.
- Najnoviji zdravi bid/ask/trade podaci daju current price i spread za `MarketSnapshot`, uz postojeći 1m/5m/15m kontekst.
- In-memory 5s i 15s OHLCV/trade-count agregati pripremaju kratki kontekst za budući scalping/replay tok.
- Read-only endpointi `GET /api/market-data/streams`, `GET /api/market-data/incidents` i `GET /api/market-data/latest` izlažu trenutno i povijesno quality stanje.

## Odluke i ograničenja točke 3

- Sirovi quote/trade tickovi namjerno se ne spremaju u operativni SQLite. Particionirani raw/research storage dolazi u točki 5; SQLite čuva stream stanje, incidente i završene barove.
- Latest quote/trade i 5s/15s agregati su operativni in-memory prikaz te se nakon restarta ponovno pune iz live feeda. Persistirani quality checkpoint ostaje sačuvan.
- Standardni CI nema službeni IBKR `CSharpAPI.dll`, pa verificira broker-unavailable build. Level-one callback u realnom adapteru mora se dodatno kompilirati i smoke-testirati u okruženju sa službenim DLL-om.
- Gap provjera sada pokriva sequence i očekivani razmak barova unutar istog UTC datuma. Trading calendar, session segmenti, pacing i automatski gap-fill pripadaju točkama 4 i 6.
- Ova točka ne pokreće veliki historical backfill niti mijenja fail-closed `AnalysisOnly`/instrument trading dopuštenja.

## Točka 4 — izvedeno

Verifikacija: [GitHub Actions run 35967896421](https://github.com/dejanakadex/aiTradingBot/actions/runs/35967896421) — .NET 10 Release build, 216/216 testova, bez warninga i grešaka.

- Svaki konfigurirani instrument/timeframe dobiva trajni `HistoricalBackfillJobRecord`; svaki pokušaj segmenta sprema se zasebno u `HistoricalBackfillSegmentRecord`.
- Checkpoint ide od najnovijih prema starijim podacima, pa se nakon prekida nastavlja na točnoj granici zadnjeg završenog segmenta.
- Zadani dohvat je širok, ali konfigurabilan: 365 dana 1m podataka, 730 dana 5m i 1825 dana 15m podataka. Segmenti su 1, 7 i 14 dana kako bi IBKR vratio razuman broj barova po zahtjevu.
- Broker-wide durable pacing koristi zadani razmak od 11 sekundi. Retry koristi exponential backoff i nakon konfiguriranog broja pokušaja trajno označava job i instrument kao `Faulted`.
- Candle upis koristi SQLite `INSERT OR IGNORE` nad postojećim unique ključem `(Symbol, Timeframe, TimestampUtc)`, pa ponovljen ili prekinut segment ne duplicira podatke i može raditi uz live writer.
- Povijesni barovi prolaze zasebnu OHLCV/finalnost/bounds validaciju i ne mijenjaju event-time checkpoint živog streama niti objavljuju trading evente.
- Nakon svih segmenata iznova se gradi persistirani `HistoricalDataGapRecord` report. Status `CompletedWithGaps` jasno razlikuje potpun rezultat od rezultata s rupama.
- Registry automatski prelazi `BackfillPending -> Backfilling -> Collecting` tek kada su završeni svi konfigurirani timeframeovi. Iscrpljeni retry vodi u `Faulted`; nijedan put ne uključuje trading.
- Live subscription više ne čeka sinkroni startup seed. Historical worker je zaseban hosted service, pa live i backfill mogu napredovati paralelno.
- Read-only endpointi `GET /api/historical-backfill/jobs` i `GET /api/historical-backfill/gaps?instrumentId=...` izlažu checkpoint, broj pokušaja, inserted/duplicate metrike i pronađene intervale.
- Testovi pokrivaju restart između segmenata, nastavak checkpointa, idempotentnu deduplikaciju, gap report, retry koji preživi novu instancu servisa i terminalni onboarding failure.

## Odluke i ograničenja točke 4

- IBKR segmenti i 11-sekundni pacing prate zahtjev da svaki poziv vraća samo nekoliko tisuća barova i da se ne prijeđe povijesni request limit. Sve vrijednosti ostaju konfigurabilne prema stvarnom računu i pretplatama.
- Gap report sada pouzdano mjeri unutarnje rupe između vraćenih barova unutar istog New York datuma. Potpuno prazan raspon označava se jednom eksplicitnom rupom; exchange holiday/early-close kalendar dolazi uz session-aware collection pouzdanost u točki 6.
- Količina koju IBKR stvarno može vratiti ovisi o instrumentu, pretplati i dostupnosti providera. `CompletedWithGaps` ne predstavlja research readiness; točke 5–8 moraju sačuvati, provjeriti i reproducirati dataset/feature obradu.
- Standardni CI i dalje ne kompilira conditional realni IBKR adapter bez službenog DLL-a. Parametri zahtjeva provjereni su prema službenom TWS API step-size/pacing ugovoru, ali potreban je integration smoke test s TWS/IB Gatewayem.
- Ova točka ne mijenja fail-closed `AnalysisOnly`, ne dopušta paper/live naloge i ne započinje Parquet dataset storage.

## Točka 5 — izvedeno

Verifikacija: [GitHub Actions run 35969992224](https://github.com/dejanakadex/aiTradingBot/actions/runs/35969992224) — .NET 10 Release build, 219/219 testova, bez warninga i grešaka.

- Dodan je Parquet raw/research sloj odvojen od operativnog SQLitea. SQLite i dalje čuva checkpoint/status/incidente i canonical candleove, ali ne bid/ask/trade tickove.
- Stabilna particija je `instrument=<InstrumentId>/date=<UTC yyyy-MM-dd>/type=<bid|ask|trade|bar>`; naziv part datoteke proizlazi iz vremenskog raspona i determinističkog hasha sadržaja.
- Svaka part datoteka nastaje u istoj particiji kao privremena datoteka i atomskim renameom postaje vidljiva tek nakon uspješne Parquet serializacije.
- `_manifest.json` sadrži schema version, instrument, datum, vrstu, broj redaka, vremenski raspon i SHA-256 svake datoteke. `_manifest.sha256` zasebno štiti sam manifest.
- Ponovni zapis identičnog batcha prepoznaje isti sadržajni identitet i ne stvara novu datoteku ni novu manifest stavku.
- Read path prvo provjerava hash, filtrira po instrumentu/vremenu/vrsti, uklanja ponovljeni `EventId` i vraća stabilan event-time/receive-time/ID redoslijed.
- Live canonical bid/ask/trade/bar događaji i validirani historical barovi prolaze kroz bounded single-reader writer. Puni kanal primjenjuje backpressure umjesto tihog odbacivanja; graceful shutdown prazni preostali batch.
- Quality rezultat, razlog i dopuštenja za persistence/trading spremaju se uz svaki canonical zapis, uključujući događaje koji su korisni za research, ali ne smiju pokrenuti trade.
- Read-only endpointi `GET /api/datasets/manifest` i `GET /api/datasets/verify` izlažu inventar i integrity status bez mutacije dataseta.
- `DatasetStorage` konfiguracija upravlja rootom, kapacitetom queuea, batch veličinom, intervalom flushanja i schema versionom. Runtime `data/datasets/` je u `.gitignore`.
- Testovi pokrivaju više instrumenata/datuma/vrsta, stvarni Parquet read, stabilni poredak, idempotentni replay batcha, manifest/file hash, detekciju tamperinga, live tick bez SQLite candlea i historical bar dataset integraciju.

## Odluke i ograničenja točke 5

- Dataset je append-only na razini immutable part datoteka. Deduplikacija istog batcha događa se pri pisanju, a preklapanje različitih batchova deterministički se uklanja na read pathu po `EventId`; buduća compaction/retention politika nije dio ove točke.
- Schema version se ne mijenja nad postojećim rootom bez eksplicitne migracije dataseta. Hash potvrđuje integritet bajtova, ali nije digitalni potpis i ne zamjenjuje kontrolu pristupa storageu.
- Historical validacija i dalje odbacuje neispravne broker barove prije dataset writera; broker request/retry/gap audit ostaje u operativnom SQLiteu.
- Standardni CI ne testira stvarni IBKR feed niti dugotrajno opterećenje diska. Capacity, flush interval, disk monitoring, retention i recovery procedure moraju se kalibrirati prije kontinuiranog rada.
- Ova točka ne uključuje deterministic replay engine; reproducibilan read ugovor koji će replay koristiti dolazi sada, a samo izvršavanje je točka 7.

## Točka 6 — izvedeno

Verifikacija: [GitHub Actions run 35979682906](https://github.com/dejanakadex/aiTradingBot/actions/runs/35979682906) — .NET 10 Release build, 224/224 testova, bez warninga i grešaka.

- Uklonjena je pogrešna ovisnost collection workera o `TradingEngineState.Ready`, `TradingEnabled`, reconciliation statusu i `TradingSettings.Enabled`. Omogućeni instrumenti sada skupljaju podatke u `AnalysisOnly`, tijekom trading pauze i neovisno o AI dostupnosti.
- Svaki instrument/timeframe radi u vlastitom dugotrajnom subscription loopu. Exception, završen channel ili stale heartbeat ponovno pokreće samo taj stream; ostali instrumenti i timeframeovi nastavljaju bez prekida.
- Runtime status po streamu sadrži broker/subscription stanje, zadnji heartbeat i event-time, receive lag, izračunati heartbeat timeout, consecutive failures, reconnect counter, zadnji error i gap-fill metrike.
- Heartbeat timeout računa se iz intervala timeframea, konfigurabilnog multiplikatora i grace perioda. Izvan konfigurirane New York regularne sesije stream ostaje pretplaćen u `IdleOutsideSession` stanju i očekivana tišina ne pokreće reconnect.
- Reconnect prije nove live pretplate određuje nedostajući `[last event + interval, aligned now)` raspon i poziva automatski gap-fill. Maksimalni lookback je ograničen konfiguracijom kako kvar ne bi proizveo nekontrolirano velik zahtjev.
- Automatski gap-fill prolazi kroz isti singleton historical servis i njegov broker-wide semaphore/pacing, OHLCV validaciju, `INSERT OR IGNORE` deduplikaciju i Parquet dataset sink. Gap-fill barovi ne objavljuju trading evente.
- 1m resubscription u stvarnom IBKR adapteru ponovno stvara i vezanu level-one bid/ask/trade pretplatu. Njihov canonical quality status ostaje vidljiv kroz postojeći stream/incidents endpoint.
- Read-only `GET /api/market-data/collection` izlaže sve collection statuse; postojeći `streams`, `incidents`, `latest`, historical jobs/gaps i dataset integrity endpointi ostaju odvojeni pogledi.
- `MarketDataCollection` konfiguracija upravlja uključivanjem collectiona, monitor intervalom, heartbeatom, reconnect backoffom, session-aware ponašanjem, automatskim gap-fillom i najvećim lookbackom.
- Testovi pokrivaju collection bez trading readinessa, više instrumenata, izolaciju neuspjelog streama, disconnect čekanje, graceful dispose, stvarni bar/pattern tok, završeni channel reconnect, stale heartbeat i idempotentni automatski gap-fill.

## Odluke i ograničenja točke 6

- Collection status je namjerno runtime/in-memory prikaz; canonical quality checkpointi, historical jobovi/gapovi i sami podaci ostaju trajni u SQLiteu/Parquetu. Nakon restarta status se ponovno izgradi iz aktivnih streamova.
- Session guard trenutno poznaje radne dane i konfigurirane New York sate, ali ne službeni exchange holiday/early-close kalendar. Zato se izvan uobičajenih sati ne stvara lažni alarm, dok će puna calendar-aware provjera trebati zaseban market-calendar izvor.
- Reconnect i gap-fill garantiraju izolaciju u procesu, ali ne mogu nadoknaditi podatke koje broker/provider više ne nudi. Takav rezultat ostaje mjerljiv kroz historical gap report i quality incidente.
- Standardni CI provjerava fake broker put. Stvarni TWS/Gateway reconnect, IBKR pacing kod više paralelnih streamova i level-one recovery trebaju integration/soak test sa službenim `CSharpAPI.dll` i paper računom.
- Ova točka ne implementira replay. Stabilni collection/dataset input sada je spreman za deterministic event-time replay u točki 7.

## Točka 7 — izvedeno

Verifikacija: [GitHub Actions run 36035034631](https://github.com/dejanakadex/aiTradingBot/actions/runs/36035034631) — .NET 10 Release build, 230/230 testova, bez warninga i grešaka.

- `DeterministicReplayService` prije kreiranja runa provjerava Parquet manifest i file SHA-256 vrijednosti, čita samo traženi instrument/vremenski raspon i sprema hash svih polja točno odabranog ulaza uz market-data/feature/pattern/strategy verzije.
- Događaji se dodatno dedupliciraju po `EventId` te obrađuju stabilnim redoslijedom `EventTimeUtc`, `ReceivedTimeUtc`, `EventId`, neovisno o redoslijedu kojim ih dataset store vrati.
- Replay propušta samo finalne `Healthy` barove s `CanTriggerTrading=true`; svaki timeframe dobiva vlastiti as-of prozor. Feature i pattern evaluacija vide samo trenutnu i ranije svijeće, nikad buduće podatke.
- Live i replay instanciraju isti `PatternDetector` preko zajedničkog `IPatternDetectorFactory`, a replay koristi isti `IFeatureEngine`. Pattern verzija uključuje SHA-256 fingerprint svih detector opcija i nastavak je fail-closed ako se konfiguracija promijeni. Replay nema ovisnost o `ITradingEventBus`, order manageru ili broker adapteru i ne može poslati nalog.
- `ReplayRunRecords` trajno sprema status, brzinu, checkpoint, broj obrađenih događaja/signala, input/output hash i grešku. `ReplaySignalRecords` odvojeno sprema deterministički `SignalId`, source event, pattern, feature JSON i canonical sortirani metadata JSON.
- Checkpoint se sprema nakon svakog događaja. Nova instanca servisa pri nastavku ponovno izgradi samo dotadašnju candle povijest i stateful pattern deduplikaciju, a unique `(ReplayRunId, SignalId)` indeks sprječava dvostruki signal.
- Brzina `0` znači obradu bez namjerne pauze. Pozitivni multiplier reproducira razmak između event-timeova podijeljen multiplierom, uz konfigurabilan najveći pojedinačni delay.
- Hosted worker obrađuje `Pending`/`Running` runove u konfigurabilnim batchovima. Dostupni su start/list/detail/signals/pause/resume/cancel endpointi pod `/api/replays`.
- Ako se ulazni raspon promijeni nakon kreiranja runa, nastavak završava `Faulted` umjesto da proizvede nereproducibilan rezultat. Isti ulaz i verzije daju isti poredak signal ID-jeva i isti output SHA-256.
- Testovi pokrivaju jednak rezultat dvaju runova, as-of/no-future-data prozor, trajni checkpoint nakon nove instance servisa, pause/resume bez duplikata, fail-closed promjenu dataseta ili pattern konfiguracije, neispravan integrity status, ograničenje brzine i stvarnu EF migraciju.

## Odluke i ograničenja točke 7

- Ova točka replaya završene canonical barove jer postojeći feature/pattern engine radi nad candleovima. Bid/ask/trade tick replay, spread/microstructure featurei i kratki 5s/15s canonical featurei pripadaju sljedećim točkama.
- Replay namjerno stvara samo izolirane research signale. Ne piše live `PatternDetections`, ne poziva AI/strategy/risk/order tok i ne mijenja onboarding ili trading readiness.
- Worker je zasad procesno serijaliziran kako dva runa ne bi opteretila SQLite i dataset nekontroliranom paralelnošću. Kontrolirani multi-run concurrency može se dodati nakon mjerenja I/O i CPU troška.
- Mutacijski replay endpointi ne mogu trgovati, ali mogu trošiti CPU, disk i memoriju. Prije javnog izlaganja aplikacije treba ih zaštititi autentikacijom/autorizacijom i rate limitom.
- `MaximumDelayMilliseconds` namjerno ograničava dugo čekanje između rijetkih događaja. Za stvarni 1x wall-clock replay vrijednost mora biti postavljena dovoljno visoko; za research je preporučena brzina `0`.

## Sljedeći checkpoint — točka 8

Implementirati canonical feature ugovor za VWAP, ATR, RSI, EMA, relativni volumen, spread, momentum, mean reversion, režim i normalizaciju likvidnosti/volatilnosti tako da live, backfill i replay daju jednake vrijednosti za isti `asOf`.

## Točka 8 — izvedeno

Verifikacija: [GitHub Actions run 36039004900](https://github.com/dejanakadex/aiTradingBot/actions/runs/36039004900) — .NET 10 Release build i 237/237 testova.

- `CanonicalFeatureInput` uvodi eksplicitni `AsOfUtc` te event-timeove bid/ask/trade/spread podataka. Engine uklanja buduće candleove i quoteove, odbacuje prestare quoteove, deterministički deduplicira timestampove te validira jedan instrument/symbol/timeframe po izračunu.
- `MarketFeatures` sada uz EMA, RSI, ATR, VWAP i relativni volumen nosi instrument, timeframe, sample count, momentum, mean-reversion z-score, EMA separaciju, spread/bps/spread-to-ATR, quote age, dollar-volume likvidnost, ATR/price i realiziranu volatilnost te deterministički market regime s razlogom.
- `CanonicalFeatureSettings` centralizira periode i pragove. `FeatureEngine.FeatureVersion` kombinira `features-v2` sa SHA-256 fingerprintom cijele konfiguracije, a replay fail-closed odbija nastavak runa ako se feature konfiguracija promijeni.
- Live pattern worker gradi strogo as-of povijest, koristi latest canonical quote/trade stanje i u audit detalje sprema cijeli canonical feature objekt i njegovu stvarnu konfiguracijsku verziju. Market snapshot za sva tri timeframea koristi isti ugovor.
- Replay sada čita i event-time redom primjenjuje `bid`, `ask` i `trade` zapise uz barove. Svaki bar vidi samo quote stanje dostupno do njegova `asOf`; izolirani signal sprema puni canonical feature JSON.
- Backfill ostaje raw/canonical market-data put, bez zasebne feature implementacije. Test kroz stvarni Parquet write/read dokazuje da backfill round-trip i live ulaz daju isti feature JSON, a dodatni testovi pokrivaju obrnuti redoslijed, buduće podatke, replay quote stanje i konfiguracijski fingerprint.

## Odluke i ograničenja točke 8

- VWAP se računa nad cijelim candle prozorom koji pozivatelj preda; još nije session-reset VWAP. To treba zadržati kao eksplicitnu definiciju ili u kasnijoj verziji ugovora odvojiti rolling i session VWAP.
- Režim je deterministička klasifikacija iz ATR/price, EMA separation/ATR, momentuma i dostupnosti mean-reversion prozora. Pragovi su konfigurabilni, ali njihova statistička kalibracija pripada točkama 11–12.
- `NormalizedLiquidity` je omjer zadnjeg dollar volumea i prosjeka prethodnog prozora. Ne tvrdi da je to broker depth niti procjena stvarnog fill kapaciteta; execution-grade likvidnost i edge-after-cost provjera dolaze u točki 16.
- Spread je dostupan samo kad oba quotea zadovoljavaju isti as-of/freshness ugovor ili kad pozivatelj preda eksplicitni spread s vremenom. Nepotpuni, budući ili prestari quote ostavlja spread feature praznim umjesto da koristi buduću vrijednost.
- Promjena feature formule ili značenja zahtijeva novu baznu verziju ugovora; promjena samo konfiguracije automatski mijenja fingerprint. Postojeći replay run tada se namjerno ne može nastaviti pod novim izračunom.

## Sljedeći checkpoint — točka 9

Implementirati pattern engine s punim pattern + instrument + strategija + timeframe identitetom, eksplicitnim hard uvjetima, score komponentama i razlozima te long/short domenom. Završni kriterij su pozitivni i negativni testovi za svaki pattern bez deduplikacijskih konflikata između instrumenata i timeframeova.

## Točka 9 — izvedeno

Verifikacija: [GitHub Actions run 36109324767](https://github.com/dejanakadex/aiTradingBot/actions/runs/36109324767) — .NET 10 Release build, 248/248 testova, bez warninga i grešaka.

- Ugovor je podignut na `patterns-v2`; konfiguracija detektora daje vlastiti SHA-256 fingerprint, a live i replay kandidat nose stvarnu feature i pattern verziju.
- `PatternCandidate` i persistirani `PatternDetection` sada nose smjer, instrument, strategiju, timeframe, deterministički signal/pattern ključ, hard uvjete, ponderirane score komponente i razloge evaluacije.
- Long pravila za Hammer, Bullish Engulfing, Double Bottom, Breakout and Retest i VWAP Reclaim imaju zrcalna short pravila: Shooting Star, Bearish Engulfing, Double Top, Breakdown and Retest i VWAP Reject.
- Live worker evaluira sve konfigurirane `StrategyIds` i dopuštene smjerove za instrument. Deduplikacija uključuje instrument, strategiju, smjer, timeframe i pattern, pa jednaki setupi na različitim tokovima ne blokiraju jedan drugoga.
- Replay koristi isti engine za oba smjera, sprema smjer signala i uključuje ga u deterministički output hash. Migracija nadograđuje postojeće pattern/replay zapise bez gubitka legacy povijesti.
- Short kandidati zasad ostaju research-only: quality gate ih eksplicitno odbija prije AI/strategy/order puta dok točke 13–16 ne uvedu short risk, borrow, arbitration i execution podršku.
- Testovi pokrivaju pozitivne i negativne slučajeve svih deset pravila, izolaciju deduplikacije, više paralelnih strategija u live workeru, short fail-closed gate te EF migraciju i unique pattern ključ.

## Sljedeći checkpoint — točka 10

Spremati prihvaćene, odbijene i blokirane kandidate te iz isključivo naknadnih podataka izračunati 5s/15s/30s/1m/3m/5m MFE/MAE i target/stop-first labele s realističnim troškom.

## Točka 10 — izvedeno

Verifikacija: [GitHub Actions run 36112293431](https://github.com/dejanakadex/aiTradingBot/actions/runs/36112293431) — .NET 10 Release build, 255/255 testova, bez warninga i grešaka.

- `PatternDetector.Process` jednom evaluira cijelu long/short domenu te vraća svih deset evaluacija i stvarno emitirane kandidate. Time se u research skup spremaju negativni primjeri, a ne samo signali koji su prošli hard uvjete.
- `ResearchCandidateRecords` trajno čuva stabilni candidate/record ključ, live ili replay scope, source event, instrument, strategiju, smjer, timeframe, reference cijenu, score, verzije, uvjete, komponente, razloge i metadata. Unique record ključ čini ponovljeni batch idempotentnim.
- Početni ishodi razlikuju `Rejected` hard uvjete, `Blocked` deduplikaciju i `Accepted` emitirani pattern. Live decision tok naknadno označava kandidata blokiranim na trading-hours, freshness, engine, broker, quality, AI, strategy, risk ili error gateu bez gubitka izvorne evaluacije.
- Svaki kandidat odmah dobiva pending labele za 5, 15, 30, 60, 180 i 300 sekundi. `labels-v1` uz SHA-256 konfiguracijski fingerprint sprema korištenu definiciju horizonata, targeta, stopa i troška.
- Kalkulator koristi samo market događaje s `eventTime > candidateTime` i `eventTime <= horizonEnd`. Live worker čeka konfigurabilni dataset grace period; replay nema writer grace i labelira samo horizonte potpuno sadržane u reproduciranom rasponu.
- MFE, MAE i gross return zrcalno se računaju za long i short. Neto rezultat oduzima opaženi spread kada postoje bid/ask događaji, inače konfigurirani fallback spread, te round-trip proviziju i slippage.
- Target/stop-first koristi stabilni event-time/ID redoslijed. Ako ista agregirana opservacija dotakne target i stop, labela je `Ambiguous` umjesto da izmisli intrabar redoslijed; prazan završeni horizont je eksplicitno `InsufficientData`.
- Live i deterministic replay koriste isti persistence/label servis. Replay output hash sada obuhvaća research kandidate i labele bez database-generated ID-jeva, pa isti input i verzije ostaju reproduktivni.
- Migracija dodaje `ResearchCandidateRecords` i `CandidateLabelRecords` s cascade vezom te indeksima za idempotency, scope, instrument/outcome i pending maturity. Read-only endpointi su `GET /api/research/candidates` i `GET /api/research/candidates/{id}/labels`.
- Testovi pokrivaju strogo as-of prozor, točnu horizon granicu, long/short MFE/MAE, opaženi/fallback trošak, target-first, stop-first, intrabar ambiguity, pending horizont, svih deset evaluacija, idempotentnu persistenciju, blokiranje i stvarnu EF migraciju.

## Odluke i ograničenja točke 10

- Entry/reference cijena je close svijeće na kojoj je pattern evaluiran, a trošak je istraživačka procjena. To nije tvrdnja o stvarnom fillu; execution-grade quote, latency i fill model pripada točki 16.
- Kratke labele imaju punu vrijednost samo kada dataset sadrži dovoljno granularne trade/quote događaje. Sam 1m OHLC bar može dati MFE/MAE za dulji prozor, ali target/stop dodir unutar istog bara ostaje namjerno neodređen.
- Opaženi spread je prosjek dostupnih uparenih quote stanja unutar horizonta. Ne modelira queue position, market impact, partial fill ni borrow trošak; te komponente zahtijevaju kalibraciju stvarnim paper/execution podacima.
- Završena `InsufficientData` labela ne nagađa cijenu niti se automatski popunjava budućim događajem. Operativno treba pratiti dataset lag i gapove kako bi se razlikovao stvarni nedostatak podataka od kašnjenja writera.
- Endpointi su read-only, ali prije javnog izlaganja i dalje trebaju autentikaciju/autorizaciju. Točka ne mijenja `AnalysisOnly`, instrument trading dozvole ni short execution zabranu.

## Sljedeći checkpoint — točka 11

Izgraditi evaluaciju po instrumentu, strategiji, vremenu, režimu i likvidnosti uz walk-forward podjele, expectancy, profit factor, drawdown i osjetljivost na trošak. Završni holdout period mora ostati netaknut, a prag se ne smije birati samo prema win rateu.

## Točka 11 — izvedeno

Verifikacija: [GitHub Actions run 36184406649](https://github.com/dejanakadex/aiTradingBot/actions/runs/36184406649) — .NET 10 Release build, 262/262 testova, bez warninga i grešaka.

- `evaluation-v1` uvodi konfiguracijski fingerprint za training/validation/step/holdout prozore, minimalne uzorke, confidence pragove, cost multipliere i granice likvidnosti. Promjena bilo koje pretpostavke proizvodi novu evaluation verziju.
- Evaluacija radi odvojeno nad live kandidatima ili jednim eksplicitnim replay runom; opcionalni instrument i strategija dodatno sužavaju scope. Jedan run prihvaća točno jedan horizon i fail-closed odbija miješane feature, pattern ili label verzije.
- Završni period rezervira se prije izbora praga. Kandidat čija buduća labela prelazi training/validation/holdout granicu izbacuje se iz ranijeg skupa, čime se uklanja leakage preko MFE/MAE horizonta.
- Svaki walk-forward fold bira confidence prag samo na svom ranijem rolling training prozoru i primjenjuje ga na kasniji, nepreklapajući validation prozor. Završni prag bira se iz cijelog pre-holdout dijela, a holdout se mjeri tek nakon tog izbora.
- Odabir praga namjerno ne optimizira win rate. Kandidati moraju zadovoljiti minimalni uzorak; pozitivna expectancy ima prednost, zatim se rangira po expectancyju, profit factoru, manjem drawdownu i veličini uzorka.
- `EvaluationMetrics` sprema broj dobitaka/gubitaka/breakevena, win rate, expectancy, net profit, gross profit/loss, profit factor, kronološki maximum drawdown, prosječni MFE/MAE te target-first, stop-first i ambiguous udjele.
- Cost sensitivity ponovno računa holdout net rezultat za konfigurirane multipliere stvarnog procijenjenog troška. Ne mijenja labelu ni odabrani prag i jasno pokazuje preživljava li edge skuplje izvršenje.
- Out-of-sample segmenti obuhvaćaju instrument, strategiju, pattern, smjer, timeframe, UTC dan, New York dio sesije, market regime i normalized-liquidity bucket. Live i replay kandidat sada trajno spremaju režim, likvidnost i normaliziranu volatilnost iz točno njegovog as-of feature konteksta.
- `ResearchEvaluationRunRecords` čuva request scope, granice, verzije, input/output SHA-256, odabrani prag, foldove, threshold tablicu, tri glavna metric seta, cost sensitivity i segmente. Jednaki input i konfiguracija vraćaju isti immutable/idempotentni run.
- API podržava `POST /api/research/evaluations`, listu i detalj runa. Testovi dokazuju da promjena završnog holdout rezultata ne može promijeniti prag, da viši win rate ne pobjeđuje bolji expectancy, da su rejected primjeri neeligible, da miješane verzije padaju, te provjeravaju metrike, segmente, trošak, idempotency i stvarnu migraciju.

## Odluke i ograničenja točke 11

- `Rejected` hard-condition evaluacije ostaju u research skupu i input auditu, ali ne mogu postati tradeable izbor confidence praga. Prag se bira samo među `Accepted` i kasnije `Blocked` kandidatima koji su prošli pattern hard uvjete.
- Zadane postavke traže 60 dana traininga, 14 dana validationa, korak od 14 dana i netaknuti završni holdout od 30 dana, uz minimalno 100/30/50 uzoraka. Run namjerno odbija premalo podataka umjesto da vrati statistički privlačan, ali nepouzdan rezultat.
- Profit factor je nedefiniran kada nema gubitaka i tada ostaje `null`; takav threshold se pri internom rangiranju tretira kao bolji od konačnog omjera samo ako stvarno ima pozitivan gross profit.
- Segmenti se računaju nad stvarno odabranim out-of-sample validation i holdout kandidatima. Vrlo mali segment može biti informativan, ali se ne smije koristiti kao novi prag bez zasebne minimalne veličine i točke 12.
- Holdout rezultat je vidljiv nakon završenog runa radi konačne procjene, ali ne ulazi u izbor praga. Ponavljano ručno podešavanje konfiguracije prema tom rezultatu pretvorilo bi ga u validation skup i mora se organizacijski zabraniti.
- Ova točka ne mijenja runtime trading pragove niti automatski promovira strategiju. Kalibrirane vjerojatnosti, stabilni pragovi i auditirana ručna potvrda pripadaju točki 12; bot ostaje `AnalysisOnly`.
- Mutacijski evaluation endpoint može trošiti CPU i čitati velik broj zapisa. Prije javnog izlaganja treba autentikaciju, autorizaciju i rate limiting.

## Sljedeći checkpoint — točka 12

Implementirati kalibrirane vjerojatnosti, stabilne pragove i rangiranje konkurentnih prilika uz verzioniranu ručnu potvrdu. Svaka promjena praga ili modela mora biti auditirana i uspoređena s determinističkim baselineom bez ponovnog optimiziranja na završnom holdoutu.

## Točka 12 — izvedeno

Verifikacija: [GitHub Actions run 36484379002](https://github.com/dejanakadex/aiTradingBot/actions/runs/36484379002) — .NET 10 Release build, 269/269 testova, bez warninga i grešaka.

- `calibration-v1` uključuje SHA-256 fingerprint minimalnog uzorka i binova, pravila stabilnosti, dopuštenih baseline degradacija, obveznog razloga odobrenja i limita batch rangiranja. Jednaki evaluation input i konfiguracija daju isti idempotentni profil.
- Izotonička PAV kalibracija pretvara raw pattern confidence u monotono neopadajuću empirijsku vjerojatnost dobitnog neto ishoda. Početni binovi poštuju minimalnu veličinu, a profil sprema granice, uzorke, opaženi win rate i prosječni neto povrat svakog završnog bloka.
- Svaki walk-forward fold fitira vlastitu kalibraciju isključivo na svom training prozoru i mjeri je na kasnijem validation prozoru. Spremaju se raw i calibrated Brier score, log loss i expected calibration error; završna mapa fitira se samo na cijelom pre-holdout skupu.
- Stabilni confidence prag je medijan training pragova završenih foldova. Minimalan broj foldova, najveći dopušteni raspon i udio foldova unutar tolerancije čine fail-closed stability gate; holdout ne sudjeluje u tom izboru.
- Završni holdout koristi se tek nakon zaključavanja mape i praga. Predloženi prag uspoređuje se s determinističkim baselineom točke 11 po expectancyju, maximum drawdownu i zadržanom uzorku, a calibrated probability dodatno se uspoređuje s raw confidenceom.
- Profil trajno sprema evaluation/pipeline verzije, input/output hash, kalibracijsku mapu, metrike i baseline usporedbu. Ako se underlying evaluation input promijeni nakon runa, kalibracija ga odbija i zahtijeva novi evaluation umjesto tihog ponovnog fitanja.
- Ručna odluka zahtijeva reviewer, obrazloženje i točan output SHA-256 pregledanog profila. Approval/rejection se sprema u append-only povijest revizija; odobrenje nove verzije istog instrument/strategija/horizon scopea auditirano označava prethodno odobrenu verziju kao `Superseded`, a parcijalni unique indeks sprječava dva istodobno aktivna profila.
- Rangiranje radi samo s ručno odobrenim profilom, provjerava njegov hash, točne feature/pattern/label verzije i scope. Prilike rangira po eligibilityju, očekivanom neto povratu nakon dodatnog troška, kalibriranoj vjerojatnosti i stabilnom tie-breaku; ne šalje naloge niti mijenja trading konfiguraciju.
- API dodaje listu/detalj/generiranje pod `/api/research/calibrations`, eksplicitni `/decision` i deterministički `/rank`. Testovi pokrivaju monotoni fit, metrike, netaknuti holdout, nestabilan prag, idempotency, reviewed hash, manual approval, supersede audit, fail-closed verzije, rangiranje i stvarnu EF migraciju.

## Odluke i ograničenja točke 12

- Kalibracijski ishod je binarna vjerojatnost `NetReturnBps > 0` za jedan label horizon. Ne predstavlja vjerojatnost target-first ishoda niti veličinu povrata; očekivani neto povrat rankera zato zasebno koristi pre-holdout prosječni dobitak i gubitak.
- Holdout usporedba smije biti završna accept/reject provjera unaprijed definiranih pravila. Mijenjanje kalibracijskih postavki nakon gledanja rezultata istog holdouta i ponovno odobravanje bilo bi leakage; takva promjena zahtijeva novi vremenski holdout/evaluation ciklus.
- `Reviewer` je zasad auditno polje koje dostavlja API pozivatelj, ne potvrđen identitet. Prije javnog ili višekorisničkog rada mutacijski endpointi moraju dobiti autentikaciju, autorizaciju, stvarni user identity i rate limiting.
- Odobreni profil nije spojen na live strategy/risk/order pipeline. Portfolio rezervacije, signal arbitration i lifecycle izvedeni su u točkama 13–15; execution-grade edge provjera ostaje u točki 16.
- Scope bez instrumenta/strategije može rangirati više instrumenata samo ako svi koriste iste zaključane pipeline verzije. Za različite podatkovne režime treba izraditi i zasebno odobriti uže profile umjesto ručnog prepisivanja rezultata.

## Točka 13 — izvedeno

Verifikacija: GitHub Actions .NET 10 Release build i 276/276 testova bez warninga i grešaka.

- `PositionSizer` više ne uklanja nulti leverage kapacitet prije izračuna minimuma. Točna granica i prekoračenje sada vraćaju nultu količinu i odbijanje.
- Produkcijski pattern/risk tok bira `PaperAccountId` u paper modu (i kada je dostupan u AnalysisOnly), zahtijeva da broker vrati isti account ID te učitava stvarno zatvorene tradeove od početka aktualne New York sesije. Time dnevni gubitak, broj tradeova, uzastopni gubici i loss cooldown rade nad stvarnim podacima.
- `PortfolioRiskService` serijalizira provjeru i upis u istoj `Serializable` SQLite transakciji. Rezervacija ima stabilan signal/account ključ, trajni status `Pending`, `Committed`, `Released` ili `Expired`, concurrency verziju i append-only audit svake tranzicije.
- Svaka odluka uključuje postojeće broker pozicije i sve aktivne rezervacije u globalni gross/net exposure, buying power i leverage. Dodatno se primjenjuju per-instrument, per-strategy, globalni broj otvorenih pozicija/rezervacija, instrument cooldown i konfigurabilne korelacijske grupe.
- Ako je dopušten samo dio predložene vrijednosti, količina i risk izračunavaju se iz preostalog najmanjeg kapaciteta. Bilo koji obvezni kapacitet manji ili jednak nuli odbija nalog.
- `RiskDecision` nosi `ReservationId`. Puni execution kanal otpušta rezervaciju; AnalysisOnly je otpušta nakon hipotetskog zapisa; odbijen nalog je otpušta; broker-prihvaćen ili nejasan nalog prelazi u `Committed` fail-closed stanje s konfigurabilnim timeoutom.
- Bounded approved-plan kanal sada javlja je li objava uspjela, pa se rezervacija ne ostavlja zauzetom kada je red pun. Read-only `GET /api/risk/portfolio?accountId=...` prikazuje aktivne rezervacije i rezervirani gross/net exposure.
- EF migracija dodaje trajne reservation i audit tablice s unique signal ključem i indeksima po accountu, statusu, isteku, instrumentu i strategiji.
- Testovi pokrivaju paralelnu borbu za isti slot, durable commit/release audit, korelacijsko smanjenje količine, session trade history, izbor paper računa te nulti i prekoračeni leverage.

## Odluke i ograničenja točke 13

- Trenutni izvršni put je i dalje long-only; short patterni ostaju research-only dok borrow/direction-aware izvršenje ne bude eksplicitno uvedeno.
- Ovo ograničenje točke 13 riješeno je u točki 15: `OrderStatusDto` i trajni order zapis nose strukturirani intent/fill, a reconciliation uključuje nepoznate i cancel-pending naloge.
- Per-strategy izloženost zasad se može točno atribuirati aktivnim rezervacijama. Atribucija već fillanih neto broker pozicija pojedinoj strategiji pripada virtualnoj atribuciji u točki 14.
- Korelacijske grupe su eksplicitna konfiguracija simbola/instrument ID-jeva, ne procjena korelacije iz podataka. Statistička matrica može se dodati kasnije tek uz definirani lookback, minimalni uzorak i režim.
- Zadani način rada ostaje `AnalysisOnly`; ova točka sama ne daje nijednom instrumentu paper/live readiness.

## Točka 14 — izvedeno

Verifikacija: [GitHub Actions run 36542382787](https://github.com/dejanakadex/aiTradingBot/actions/runs/36542382787) — .NET 10 Release build i 285/285 testova.

- `SignalArbitrationService` donosi trajnu odluku prije portfolio rezervacije. Jedinstveni account/signal ključ i serijalizirana transakcija sprječavaju da paralelni ili ponovljeni signal proizvede više aktivnih intentova.
- Zadana politika konflikta je `Reject`. Opcionalni `Priority` smije zamijeniti samo suprotni intent u stanju `Accepted` ili `Reserved`; signal koji se već šalje brokeru ili ima broker exposure nikad se ne netira niti preuzima.
- Ista strategija ne može imati dupliciranu aktivnu alokaciju za isti instrument i smjer. Različite strategije mogu skalirati isti smjer do konfiguriranog maksimuma, dok postojeća broker neto pozicija blokira suprotni smjer.
- Arbitration intent veže se uz točnu portfolio rezervaciju i odobrenu količinu. Execution worker ga atomski preuzima prijelazom `Reserved` → `Submitting` prije brokerskog poziva; istekli, superseded ili odbijeni intent otpušta rezervaciju.
- Trajni virtual allocation ledger povezuje signal, strategiju, ulazni nalog, child/exit naloge te kumulativne entry/exit fillove. Izlaz se odobrava samo unutar otvorene količine vlastitog signala, pa strategija ne može prodati količinu pripisanu drugoj strategiji.
- `ExitManagementService` sprema `SignalId`, `InstrumentId` i `StrategyId`, ažurira entry fillove te provjerava i registrira zaštitne i vremenske izlaze kroz arbitration servis.
- Per-strategy portfolio exposure sada zadržava fillanu virtualnu alokaciju i nakon isteka committed rezervacije, bez dvostrukog brojanja dok je rezervacija još aktivna.
- EF migracija dodaje arbitration intent, audit i allocation-order tablice te atribuciju exit zapisa. Migracija i startup repair pokrivaju poznati legacy schema-drift slučaj u kojem je stara exit migracija evidentirana, ali tablica nedostaje.
- Read-only `GET /api/signals/allocations?accountId=...&instrumentId=...` prikazuje odobrenu, fillanu, izašlu i otvorenu količinu po signalu/strategiji.
- Sigurne zadane vrijednosti žive u `SignalArbitrationSettings`; primjer bez brokerskih podataka nalazi se u `TradingBot.Web/signal-arbitration.example.json`, a produkcijske vrijednosti mogu se zadati standardnom konfiguracijom ili environment varijablama.

## Odluke i ograničenja točke 14

- Stvarno short izvršavanje i dalje nije omogućeno; short signal ostaje research-only dok se ne uvedu direction-aware nalozi, borrow provjera i odgovarajući zaštitni izlazi.
- Broker neto pozicija ostaje konačni izvor istine. Točka 15 dodala je restart reconciliation virtualnih alokacija, parcijalnih fillova, provizija i nepoznatih broker stanja.
- Bracket stop i target mogu oba referencirati istu virtualnu količinu jer su OCO alternative. Točka 15 dodala je OCA koordinaciju, zaštitu od dvostrukog filla te potvrđeni cancel/modify lifecycle.
- Zadani način rada ostaje `AnalysisOnly`; signal arbitration ne dodjeljuje paper/live readiness nijednom instrumentu.

## Točka 15 — izvedeno

Verifikacija: [GitHub Actions run 36687496236](https://github.com/dejanakadex/aiTradingBot/actions/runs/36687496236) — .NET 10 Release build i 290/290 testova.

- `OrderManager` trajno sprema business intent prije brokerskog submit poziva. Stabilni `ClientOrderKey`, jedinstveni `IntentId`/broker ID indeksi, singleton serijalizacija i callback-first merge sprječavaju drugi entry za istu namjeru tijekom ponavljanja ili restarta.
- Order zapis sada strukturirano čuva ulogu, parent ID, stranu, tip, traženu/fillanu/preostalu količinu, cijene, proviziju te vrijeme zahtjeva i potvrde otkaza. Brokerovo nepoznato stanje ostaje eksplicitno i ne pretvara se optimistično u uspjeh.
- Pojedinačni execution ima nepromjenjivi broker execution ID, vlastitu količinu i cijenu. Kumulativni order status obrađuje se odvojeno, dupli fill se ignorira, a naknadni commission callback dopunjava isti execution i ukupnu proviziju naloga.
- Entry registracija oporavlja fill koji je stigao tijekom submit poziva. Fixed bracket vraća i trajno povezuje stop/target child ID-jeve; child nalozi koriste OCA grupu u stvarnom IBKR adapteru.
- Stop, target, maximum-holding i operator-close prolaze isti exit lifecycle. Managed izlaz se šalje tek nakon broker potvrde otkaza zaštitnog stopa, a BE/trailing stanje mijenja se tek nakon potvrđene modifikacije.
- Svaki izlaz koristi preostalu stvarno fillanu količinu. Per-order kumulativni fillovi pretvaraju se u delte, konkurentni izlazi se serijaliziraju po poziciji, sibling nalozi se otkazuju i ukupni izlaz ne može prijeći entry fill.
- Pause, kill i dopuštenje trgovanja imaju trajno stanje odvojeno od broker readinessa. Reconnect/reconciliation ih ne može poništiti; Close šalje koordinirani izlaz, a Kill traži otkazivanje otvorenih naloga.
- Startup reconciliation uključuje `CancelPending`, nerazriješene intente bez broker ID-a i exit zapise bez potpune zaštite. Svako takvo stanje drži engine degradiranim i blokira nove ulaze dok se ne razriješi.
- Protective-stop monitor više ne ovisi samo o promjenjivom JSON-u: provjerava strukturirani role/side/type/quantity/stop zapis, uz legacy fallback. Dashboard prikazuje ulogu naloga, stvarni fill i zbroj strukturiranih provizija.
- EF migracija dodaje lifecycle i control-state stupce/tablicu, čuva značenje ranije spremljenih enum vrijednosti i sigurno sanira poznate stare SQLite sheme prije migracije.
- Testovi pokrivaju rani fill, ponovljeni intent, potvrđeni cancel, kasnu proviziju, partial fillove, idempotentni time-exit, konkurentne izlaze, očuvanje pauze nakon reconnecta, unresolved intent reconciliation i migraciju starih shema.

## Odluke i ograničenja točke 15

- Zadani način rada ostaje `AnalysisOnly`; završetak lifecyclea sam ne daje nijednom instrumentu paper/live readiness.
- Stvarno short izvršavanje još nije omogućeno. Direction-aware nalozi, borrow provjera i zaštitni short izlazi ostaju zaseban sigurnosni zahtjev prije uključivanja short tradeova.
- CI kompajlira fallback bez službenog IBKR `CSharpAPI.dll`-a. Produkcijski adapter je ažuriran, ali zaseban build sa službenim DLL-om i nadzirani TWS paper callback scenariji i dalje su obvezni prije live rada.
- R11, automatski idempotentni post-trade zapis cijelog zatvorenog ciklusa, nije dio ove točke i ostaje otvoren.

## Točka 16 — izvedeno

Verifikacija: [GitHub Actions run 36692989748](https://github.com/dejanakadex/aiTradingBot/actions/runs/36692989748) — .NET 10 Release build i 295/295 testova, bez warninga i grešaka.

- `ScalpingExecutionGate` radi nakon atomskog execution claima i neposredno prije brokerskog submitanja. Svaki plan ponovno provjerava dob risk odluke i odobrenja, potpuni bid/ask, starost i vremenski skew kotacije, spread te odobreni entry raspon.
- Gate računa usporedive procjene za passive limit, marketable limit i market: konzervativni bruto edge je minimum AI očekivanog pomaka i target edgea, a neto edge oduzima spread, round-trip proviziju, round-trip slippage i sigurnosni buffer.
- Odabrana politika određuje stvarni tip i cijenu entry naloga. `OrderManager` i bracket put sada prihvaćaju market ili limit entry, dok su zaštitni stop/target i trajni lifecycle ostali koordinirani kao u točki 15.
- U paper/live načinu neposredno se ponovno čitaju broker račun, pozicije, otvoreni nalozi i portfolio rezervacija. Promjena računa, novi BUY za isti simbol, nestala rezervacija ili prekoračenje exposure/buying-power/leverage limita odbija nalog; kvar refresha je fail-closed.
- Svaka odluka, uključujući odbijanje, sprema quote vrijeme, cijene, količinu, odabranu politiku, sve tri cost procjene, neto edge, očekivani holding u sekundama, razloge i trajanje evaluacije.
- Maximum holding koristi kraću vrijednost između AI horizonta i per-instrument limita u sekundama. Exit zapis taj limit nosi trajno, bez zaokruživanja kratkih tradeova na minute.
- Sigurne zadane vrijednosti postoje u kodu, a primjer bez brokerskih podataka nalazi se u `TradingBot.Web/scalping-execution.example.json`. Produkcijske pretpostavke troška moraju se kalibrirati iz stvarnih paper fillova.

## Odluke i ograničenja točke 16

- Zadana politika je `MarketableLimit`; `PassiveLimit` i `Market` mogu se odabrati konfiguracijom, ali gate uvijek mjeri sve tri politike radi audita i kasnije kalibracije.
- `AnalysisOnly` provodi quote/latency/edge provjere, ali ne zove broker risk refresh. Paper/live uvijek zahtijeva valjan račun, svježe broker stanje i aktivnu trajnu rezervaciju.
- CI koristi determinističke fake servise i fallback bez službenog IBKR DLL-a. Stvarni TWS paper latency, fill/slippage i callback ponašanje moraju se izmjeriti u točki 17 prije bilo kakvog live dopuštenja.
- Short execution i dalje nije omogućen; borrow i direction-aware order/exit sigurnost ostaju obvezni prije paper/live short trgovanja.

## Točka 17 — izvedeno

Verifikacija: [GitHub Actions run 36761349366](https://github.com/dejanakadex/aiTradingBot/actions/runs/36761349366) — .NET 10 Release build i 298/298 testova, bez warninga i grešaka.

- `InstrumentRolloutService` za svaki konfigurirani instrument gradi trajni scorecard unutar podesivog vremenskog prozora. Bilježi broj shadow odluka i odobrenja, paper naloge/fillove, unfilled omjer, prosječni entry slippage prema završnom asku, decision-to-fill latency, quality incidente, nezdrave streamove i nerazriješene naloge.
- Background monitor periodički evaluira svaki instrument. Zdrav `ResearchReady` instrument prelazi u `ShadowReady`, a `ShadowReady` prelazi u `PaperReady` tek nakon minimalnog broja ukupnih i odobrenih shadow odluka.
- `PaperReady` ili `LiveEnabled` instrument automatski prelazi u `Suspended` ako nema market-data stream, stream je nezdrav, quality incidenti prelaze prag, nalog ostane u unknown/pending stanju ili broker reconciliation nije zdrav. Suspenzija odmah uklanja paper/live order dopuštenje iz postojećeg execution guarda.
- Paper score koristi stvarne trajne broker executione povezane preko signal arbitration zapisa. Live eligibility traži dovoljan uzorak paper naloga, prihvatljiv unfilled ratio, slippage i latency te odsutnost kritičnih razloga.
- Live status nikad se ne dodjeljuje automatski. Endpoint za ručno odobrenje zahtijeva `ConfirmLiveTrading=true`, razlog, očekivanu registry verziju, `LiveTradingExplicitlyEnabled=true`, `TradingRequested=true`, aktualni `PaperReady` status i ponovno uspješno mjerenje paper kriterija.
- Read-only povijest dostupna je kroz `GET /api/rollout`; ručna evaluacija kroz `POST /api/rollout/{instrumentId}/evaluate`. Sve evaluacije i razlozi ostaju u SQLiteu kroz novu migraciju.
- Sigurne zadane vrijednosti i primjer bez tajni nalaze se u `TradingBot.Web/instrument-rollout.example.json`.

## Odluke i ograničenja točke 17

- Zadani pragovi (25 shadow odluka, 20 paper naloga, 3 bps prosječnog slippagea i 2500 ms prosječnog fill latencyja) početne su sigurnosne vrijednosti, ne potvrđene produkcijske konstante. Treba ih kalibrirati iz stvarnog TWS paper uzorka po instrumentu.
- Quality incident prag je zadano nula, pa je sustav namjerno osjetljiv. Ako feed normalno proizvodi bezopasne incidente, prag se mijenja tek nakon pregleda njihove distribucije; suspenzija se ne vraća automatski u trading-ready status.
- CI potvrđuje deterministički rollout, persistenciju i prijelaze s fake/persistiranim executionima. Ne potvrđuje stvarni IBKR routing, fill kvalitetu ni callback latency bez službenog `CSharpAPI.dll`-a i nadziranog TWS paper rada.
- Projekt još nema ugrađenu autentikaciju za operativne HTTP endpointove. Do dodavanja autentikacije web aplikaciju i posebno live-approval endpoint treba držati na lokalnom ili strogo ograničenom mrežnom pristupu.

## Točka 18 — izvedeno

- Dodan je `Microsoft.ML.LightGbm` kandidat nad verzioniranim research labelama. Ulaz koristi pattern confidence/type/direction/timeframe, normaliziranu likvidnost i volatilnost te cikličko vrijeme dana; cijena/spread nisu slučajno korišteni kao label leakage.
- Svaki fold ponovno trenira model samo na svojem purged training prozoru. Probability threshold bira se isključivo na training dijelu, walk-forward validacija koristi postojeće vremenske foldove, a završni model samo jednom prolazi netaknuti holdout.
- Deterministički confidence threshold ostaje baseline. Kandidat mora imati dovoljan broj selekcija, pozitivan out-of-sample expectancy i nadmašiti baseline nakon spremljenih troškova za konfigurirani broj baznih bodova.
- Model artifact, threshold, metrike, verzije feature/pattern/label ugovora te ulazni i izlazni SHA-256 spremaju se u SQLite. Odobrenje zahtijeva točan reviewed hash, reviewer i razlog; novo odobrenje supersedea staru verziju za isti instrument i strategiju.
- Entry pipeline zadano koristi samo odobreni model s točnim instrumentom, strategijom i feature verzijom. Inferencija se učitava i izvršava lokalno kroz ML.NET; nedostajući model, verzijski mismatch ili probability ispod praga blokira signal.
- U numerical načinu `IAiMarketAnalyzer` i `IAiTradeCritic` se ne razrješavaju niti pozivaju. Njihovi postojeći DTO-i služe samo kao deterministički compatibility adapter prema još uvijek zajedničkom strategy/risk sloju.
- Read-only pregled modela dostupan je kroz `GET /api/models/numerical` i `GET /api/models/numerical/{modelId}`. Trening i ručna odluka namjerno nisu izloženi kao neautenticirani HTTP mutation endpointi.
- Sigurna konfiguracija nalazi se u `TradingBot.Web/numerical-model.example.json`; `UseApprovedModelForEntry=true` je zadano fail-closed ponašanje.
- Testovi pokrivaju LightGBM pobjedu nad baselineom na sintetičkom walk-forward/holdout skupu, ručno odobrenje, lokalnu predikciju, nedostajući odobreni model, konfiguracijsku validaciju i dokaz da production entry put zaobilazi oba mrežna LLM servisa.

## Odluke i ograničenja točke 18

- Pozitivan sintetički test dokazuje mehaniku, a ne produkcijski edge. Svaki instrument/strategija mora prikupiti dovoljan stvarni labeled uzorak i zasebno proći isti walk-forward, holdout, shadow i paper protokol.
- Research labele već sadrže procijenjeni trošak, dok finalni execution gate neposredno prije naloga ponovno provjerava aktualni spread, proviziju, slippage, latency i neto edge.
- Model promotion ostaje interna operacija dok se ne uvedu autentikacija i autorizacija za operativne mutation endpointove. Read-only model API ne može trenirati, odobriti niti poslati nalog.
- LLM implementacije ostaju dostupne za eksplicitno isključen `UseApprovedModelForEntry` legacy/offline tok, ali nisu dio zadane produkcijske entry latencije.
- Stvarno short izvršavanje i dalje nije omogućeno; numerički model može učiti direction feature, ali execution sigurnost i borrow provjera ostaju zaseban preduvjet.

## Točka 19 — stvarni IBKR adapter i dijagnostika

- Lokalna provjera sa službenim `CSharpAPI.dll` 10.49.02.0 kompajlira `IBKR_API_AVAILABLE` put, a Release build i solution testovi prolaze. `scripts/verify-ibkr-adapter.ps1` ponavlja restore, build, SHA-256 usporedbu kopiranog DLL-a i testove za eksplicitno zadanu putanju DLL-a. DLL se ne sprema u Git.
- `GET /api/ibkr/diagnostics` odvojeno prikazuje adapter (`Real`, `Custom`, `Unavailable`), verziju C# API-ja, verziju broker servera samo nakon spajanja, stanje veze, paper-account konfiguraciju i potvrdu da se konfigurirani račun nalazi među brokerovim managed accounts. Identifikatori računa nisu u odgovoru ni u novom log zapisu.
- Za svaki instrument dijagnostika pokazuje podržava li sadašnji adapter contract (`STK/SMART/USD`), status broker provjere, zadnju grešku kao kod, stanje collection streamova i dostupnost svježeg bid/aska. `probeInstrumentId` šalje samo `reqContractDetails` za jedan konfigurirani instrument; rezultat razlikuje `Confirmed`, `NotFound`, `Ambiguous`, `Mismatch`, `BrokerError` i `TimedOut`.
- Obični prikaz ne šalje broker zahtjeve. Provjera contracta traži aktivnu TWS/IB Gateway vezu; build i fake testovi ne dokazuju ni handshake, pravo na podatke ni paper nalog. `STK/SMART/USD` contract ostaje za izvršavanje i pretplate. Operativni endpoint treba ograničiti pristupom dok autentikacija nije uvedena.

## Točka 20 — tick-by-tick feed i trajno prikupljanje

- Stvarni adapter zasebno traži brokerove `AllLast` i `BidAsk` tick-by-tick streamove po omogućenom instrumentu. Više ne izvodi bid/ask/trade iz `reqMktData` na 1m pretplati. Brokerov Unix timestamp je `EventTimeUtc`, a stvarni primitak callbacka je `ReceivedTimeUtc`; cijena i dostupna veličina čuvaju se za trade i obje strane kotacije. Jednaki nepromijenjeni bid/ask callbackovi se preskaču, a tradeovi se ne spajaju samo zato što imaju istu cijenu i vrijeme.
- Bounded per-instrument tick channel šalje događaje sekvencijalno kroz postojeći canonical quality gate i Parquet writer. `EventId` je jedinstven u pretplatničkoj sesiji; broker ne daje globalni tick ID, stoga se savršena deduplikacija preko reconnecta ne može dokazati. Prelijevanje kanala je detektirano i pokreće reconnect; nema tihog `DropOldest` ponašanja.
- Novi collection worker radi neovisno o trgovanju i paralelno s postojećim povijesnim backfillom barova. Per-instrument status pokazuje heartbeat, lag i reconnect. SQLite `TickCoverageGapRecords` i read-only `GET /api/market-data/tick-gaps` čuvaju prekide, uključujući restart i overflow. Ti intervali imaju nepoznatu stvarnu količinu izgubljenih tickova; 1m gap-fill ih ne označava popravljenima.
- Lokalni testovi pokrivaju mapiranje brokerskog vremena/veličina i overflow buffera te paralelno prikupljanje dva instrumenta i trajni zapis prekida. Build sa službenim DLL-om potvrđuje potpis callbacka, ali bez TWS/IB Gateway paper sesije ne potvrđuje pravo na real-time podatke, stvarni callback slijed, povijesne tickove ni mjereni throughput. Flush Parquet writera ostaje asinkron; pad procesa između prihvata u red i flushanja može ostaviti nepokriven interval koji treba otkriti operativnom provjerom.
