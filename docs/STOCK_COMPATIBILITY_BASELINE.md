# FX-1 — postojeće dioničke funkcionalnosti i regresijska osnova

Datum pregleda: 23.–25.10.2026. Polazni shared commit: `d4bf83c` na `trading-bot-v2`; radni checkout `75fe6e0` ima identično Git stablo `61a275c85d4cd09944803bb82a575dbd18a2f716`. Oba checkouta bila su čista. SDK: .NET `10.0.402`, prema `global.json` (`10.0.100`, `latestFeature`).

Ovo je početna osnova za aditivnu EUR/USD nadogradnju. SPY i ostale dionice ostaju podržane. FX-1 dodaje dokumentaciju i regresijske testove; ne mijenja produkcijske servise, postavke, pakete, migracije ni trading dozvole.

## Razine dokaza

- **Implementirano**: funkcionalnost postoji u produkcijskom kodu.
- **Lokalno provjereno**: unit/integration/fake-broker testovi prolaze; koriste se sintetički podaci i privremene baze.
- **Stvarno IBKR paper potvrđeno**: potreban je zaseban zabilježeni test s TWS callbackovima. Build službenog adaptera nije takav dokaz.

Trenutno nema potvrđenog stvarnog SPY ni EUR/USD end-to-end order ciklusa. Prethodni nezavisni EUR/USD read-only probe potvrđuje samo dostupnost FX quote feeda na tadašnjoj vezi. Ne potvrđuje FX podršku botova adaptera. [Shadow dokazi](SHADOW_READINESS.md).

## Matrica funkcionalnosti

U stupcu SPY izraz **lokalno** znači implementirano i provjereno simulacijom, ne stvarnim paper trgovanjem. Za EUR/USD zajednički servis predstavlja osnovu za ponovnu uporabu; njegova FX integracija još nije provedena.

| Funkcionalnost i postojeći kod | SPY / dionice sada | EUR/USD sada i potrebna nadogradnja | Regresijska provjera koju čuvamo |
| --- | --- | --- | --- |
| Konfiguracija i identitet: `TradingSettings`, `InstrumentSettings`, `PipelineContext` | Lokalno: instrument/strategija/signal identitet, više instrumenata i legacy `Symbols` | Polja za tip ugovora postoje; identitet različitih parova s istim osnovnim simbolom još nije riješen (FX-2) | `TradingSettingsTests`, `DomainModelsTests` |
| IBKR ugovori i dijagnostika: `IbkrBrokerService`, `IbkrDiagnosticsService` | `BuildStockContract` gradi `STK/SMART/USD`; službeni adapter se uvjetno kompilira. Dijagnostika lokalno testirana | Bot ne gradi CASH/IDEALPRO; nema potvrđenog FX contract/order puta (FX-3) | `IbkrDiagnosticsServiceTests`, `IbkrConnectionServiceTests`, `InfrastructureServiceRegistrationTests`; zaseban real-DLL build |
| Bar i tick prikupljanje: `MarketDataSubscriptionHostedService`, `TickMarketDataHostedService` | Lokalno: `TRADES`, `AllLast`/`BidAsk`, neovisnost od trading readinessa, reconnect, heartbeat i gapovi | Dodati FX vrstu podataka i raspored; nezavisni FX probe nije ovaj tok (FX-4) | `MarketDataSubscriptionHostedServiceTests`, `TickMarketDataTests`, `MarketDataPipelineTests` |
| Backfill i pohrana: `HistoricalBackfillService`, `ParquetMarketDatasetStore` | Lokalno: checkpoint, retry/pacing, dedupe, manifest/hash, finalnost i kvaliteta | Proširiti na FX price source bez pretvaranja quote sizea u trade volumen (FX-4) | `HistoricalBackfillServiceTests`, `ParquetMarketDatasetStoreTests`, `CanonicalMarketDataTests` |
| Kratki agregati: `LatestMarketDataService`, `ShortSignalTriggerService` | Lokalno: trade OHLCV 5s/15s, opcionalno 1s, 2s zadana lateness, eksplicitne praznine | Quote-derived barovi još ne postoje u botovu FX toku (FX-5) | `ShortAggregateTests`, `StreamingBarCompletionBufferTests`, `ShortSignalTriggerTests` |
| Canonical featurei: `FeatureEngine`, `FacioQuoIndicatorAdapter`, `MarketSnapshotService` | Lokalno: as-of i identitet, trend, volumen, volatilnost, spread i režim; detalji niže | Zajednički izračuni ostaju; potrebno definirati FX input i dostupnost volume featurea (FX-5) | `FeatureEngineTests`, `FacioQuoIndicatorAdapterTests`, `MarketSnapshotServiceTests`, novi `StockCompatibilityBaselineTests` |
| Patterni: `PatternDetector`, `PatternQualityGate` | Lokalno: 10 pravila, hard uvjeti, komponente scorea, razlozi, dedupe; short je research-only | Price geometrija je ponovno uporabiva; FX pragovi, volume uvjeti i verzije još nisu provedeni (FX-6) | `PatternDetectorTests`, `PatternQualityGateTests`, `PatternEngineV2PersistenceTests`, novi baseline |
| Strategije i ulazne odluke: `PatternDecisionBackgroundService`, `StrategyEngine` | Lokalno: više strategijskih identiteta, quote/context gate, long entry/stop/target; numerički ili eksplicitni legacy LLM put | FX sesija, pragovi i model odobrenje po paru tek slijede (FX-7) | `StrategyEngineTests`, `BackgroundPipelineTests`, `AiPipelineOptimizationTests`, `ShortSignalTriggerTests` |
| Portfolio risk i arbitraža: `RiskEngine`, `PositionSizer`, `PortfolioRiskService`, `SignalArbitrationService` | Lokalno: rizik, buying power, exposure, loss/cooldown, atomske rezervacije, virtualna atribucija i kontrola izlazne količine | Sizer/vrednovanje pretpostavljaju dionice; FX jedinice, saldo i preračunavanje valuta tek slijede (FX-8) | `RiskEngineTests`, `PortfolioRiskServiceTests`, `SignalArbitrationServiceTests` |
| Finalni ulazni gate: `ScalpingExecutionGate` | Lokalno: Market/PassiveLimit/MarketableLimit politika, freshness/skew/latency, edge nakon troška | Dodati FX troškovni profil i brokerove korake cijene/količine (FX-8) | `ScalpingExecutionGateTests`, `TradingExecutionGuardTests` |
| Nalozi i izlazi: `OrderManager`, `ExitManagementService`, `ExitManagementHostedService` | Lokalno: durable intent, partial fills, provizije, FixedBracket, ATR trailing/break-even i neovisni sekundni timer | FX ugovor i količina te potvrda podržanih order tipova tek slijede (FX-9) | `OrderManagerTests`, `ExitManagementServiceTests`, `ExitManagementHostedServiceTests`, `FakeIbkrOrderLifecycleTests` |
| Restart i usklađivanje: `BrokerStateReconciliationService`, operativne zaštite | Lokalno: unknown outcome, reconnect, mismatch, suspenzija i idempotencija | Provjeriti FX callbackove/valutna salda i razliku prema botovoj atribuiranoj poziciji (FX-9) | `BrokerStateReconciliationTests`, `OperationalSafetyTests`, `SafetyControlServiceTests` |
| Replay: `DeterministicReplayService`, `ReplayTradeSimulator`, `ReplayBacktestService` | Lokalno, djelomična točka 25: kratki/bar signali, research, dostupnost quotea, long fixed-bracket fill/exit simulacija s troškom | FX source, količina, trošak i puni produkcijski model/strategy/risk/order paritet ostaju za FX-10 i točku 25 | `DeterministicReplayServiceTests`, `ReplayTradeSimulatorTests` |
| Research/labele/evaluacija: `CandidateResearchService`, `CandidateLabelCalculator`, research servisi | Lokalno: svi ishodi kandidata, covered-tick SL/TP/timer labele, purge, holdout, cost stress i kalibracija | FX quote labele i različite podatkovne verzije još nisu potvrđene (FX-10) | `CandidateResearchServiceTests`, `CandidateLabelCalculatorTests`, `ResearchEvaluation*Tests`, `ResearchCalibration*Tests` |
| Modeli: `NumericalModelService`, `TradingBot.ModelOps` | Lokalno: LightGBM trening, hash/verzije, approval/reject/rollback, cache i lokalna inferencija; CLI postoji | Nema odobrenog EUR/USD modela; ne nasljeđuje SPY odobrenje (FX-10) | `NumericalModelServiceTests`, `PaperFlowEndToEndTests` |
| Registry/rollout/UI: `InstrumentRegistryService`, `InstrumentRolloutService`, dashboard/analytics, Blazor `Index`, `Patterns`, `Trades` | Lokalno servisno: onboarding, research/shadow/paper uvjeti, audit i analitika; nema nove vizualne UI provjere u FX-1 | FX prikaz jedinica, razloga blokade i specifične readiness metrike tek slijede (FX-11) | `InstrumentRegistryServiceTests`, `InstrumentRolloutServiceTests`, `DashboardAnalyticsServiceTests`, `ShadowPerformanceTests` |
| Cijeli tok | Lokalno: fake broker + SQLite onboarding/model/signal/risk/entry/fill/SL/TP/timer/restart i dva instrumenta | FX scenariji još nisu implementirani (FX-11); stvarni test slijedi u FX-12 | `PaperFlowEndToEndTests`; stvarna IBKR paper potvrda ostaje otvorena |

## Stvarni katalog patterna

Izvor istine je `PatternDetector.Evaluate`, ne samo `PatternType` enum. Detekcija pozitivnog oblika nije odobrenje trgovanja: `PatternQualityGate`, model, strategija, risk i execution gate imaju dodatne uvjete.

| Long / short par | Postojeća semantika | Što štitimo za SPY pri FX nadogradnji |
| --- | --- | --- |
| Hammer / ShootingStar | Tijelo i wickovi, položaj closea, pullback/context, lokacija i volume score | Iste hard geometrijske uvjete, score komponente i stock pragove |
| BullishEngulfing / BearishEngulfing | Obuhvat tijela, veličina u odnosu na ATR, context i volume score | ATR uvjete i obveznu volume potvrdu zadane stock konfiguracije |
| DoubleBottom / DoubleTop | Dva pivota, razmak, rebound, sličnost i neckline potvrda | Razlikovati detektirani oblik od potvrđenog trade setupa; DoubleBottom gate traži neckline break |
| BreakoutAndRetest / BreakdownAndRetest | Proboj, tolerancija retesta, bounce, context i volume score | Postojeće bodovanje i obveznu volume potvrdu zadane stock konfiguracije |
| VwapReclaim / VwapReject | Stvarni volume-weighted prosjek, prijelaz, prethodna strana VWAP-a i ATR-normalizirana potvrda | Bez volumena nema valjanog VWAP patterna; FX alternativa ne smije promijeniti značenje SPY pravila |

Svih pet short patterna postoji za istraživanje, ali `PatternQualityGate` izričito blokira short izvršavanje. `HeadAndShoulders` i `Triangle` postoje samo kao enum vrijednosti, bez pravila u aktualnom `Evaluate`; `Unknown` je sentinel. To nisu dovršene SPY funkcionalnosti koje bi se mogle samo uključiti za EUR/USD. Ništa iz enuma se ne uklanja.

Volume score sudjeluje u svih 10 aktivnih pravila. Sadašnji helper za manje od šest svijeća ili nepozitivan prethodni volumen vraća neutralnih `0.5`; taj fallback nije dokaz kvalitetnog FX volumena. FX-6 mora eksplicitno definirati dostupnost podataka i bodovanje, bez globalne promjene stock profila.

## Indikatori i načini izlaza

`FeatureEngine` koristi FacioQuo adapter za EMA12/EMA26 i RSI14 (zadane vrijednosti). ATR14 u canonical putu ostaje postojeći vlastiti izračun; adapter podržava ATR, ali se poziva s `includeAtr: false`. Time se pri FX nadogradnji ne smije prešutno promijeniti SPY ATR metoda.

Ostali featurei: VWAP, prosječni i relativni volumen, promjena cijene, udaljenost od VWAP-a, recent high/low, trend, volatilnost, ATR/price, momentum, mean-reversion z-score, EMA separation (% i ATR), dollar volume i normalized liquidity, spread (apsolutni/bps/ATR), quote age te režim Trending/Ranging/Volatile/Unknown. Missing/warmup/as-of semantika pripada feature ugovoru. Feature/label/model verzije moraju odvojiti budući FX izvor ili izmijenjeni izračun.

`FixedBracket` je zadani izlaz: fiksni broker stop i target, usklađivanje partial fillova te cancel potvrde prije upravljanog izlaza. `AtrTrailing` čuva break-even i trailing po konfiguriranom ATR timeframeu; stop se ne spušta. Sekundni rok od prvog filla nadzire se neovisno o svijeći. Načini izlaza imaju različitu semantiku i oba se čuvaju; ne tvrdi se da su oba već pokrivena punim replayem ili stvarnim paper testom.

## Regresijski ugovor za sljedeće FX točke

1. Stock konfiguracija bez novih FX polja mora nastaviti raditi. Novi instrument ne mijenja SPY contract, source, session, strategijske pragove ni dozvole. Zajedničke matematičke komponente se ponovno koriste; posebna pravila biraju se po instrumentu/profilu.
2. Ne uklanjati patterne, volume featuree, ATR trailing, fiksni bracket, legacy konfiguraciju ni postojeće testove radi prolaska FX promjene. Popravak postojećeg buga dopušten je kao eksplicitno obrazložena i testirana promjena, ne kao prikrivena posljedica FX rada.
3. Nakon svake FX točke pokrenuti odgovarajuće nove testove i cijeli solution. Pri brokerskim/DI/build promjenama dodatno provjeriti službeni DLL i fallback build. CI već automatski uključuje novi testni razred; postojeći Linux CI provjerava fallback put.
4. Numerički baseline se ne ažurira automatski kad test padne. Potrebno je pregledati koji se SPY rezultat promijenio i je li promjena namjerna. Fiksne vrijednosti ne zamjenjuju testove scenarija ni stvarni feed.
5. Promjene tržišnih/podatkovnih ugovora moraju imati verzije; stari stock modeli i dataset ne smiju se prešutno reinterpretirati kao FX podaci.
6. Mješoviti SPY + EUR/USD testovi postaju obvezni čim FX tok postoji. U FX-1 takav tok još ne postoji pa nije označen kao potvrđen. Postojeći testovi paralelizma s dionicama ostaju zaštita.

## Novi fiksni numerički baseline

[`StockCompatibilityBaselineTests`](../TradingBot.Tests/StockCompatibilityBaselineTests.cs) dodaje 13 slučajeva: 10 pozitivnih stock detekcija s fiksnim finalnim i komponentnim scoreovima, jedan 40-bar SPY feature uzorak sa svim numeričkim featureima, identitetom i režimom, te dva slučaja odbijanja VWAP patterna bez volumena. Brojevi su očitani iz neizmijenjenih produkcijskih servisa polaznog commita i spremljeni kao konstante za usporedbu, zaokružene na 12 decimala.

Pattern fixturei su sintetički OHLCV uzorci s eksplicitnim testnim pivot/retest parametrima. Dvobarni hammer/engulfing fixture namjerno čuva postojeći neutralni volume fallback, a šestobarni double-pivot i devetobarni breakout imaju izračunat volume score. Feature fixture koristi zadani canonical profil, stvarne nenulte fixture volumene i svježi bid/ask. Ti podaci nisu stvarna SPY povijest ni dokaz da signal prolazi sve trading gateove.

Postojeći `PatternDetectorTests` dodatno čuva negativne hard-condition slučajeve svih 10 pravila, deduplikaciju po instrumentu/strategiji/timeframeu i kratki timeframe. Quality-gate, risk, lifecycle i E2E testovi ostaju glavni dokaz ponašanja iz matrice; novi baseline ih nadopunjuje.

Brza provjera samo novog baselinea:

```powershell
dotnet test TradingBot.Tests/TradingBot.Tests.csproj -c Release --filter FullyQualifiedName~StockCompatibilityBaselineTests
```

Puna regresija:

```powershell
dotnet test TradingBot.sln -c Release
```

Službeni adapter dodatno provjerava postojeća `scripts/verify-ibkr-adapter.ps1` skripta (kompilacijski simbol, DLL hash, restore/build/test). Fallback je u ovoj provjeri izgrađen i testiran u Debug konfiguraciji s eksplicitnim `-p:IbkrApiDll=C:\__fx1_no_ibkr__\CSharpAPI.dll`, nakon potvrde da ta datoteka ne postoji. Ne smiju se pomiješati outputi dvaju build načina.

Rezultati izvedene FX-1 provjere i konačan status nalaze se u [statusu implementacije](IMPLEMENTATION_STATUS.md#fx-1--popis-funkcionalnosti-i-zaštita-dioničkog-ponašanja). Točke 25, 30 i 31 zadržavaju svoja prethodna otvorena ograničenja.
