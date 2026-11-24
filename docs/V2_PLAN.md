# Plan nastavka — multi-instrument scalping v2

**N5 jutarnja provjera, 24.11.2026.:** [noćno prikupljanje i početak trening prozora](N5_DATASET_READINESS.md#jutarnja-provjera--24112026) potvrđuju aktivan read-only kolektor. TWS je bio nedostupan 23:45–01:03 Zagreb; korisnik potvrđuje naknadno ručno pokretanje. Noćni raw snapshot ima dva nepotpuna dijagnostička para već označena `Stale/CanPersist=false`, pa strogi replay opravdano odbija cijeli sirovi ulaz. Prvih 20 minuta današnje sesije ima 22.957 potpunih parova, 238/239 zdravih 5s barova i 188 zagrijanih frameova; jedan bar potisnut je zbog zakasnjelih kotacija. Brokerska pravila obnovljena su prije isteka, bez promjene starih vremena. **N5 ostaje otvoren**, rezervirane podjele nisu mijenjane. Noćna queue/storage kašnjenja prelaze dio pragova; nema naloga, approvala ili promjene runtimea. [Dokazi](evidence/n5-overnight-2026-11-24.json).

**N5 priprema, 23.11.2026.:** [zamrznuti podaci i rezervirane podjele](N5_DATASET_READINESS.md) dodani su uz offline streaming pregled; **816/816 testova** i IBKR publish/guards prolaze. Kopija s 2.053.027 manifest redaka sačuvana je uz 82 coverage zapisa. Širi replay ulaz ispravno je odbijen zbog 33 nepotpuna jutarnja para; zasebno su provjerena 717.375 potpunih parova 20.11. i 134.203 para kasnije 23.11. Za kasniji 23.11. uzorak nedostaju tada važeća pravila; novo današnje opažanje nije retroaktivno. Budući trening/validacija/holdout unaprijed su rezervirani 24.–28.11. **N5 ostaje otvoren** do stvarnog prikupljanja i prihvata reprezentativnih sesija. Nema novih strateških rezultata, approvala ili naloga. [Dokaz](evidence/n5-preparation-2026-11-23.json).

**N4, 23.11.2026.: završeno implementacijom i simulacijom.** [Signal → execution → automatski post-trade](N4_TRADE_LIFECYCLE.md) sada daje jedan rezultat po zatvorenom tradeu, ponderirane fillove i ažuriranje kasnih provizija bez duplikata. Sačuvan je izvorni feature/model/risk kontekst; stock history/snapshot dobio je strogu `asOf` granicu. **810/810 Release testova**, IBKR publish/guards i offline feature/decision parity prolaze. Migracija i read-only kolektor provjereni su nakon restarta u 14:45 Zagreb, bez naloga. N5 je sljedeća planirana točka. Stvarni paper ciklus, model shadow, FX-12 i puni 25/30/31 ostaju otvoreni. [Strukturirani dokaz](evidence/n4-trade-lifecycle-2026-11-23.json). Stariji checkpointi niže su povijesni.

**N3 prihvat, 23.11.2026.:** [Dnevna sesija i paritet](N3_COLLECTION_LATENCY.md#prihvatna-sesija-i-paritet--23112026) potvrđuju završetak N3 za AnalysisOnly EUR/USD prikupljanje: 69 minutnih prozora, pipeline max 45,77 ms, queue max 40,73 ms, bez gapova/dropova/grešaka; 73.047 potpunih parova i 836/836 5s barova. Paritet stvarnih kotacija i kontroliranog prihvaćenog signala provjeren je; **803/803 testa** prolaze. Pozadinski SQL i dalje može trajati oko 2 s, pa paper izvršavanje nije time potvrđeno. N4 je sljedeći odobravani korak; FX-12 i 25/30/31 ostaju otvoreni. Nema naloga ni model approvala.

**N3 quality checkpoint, 23.11.2026.:** [Periodično spremanje izvedenog stanja](N3_COLLECTION_LATENCY.md#periodično-spremanje-quality-stanja--23112026) implementirano je i uključeno samo u AnalysisOnly EUR/USD kolektoru (1 s / maksimalno pending 5 s). Svaka kotacija i dalje se provjerava; incidenti se spremaju odmah, a neuspjeh ili zaostajanje blokira tok. Paper/live zadržava per-event spremanje. **802/802 testa** i IBKR guards prolaze; nova verzija je pokrenuta uz zabrane naloga. Potrebna je nova ≥60-minutna prihvatna sesija; **N3, FX-12 i 25/30/31 ostaju otvoreni**, N4 nije započet.

**N3 NVMe test uz aktivni feed, 23.11.2026.:** [dvadesetominutni test i povratna faza](N3_COLLECTION_LATENCY.md#nvme-usporedba-uz-aktivni-feed--23112026) završeni su uz isti PID i bez prekida. Pipeline maksimum je 986 ms uz NVMe AC 0/0, a 909 ms nakon vraćanja Balanced plana; **obje faze ne prolaze N3**. Potvrđena su 28.394 potpuna para i svih 479 očekivanih 5s barova, bez gapova/dropova. Izvorne postavke su vraćene, nema naloga. Slijedi pregled blokirajućeg spremanja quality stanja; trajna promjena napajanja nije opravdana ovim rezultatom.

**N3 stabilna sesija, 20.11.2026.:** [analiza i SQL dopuna](N3_COLLECTION_LATENCY.md#stabilna-večernja-sesija-i-dopuna-sql-mjerenja--20112026): 77.772 potpuna EUR/USD para, svih 1.259 finalnih 5s barova i 1.234 zagrijana framea. Nema gapova/dropova, ali 17 minuta ima lokalni pipeline maksimum >250 ms (najviše 1,883 s). Novo ReaderLifetime mjerenje stvarno je uhvatilo 1,553 s u dovršavanju UPDATE-a. Dublji I/O uzrok nije još dokazan; **N3 ostaje otvoren**. 781/781 testova i IBKR publish guard prolaze. Nema naloga ni model approvala.

**N3 checkpoint, 20.11.2026.:** [fazna mjerenja i stanje provjere](N3_COLLECTION_LATENCY.md) uvedeni su uz ispravak vremenskog flush-a i urednog završetka zapisa pri stopu. Nova mjerenja potvrđuju da gotovo dvije sekunde mogu nastati u SQLite spremanju quality stanja; dublji uzrok nije još dokazan. 780/780 .NET testova, 2 testa izvještaja i nepromijenjen N2 replay rezultat; stvarna prihvatna sesija i uklanjanje outliera još nedostaju. **N3 ostaje otvoren**, N4 nije započet; nema naloga ni model approvala.

**N2, 20.11.2026.:** [kontinuirani streaming research i dokazi](N2_STREAMING_RESEARCH.md) implementirani su i offline provjereni: 773/773 testa, isti rezultat pri različitim blokovima i oporavku u novi pokušaj. Stvarni dvosatni replay ima 223.922 događaja bez resetiranja portfelja; provjeren je i prelazak ponoći. Aktualne FX labele su v3. Reprezentativni višednevni podaci/evaluacija ostaju N5/N7; N3 je sljedeća planirana točka. FX-12 i puni opseg 25/30/31 ostaju otvoreni. Nema approvala ili naloga.

Status: **odobreno za implementaciju jednu točku po jednu**. Zajednički radni branch je `trading-bot-v2`. Nakon svake točke kod, testovi, dokumentacija i CI moraju biti završeni prije nastavka.

**N1, 20.11.2026.:** [valjanost kotacija i razlozi nepotpunih izlaza](N1_QUOTE_COVERAGE.md) implementirani su i lokalno provjereni (764/764). Četiri dnevna izlaza ostaju nepoznata; rezultat nije uljepšan. N1 commit `ed5355e` prošao je CI; novija N2 dopuna iznad ima prednost.

**Pregled 20.11.2026.:** [dovršenost ranijeg plana i predloženi nastavak N1–N12](NEXT_STEPS_2026-11-20.md). Lokalno ponovno prolazi 741/741 testova; nema stvarnog paper E2E ni dokazane profitabilnosti. Noviji pregled ima prednost pred povijesnim operativnim checkpointima niže.

Napredak 2026-09-30: programski su završene točke 1–18, uključujući portfolio risk, signal arbitration, trajni order/position lifecycle, finalni execution-grade scalping gate, mjerljivi shadow/paper rollout po instrumentu te walk-forward LightGBM model s lokalnom inferencijom u entry putu. To ne potvrđuje sve operativne kriterije ili stvarni odobreni EUR/USD model. Projekt koristi .NET 10 LTS, `global.json`, GitHub Actions i Dependabot. Službeni `CSharpAPI.dll` sada je obvezan, verzioniran u repozitoriju i uključen u build; raniji conditional fallback više nije aktualno stanje.

Nastavak: točke 19–24 i 26–29 lokalno su implementirane i testirane; točka 25 ostaje djelomična research simulacija, iako tick-only kratki signal sada prolazi kroz izolirani replay i virtualni entry/exit. Točka 28 dovršava trajne onboarding prijelaze i uvjete research/shadow/paper spremnosti, dok evaluacija, trening i odobrenje modela ostaju eksplicitni ModelOps postupak. Točka 29 provjerava cijeli servisni tok determinističkim fake brokerom i privremenim SQLiteom. Točka 30 je u tijeku: stvarni EUR/USD quote feed je potvrđen, ali odobreni model shadow nije. Ranije greške 10089/10189 odnose se na nedostupni stock feed; FX podaci ne potvrđuju SPY/QQQ prava. Stvarni IBKR paper end-to-end test još nije proveden. Detaljne granice su u [statusu implementacije](IMPLEMENTATION_STATUS.md) i [shadow izvještaju](SHADOW_READINESS.md).

Osnova: [detaljni pregled i nalazi R01–R16](PROJECT_REVIEW.md). Operativni checkpointi vode se u [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md).

FX nastavak 25.10.2026.: programski FX-1–FX-11 i lokalna regresija završeni su do onboardinga, valutnog prikaza i AnalysisOnly model mjerenja. Stvarni model shadow uzorak ostaje otvoren; read-only TWS veza/povijest potvrđeni su, naloga nije bilo. FX-12 zahtijeva zasebnu pripremu i odobrenje. [FX-11 kriteriji i dokazi](FX11_ONBOARDING.md).

## Cilj i granice

Operativni nastavak FX-12, 20.11.: [dnevna sesija](FOREX_COLLECTION.md#analiza-dnevne-sesije--20112026) daje 111.943 quote para i 1.283 zagrijana framea. Prvi baseline je neto negativan, četiri simulirana izlaza su nepotpuna. Višednevnu evaluaciju blokira prekratak raspon i jedan-replay/200k ograničenje; preostali lokalni zastoji traže mjerenje. Slijede pravila valjanosti kotacije, skalabilan dataset/replay, reprezentativna evaluacija i model shadow. Stare povijesne praznine ne smatraju se zatvorenima ovim dnevnim uzorkom.

FX-12 priprema 25.10.2026.: dodan je ponovljiv read-only preflight i [protokol ograničenog paper testa](FX12_PAPER_PROTOCOL.md). Stvarni E2E ostaje otvoren zbog nedovršenih model/shadow i operativnih preduvjeta; offline prolaz ne zamjenjuje FX-9 runtime zaštite. Sljedeći operativni korak je stvarni quote dataset tijekom otvorene sesije, zatim FX-10 model workflow i FX-11 model shadow prije naloga.

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

Nastavak nakon točke 18 provodi se redom, po jednoj točki. Točka 23 pokriva fiksni parent/stop/target bracket, partial-fill prilagodbu i potvrde otkazivanja konkurentnih izlaza. Točka 24 dodaje trajni rok od prvog filla i neovisni nadzor isteka u sekundama. Točka 31 ostaje stvarna IBKR paper provjera cijelog ciklusa; lokalni fake testovi i build adaptera ne zamjenjuju paper potvrdu.

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
7. Odobreni numerički model radi lokalno u latency-critical entry putu; LLM ostaje samo za eksplicitno odabrani legacy/offline kontekst i objašnjenja.
8. Zaštitni izlaz može realizirati kontrolirani gubitak; pravilo „nikad prodati s gubitkom” nije sigurnosno prihvatljivo.

API ključevi i broker računi ostaju lokalna konfiguracija i ne upisuju se u Git. Prema odluci 25.10.2026. službeni IBKR DLL je obvezna verzionirana ovisnost u `third_party/ibkr`, s licencom, pripadajućim izvorom i provjerom integriteta; obični build više nema fallback način bez njega.

## Aditivni EUR/USD nastavak

FX-10 je dodao bounded FX bid/ask replay, SL/TP/timer i trailing labele uz FX troškove, vremensku ML evaluaciju i ručno odobren lokalni model po paru/strategiji. Zasebni opt-in povezuje model s FX-9 pripremom; default ostaje isključen. U [FX-10 runbooku](FX10_RESEARCH.md) navedeni su ulazi, CLI, verzije, testovi i ograničenja. Sljedeća odobrenja odnose se na FX-11 onboarding/shadow i FX-12 stvarni paper dokaz.

FX-8 dodaje determinističku read-only procjenu količine u osnovnoj valuti, USD riska, troškova i valutne izloženosti. FX-9 povezuje eksplicitno odobren FX plan s osvježenim broker/portfolio snapshotom, zajedničkom atomskom rezervacijom, finalnim gateom i postojećim lifecycleom. Lokalno je provjeren long paper put za USD-kotirane parove i potvrđen USD sažetak računa; FX-8 `Passed` sam nije order approval. FX-10 nadograđuje taj put modelom i research replayem. Automatski FX-7 signal → model → entry put ostaje isključen u aktivnoj konfiguraciji; onboarding ostaje FX-11, a stvarni paper dokaz FX-12.

[Plan FX-1 do FX-12](EURUSD_READINESS.md) proširuje postojeću infrastrukturu na konfigurirane valutne parove, uz zadržavanje SPY-a i ostalih dionica. Izvodi se jedna odobrena točka po jedna. [FX-1 matrica i regresijska osnova](STOCK_COMPATIBILITY_BASELINE.md) bilježe aktivne patterne, indikatore, strategije, oba izlazna profila i granice trenutnog replaya. FX rad ne zatvara automatski točke 25, 30 ili 31 niti omogućuje live ili short izvršavanje. Aktualni dokaz provjere svake točke vodi se u [statusu implementacije](IMPLEMENTATION_STATUS.md).
