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
| 5 — Dataset storage | Sljedeće | Particionirani Parquet raw/research storage, manifest, schema version i hash bez punjenja operativnog SQLitea tickovima. |
| 6–18 | Na čekanju | Redoslijed i kriteriji nalaze se u `V2_PLAN.md`. |

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

## Sljedeći checkpoint — točka 5

Izgraditi particionirani Parquet storage za raw/research podatke: stabilna struktura po instrumentu/datumu/vrsti, manifest, schema version, hash i reproduktivan read path bez punjenja operativnog SQLitea tickovima.
