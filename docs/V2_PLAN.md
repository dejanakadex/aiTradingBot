# Plan nastavka — multi-instrument scalping v2

Status: **odobreno za implementaciju jednu točku po jednu**. Zajednički radni branch je `trading-bot-v2`. Nakon svake točke kod, testovi, dokumentacija i CI moraju biti završeni prije nastavka.

Napredak 2026-09-23: projekt koristi .NET 10 LTS, `global.json`, GitHub Actions i Dependabot. Početni CI Release build prošao je bez upozorenja i grešaka uz 195/195 testova. Conditional IBKR adapter i dalje treba zasebno provjeriti sa službenim `CSharpAPI.dll`.

Osnova: [detaljni pregled i nalazi R01–R16](PROJECT_REVIEW.md). Operativni checkpointi vode se u [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md).

## Cilj i granice

Cilj više nije jedan long SPY trade. Sustav treba podržati više konfiguriranih instrumenata, više strategija/signala, više istovremenih pozicija i velik broj malih intraday tradeova kada statistički potvrđena prilika pokriva spread, proviziju, slippage i sigurnosni buffer.

Predviđeni holding je od nekoliko sekundi do nekoliko minuta. To je automatizirani retail scalping, ne HFT: IBKR TWS API i postojeća hosted-service arhitektura nisu namijenjeni submilisekundnom izvršavanju.

Ključne sigurnosne granice:

- prikupljanje i obrada podataka odvojeni su od dozvole za slanje naloga;
- novi instrument nikad ne prelazi iz backfilla izravno u live trading;
- risk se provodi globalno, po instrumentu i po strategiji;
- brokerova neto pozicija ostaje izvor istine;
- AI/LLM ne može preskočiti deterministic risk i nije planiran u latency-critical scalping putu;
- projekt ostaje `AnalysisOnly` dok portfolio risk, order lifecycle i operativne kontrole nisu završeni.

## Onboarding instrumenta

Dodavanje konfiguriranog instrumenta pokreće ovaj lifecycle:

1. validacija konfiguracije i broker contracta;
2. početak live prikupljanja kako tijekom backfilla ne bi nastala nova praznina;
3. resumable historical backfill s pacingom, retryjem i checkpointom;
4. deduplikacija, gap analiza i izrada canonical agregata;
5. replay featurea, patterna i labela;
6. readiness statistika;
7. shadow pa paper provjera;
8. zasebno ručno dopuštanje live tradinga.

Planirani statusi registra: `Disabled`, `BackfillPending`, `Backfilling`, `Collecting`, `WarmingUp`, `ResearchReady`, `ShadowReady`, `PaperReady`, `LiveEnabled`, `Suspended`, `Faulted`.

## Redoslijed implementacije

| Točka | Sadržaj | Kriterij završetka |
| --- | --- | --- |
| 1 — Multi-instrument temelj | Konfigurirani instrumenti, stabilni `InstrumentId`/`StrategyId`/`SignalId`/`CorrelationId`, verzije ugovora i status dokument. | Više instrumenata i per-instrument timeframeovi prolaze validaciju i pokreću očekivane pretplate; isti signal zadržava identitet kroz strategy/risk tok. |
| 2 — Registry i onboarding | Persistirani instrument registry, statusi, broker metadata i readiness odvojen od trading dozvole. | Dodavanje instrumenta idempotentno pokreće onboarding; restart nastavlja zadnji status; live ostaje isključen. |
| 3 — Canonical market data | Bid/ask/trade događaji, source/receive vrijeme, finalnost i kvaliteta; kratki agregati te 1m/5m/15m kontekst. | Svaki događaj ima instrument i event-time; stale/out-of-order/gap stanja su vidljiva i ne mogu prešutno pokrenuti trade. |
| 4 — Historical backfill | Resumable segmenti, pacing, retry, checkpoint, dedupe i gap report; široka 1m povijest i najveća praktična granularna povijest. | Prekid/restart ne duplicira podatke; nedostajući intervali su mjerljivi; live collection radi paralelno. |
| 5 — Dataset storage | SQLite za operativno stanje; particionirani Parquet za raw/research podatke; manifest, schema version i hash. | Dataset je reproducibilan po instrumentu, datumu i vrsti podataka bez punjenja operativne baze tickovima. |
| 6 — Neovisna collection pouzdanost | Collection radi u `AnalysisOnly`, pauzi i bez AI-ja; heartbeat, reconnect, lag i automatski gap-fill. | Trading readiness ne zaustavlja skupljanje; kvar jednog streama je detektiran zasebno. |
| 7 — Deterministički replay | Isti feature/pattern kod kao live, event-time redoslijed, brzina, pause/resume i checkpoint. | Isti input i verzije daju iste signale; budući podaci nisu dostupni. |
| 8 — Canonical featurei | VWAP, ATR, RSI, EMA, relativni volumen, spread, momentum, mean reversion, režim i normalizacija likvidnosti/volatilnosti. | Live, backfill i replay daju jednake feature vrijednosti za isti `asOf`. |
| 9 — Pattern engine | Pattern + instrument + strategija + timeframe identitet; hard uvjeti, score komponente i razlozi; long/short domena. | Pozitivni i negativni testovi za svaki pattern; nema konflikta deduplikacije između instrumenata/timeframeova. |
| 10 — Kandidati i labele | Spremanje prihvaćenih, odbijenih i blokiranih kandidata; 5s/15s/30s/1m/3m/5m, MFE/MAE i target/stop-first labele. | Svaka labela koristi samo naknadne podatke i uključuje realističan trošak. |
| 11 — Evaluacija | Statistika po instrumentu, strategiji, vremenu, režimu i likvidnosti; walk-forward, expectancy, profit factor, drawdown i osjetljivost na trošak. | Odluka o pragu ne temelji se samo na win rateu; završni period ostaje netaknut. |
| 12 — Kalibracija/rangiranje | Kalibrirane vjerojatnosti, stabilni pragovi i rangiranje konkurentnih prilika uz verzioniranu ručnu potvrdu. | Promjena praga/modela je auditirana i uspoređena s baselineom. |
| 13 — Portfolio risk | R01–R02 plus globalni/per-instrument/per-strategy limiti, pending rezervacije, gross/net exposure, cooldown i korelacijski limit. | Paralelne odluke ne mogu rezervirati isti kapital; nulti kapacitet uvijek odbija nalog. |
| 14 — Signal arbitration | Paralelni instrumenti; eksplicitna politika konflikta strategija na istom instrumentu; virtualna atribucija nasuprot broker net poziciji. | Suprotni ili duplicirani signali ne mogu proizvesti nekontrolirane naloge ili prodati tuđu količinu. |
| 15 — Order/position lifecycle | R03–R08 i R12: durable intent, partial fills, commission, stop/target/time-exit, cancel/modify potvrda, restart i reconciliation. | Entry/exit su idempotentni; ukupni izlaz ne prelazi fillanu količinu; nepoznato broker stanje blokira nove ulaze. |
| 16 — Scalping execution | Finalna quote/risk provjera, latency budget, edge-after-cost gate, market/limit/marketable-limit mjerenje i holding u sekundama. | Nalog se ne šalje na stale quote ili kad očekivani pomak ne pokriva procijenjeni trošak i buffer. |
| 17 — Shadow i paper | Readiness po instrumentu, shadow, paper i postupni live rollout; automatska suspenzija na feed/order mismatch. | Svaki instrument zasebno prolazi unaprijed definirani protokol; live dopuštenje je ručno. |
| 18 — Numerički model i LLM | Jednostavan baseline, zatim LightGBM kandidat i lokalna .NET inferencija; LLM za offline kontekst/objašnjenja. | Model nadmašuje deterministički baseline na walk-forward testu nakon troškova i bez mrežne latencije u entry putu. |

## Pravila paralelizma

- Signali različitih instrumenata mogu se obrađivati i izvršavati paralelno.
- Jedan instrument u početnoj sigurnoj verziji ima jednu stvarnu neto broker poziciju.
- Više strategija na istom instrumentu dobiva virtualnu atribuciju, ali naloge koordinira portfolio/order sloj.
- Suprotni signali ne netiraju se prešutno; prolaze eksplicitnu `Reject`, `Priority` ili kasnije odobrenu `Net` politiku.
- Long i short podržani su u ugovorima i uključuju se zasebno po instrumentu. Short izvršavanje traži dodatnu broker/borrow provjeru prije paper/live aktivacije.

## Podatkovni i evaluacijski ugovori

- Raw market event ima stabilan ID, instrument, source-time, receive-time, izvor, sesiju, finalnost i quality status.
- Signal ima stabilni `SignalId`; jedan konkretan prolaz pipelineom ima `CorrelationId`.
- Verzije market-data, feature, pattern, strategy/label/model ugovora spremaju se uz odluke.
- Svi kandidati i odbijanja ulaze u dataset, ne samo izvršeni ili dobitni tradeovi.
- Cross-instrument trening koristi vremenske/group podjele koje sprečavaju leakage između preklapajućih razdoblja.
- Rezultat se računa nakon provizije, spreada, slippagea i pretpostavke izvršenja.
- Količina granularne povijesti ovisi o provideru; vlastito kontinuirano prikupljanje čuva podatke koje kasnije možda nije moguće ponovno preuzeti.

## Potvrđene početne odluke

1. Više instrumenata i više paralelnih pozicija zamjenjuju SPY/single-position pretpostavku.
2. Konfiguracija podržava više strategija po instrumentu i per-instrument timeframes/limite.
3. Jedna neto broker pozicija po instrumentu ostaje početna sigurnosna granica.
4. Long i short nisu hardkodirano ograničeni, ali se uključuju zasebno.
5. SQLite ostaje operativna baza; Parquet je planirani research/raw format.
6. Novi instrument automatski prikuplja, backfilla i obrađuje podatke, ali ne postaje automatski live-enabled.
7. LLM se postupno uklanja iz latency-critical entry puta ako mjerenje potvrdi da usporava kratke tradeove.
8. Zaštitni izlaz može realizirati kontrolirani gubitak; pravilo „nikad prodati s gubitkom” nije sigurnosno prihvatljivo.

API ključevi, broker računi i službeni IBKR DLL ostaju lokalna konfiguracija i ne upisuju se u Git.
