# Operativno EUR/USD prikupljanje

**N4, 23.11.2026.: završeno implementacijom i simulacijom.** [Signal → execution → automatski post-trade](N4_TRADE_LIFECYCLE.md) sada daje jedan rezultat po zatvorenom tradeu, ponderirane fillove i ažuriranje kasnih provizija bez duplikata. Sačuvan je izvorni feature/model/risk kontekst; stock history/snapshot dobio je strogu `asOf` granicu. **810/810 Release testova**, IBKR publish/guards i offline feature/decision parity prolaze. Migracija i read-only kolektor provjereni su nakon restarta u 14:45 Zagreb, bez naloga. N5 je sljedeća planirana točka. Stvarni paper ciklus, model shadow, FX-12 i puni 25/30/31 ostaju otvoreni. [Strukturirani dokaz](evidence/n4-trade-lifecycle-2026-11-23.json). Stariji checkpointi niže su povijesni.

**N3 prihvat, 23.11.2026.:** [Dnevna sesija i paritet](N3_COLLECTION_LATENCY.md#prihvatna-sesija-i-paritet--23112026) potvrđuju završetak N3 za AnalysisOnly EUR/USD prikupljanje: 69 minutnih prozora, pipeline max 45,77 ms, queue max 40,73 ms, bez gapova/dropova/grešaka; 73.047 potpunih parova i 836/836 5s barova. Paritet stvarnih kotacija i kontroliranog prihvaćenog signala provjeren je; **803/803 testa** prolaze. Pozadinski SQL i dalje može trajati oko 2 s, pa paper izvršavanje nije time potvrđeno. N4 je sljedeći odobravani korak; FX-12 i 25/30/31 ostaju otvoreni. Nema naloga ni model approvala.

**N3 quality checkpoint, 23.11.2026.:** [Periodično spremanje izvedenog stanja](N3_COLLECTION_LATENCY.md#periodično-spremanje-quality-stanja--23112026) implementirano je i uključeno samo u AnalysisOnly EUR/USD kolektoru (1 s / maksimalno pending 5 s). Svaka kotacija i dalje se provjerava; incidenti se spremaju odmah, a neuspjeh ili zaostajanje blokira tok. Paper/live zadržava per-event spremanje. **802/802 testa** i IBKR guards prolaze; nova verzija je pokrenuta uz zabrane naloga. Potrebna je nova ≥60-minutna prihvatna sesija; **N3, FX-12 i 25/30/31 ostaju otvoreni**, N4 nije započet.

**N3 NVMe test uz aktivni feed, 23.11.2026.:** [dvadesetominutni test i povratna faza](N3_COLLECTION_LATENCY.md#nvme-usporedba-uz-aktivni-feed--23112026) završeni su uz isti PID i bez prekida. Pipeline maksimum je 986 ms uz NVMe AC 0/0, a 909 ms nakon vraćanja Balanced plana; **obje faze ne prolaze N3**. Potvrđena su 28.394 potpuna para i svih 479 očekivanih 5s barova, bez gapova/dropova. Izvorne postavke su vraćene, nema naloga. Slijedi pregled blokirajućeg spremanja quality stanja; trajna promjena napajanja nije opravdana ovim rezultatom.

**N3 stabilna sesija, 20.11.2026.:** [analiza i SQL dopuna](N3_COLLECTION_LATENCY.md#stabilna-večernja-sesija-i-dopuna-sql-mjerenja--20112026): 77.772 potpuna EUR/USD para, svih 1.259 finalnih 5s barova i 1.234 zagrijana framea. Nema gapova/dropova, ali 17 minuta ima lokalni pipeline maksimum >250 ms (najviše 1,883 s). Novo ReaderLifetime mjerenje stvarno je uhvatilo 1,553 s u dovršavanju UPDATE-a. Dublji I/O uzrok nije još dokazan; **N3 ostaje otvoren**. 781/781 testova i IBKR publish guard prolaze. Nema naloga ni model approvala.

**N3 checkpoint, 20.11.2026.:** [fazna mjerenja i stanje provjere](N3_COLLECTION_LATENCY.md) uvedeni su uz ispravak vremenskog flush-a i urednog završetka zapisa pri stopu. Nova mjerenja potvrđuju da gotovo dvije sekunde mogu nastati u SQLite spremanju quality stanja; dublji uzrok nije još dokazan. 780/780 .NET testova, 2 testa izvještaja i nepromijenjen N2 replay rezultat; stvarna prihvatna sesija i uklanjanje outliera još nedostaju. **N3 ostaje otvoren**, N4 nije započet; nema naloga ni model approvala.

**N1, 20.11.2026.:** [ponovljena analiza istih dnevnih podataka](N1_QUOTE_COVERAGE.md) sada razdvaja neopaženi razmak od eksplicitno zabilježenog prekida. Četiri nepotpuna izlaza ostaju nepoznata, a fillani rezultati nepromijenjeni. Nisu mijenjani kolektor, vremenske tolerancije ili stari dokazi. Sljedeći odjeljak ostaje izvorni dnevni checkpoint.

## Analiza dnevne sesije — 20.11.2026.

Analiziran je cijeli dogovoreni raspon **10–12 sati Zagreb / 09–11 UTC**, kroz unaprijed određena četiri nepreklapajuća polusatna uzorka. Sve izvedene snimke imaju isti izvorni manifest SHA i provjerene file hashove. Ukupno **223.886 događaja / 111.943 potpuna bid/ask para**, bez nepotpunih parova ili prekoračenja future tolerancije; 308 događaja ima tolerirano negativno receive-minus-event odstupanje. Samo dva događaja, odnosno **jedan par**, starija su od dvije sekunde. [Strukturirani rezultat](evidence/fx12-day-session-analysis-2026-11-20.json).

| Raspon Zagreb | Quote parovi | Finalni 5s barovi / očekivani | 5s frameovi s najmanje 26 barova |
| --- | ---: | ---: | ---: |
| 10:00–10:30 | 30.525 | 357 / 359 | 307 |
| 10:30–11:00 | 28.484 | 359 / 359 | 334 |
| 11:00–11:30 | 32.345 | 359 / 359 | 334 |
| 11:30–12:00 | 20.589 | 358 / 359 | 308 |

Ukupno **1.433/1.436** očekivanih završenih 5s prozora i **1.283 zagrijana framea** u zasebnim rekonstrukcijama; tri prozora nemaju quote. Svaki batch ima vlastiti cold start i završni as-of cutoff, pa ovo nije potpuna rekonstrukcija neprekinutog dvosatnog feature stanja. Rubni bar svakog batcha još nije finaliziran do njegova cut-offa. Za 15s dobiveno je 476/476 finalnih barova i 351 zagrijani frame; to ne mijenja aktivni 5s profil. Kontinuitet je znatno bolji od noćnog uzorka, ali ostaju stvarni prekid i kraće quote praznine.

### Prekid i kašnjenja

TWS je prijavio code 1100 u **10:13:20**, a 1102 u **10:13:36,9**. Kolektor ima trajni coverage gap do 10:13:37,27. Prvi stari par nakon obnove ima event 09:13:36 UTC, receipt 09:13:38,094 UTC, odnosno **2.094,12 ms**; njegov queue/pipeline iznose samo 0,063/2,773 ms. To povezuje ovu staru kotaciju s vremenom obnove, ne dokazuje uzrok svih ranijih i kasnijih zastoja. Minutni backfill ne popravlja izgubljenu tick pokrivenost.

U 119 dovršenih latency prozora završetkom unutar sesije zabilježeno je 223.786 događaja, nula processing grešaka i negativnih queue delti. Ponderirani prosjek pipelinea je **2,29 ms**, queuea **57,97 ms**. Maksimumi su **1.880,20 / 1.901,04 ms**, u prozoru koji završava 10:58:22 Zagreb, izvan TWS prekida. SDK queue-drain batch maksimum je 7,52 ms, bez batcha >250 ms. Izmjerena su 11 minutnih prozora s pipeline maksimumom >250 ms. Postojeći logovi ne razdvajaju quality SQLite upit/upis, dataset enqueue i primjenu agregata; konkretan uzrok ovih lokalnih vrhova još nije dokazan. Granice log prozora nisu iste kao audit događaja. Nije mijenjana tolerancija, freshness ni runtime.

### Offline replay i ograničenja rezultata

Oba scenarija koriste **identične konfiguracijske bajtove pripremljene prije sesije**, nove odvojene baze s 23 migracije i postojeća važeća povijesna pravila. Svih osam replaya završava. To je deterministički pattern baseline, ne odobreni ML model. Nije bilo brokerskog pristupa kroz replay niti stvarnog naloga.

| Scenarij | Prihvaćeni istraživački signali | Simulirani ulazi | Potpuno zatvoreni | Neto samo zatvorenih |
| --- | ---: | ---: | ---: | ---: |
| 100 USD | 90 | 0 | 0 | 0 USD; nema izvedivog tradea |
| 50.000 virtualnih USD | 90 | 25 | 21 | −133,03275 USD |

Kod 100 USD 89 prihvaćenih signala odbija risk zbog kapitala/troškova/standardnog minimuma količine, a jedan nema svježu decision kotaciju. To ne dokazuje mogućnost trgovanja sa 100 USD; odd-lot izvršenje nije izmišljeno.

Kod većeg tehničkog scenarija **svih 21 zatvorenih tradeova izlazi timerom i ima negativan neto rezultat**. Zbroj gross PnL-a (već sa spreadom/slippageom) je −26 USD, provizije 84 USD, a dodatni hipotetski safety buffer 23,03275 USD. To nije preporuka većeg kapitala. Četiri dodatne simulirane pozicije imaju ulaz, ali nedokazan izlaz; njihov PnL nije uključen u taj zbroj. Zbroj rezultata četiri batcha nije dvosatni portfelj: kapital i stanje pozicija ponovno počinju u svakom batchu, a nepotpuna otvorena pozicija konzervativno blokira preostale ulaze do njegova kraja.

Posebna read-only rekonstrukcija potvrđuje da su četiri otvorene simulacije prekinute razmacima između quoteova od **3,10 / 4,41 / 3,60 / 3,64 s**, preko postojećeg maksimuma od 3 s, bez promjene callback sesije. To nisu rubovi polusatnih batchova ni isti TWS prekid. Odsutnost nove kotacije nije sama po sebi dokaz mrežnog gubitka; za pouzdaniji replay treba razdvojiti tiho tržište od nepoznate pokrivenosti uz dokaz i eksplicitnu politiku, ne povećati prag da bi test prošao.

Dobiveno je i **131 potpuno zatvorena counterfactual labela** te 219 nepotpunih u većem scenariju: 170 short/nepodržano izvršenje, 36 coverage, šest bez svježeg decision quotea i sedam bez filla/zatvaranja do kraja. Labele uključuju blokirane i preklapajuće kandidate, pa nisu nezavisni portfeljni tradeovi. U scenariju 100 USD svih 350 labela je nepotpuno. Ponovljeni 11:00–11:30 Zagreb replay daje isti input i output SHA (`310e78200c58006df65ec07ed08e4c0f13d35d24e209c73fb8a1852fb69585c0`); ponavljanje nije dodatni uzorak.

### Što sada blokira FX-12

Stvarni poziv `evaluate` nad replayem 11:00–11:30 Zagreb odbijen je porukom **“Evaluation range is too short for the configured training, validation and untouched holdout windows.”** Ostaju 2 dana treninga, 1 validacije i 1 netaknuti holdout; minimumi nisu spušteni. Model nije treniran ili odobren. Operativna baza i obje istraživačke baze imaju nula naloga i modela.

Analiza je konkretizirala i **ograničenje istraživačkog toka**: cijela sesija već ima više od 200.000 događaja, a trenutna FX evaluacija prihvaća jedan eksplicitni dovršeni `ReplayRunId` i provjerava njegov output hash. Četiri odvojena replaya ne spaja u višednevni trening. Prije takve evaluacije treba inkrementalna/veća obrada s očuvanim stanjem ili verzioniran skup kompatibilnih replay segmenata s jasnim granicama, deduplikacijom i vremenskim razdvajanjem. Samo dodatni dani prikupljanja uz sadašnji ograničeni batch nisu potpuno rješenje.

Slijed nastavka: razraditi i testirati quote coverage pravila i dijagnostiku nepotpunih izlaza, omogućiti provjerljiv višesegmentni istraživački tok, izmjeriti izvore lokalnih pipeline vrhova, zatim prikupiti reprezentativne višednevne sesije s pravodobnim rule snapshotovima i provesti evaluaciju. Tek zadovoljavajući rezultat vodi do model shadowa i zasebnog FX-12 paper order testa. Loš baseline ne podešavati na završnom holdoutu. Post-FX12 strategije i UI ostaju odgođeni.

Artefakti su lokalno u `TestResults/fx12-day-20261120`: četiri snimke/audita, osam replay rezultata i jedno ponavljanje, zasebne baze/configi, latency/gap analiza i dokaz odbijene evaluacije. Analitički pomoćni program samo je čitao snimke; produkcijski kod, profil, baza i proces kolektora nisu mijenjani. Nije ponavljana puna regresija za dokumentacijski checkpoint; provjereni su manifest/file hashovi, stvarni CLI replay/evaluation guard, ponovljivost rezultata i JSON dokazi. **FX-12 ostaje otvoren; današnji test potvrđuje prikupljanje i dio offline obrade, ne profitabilnost ni stvarno izvršenje.**

## Početak dnevne sesije — 20.11.2026.

Planirani raspon je **10–12 sati Europe/Zagreb (09–11 UTC)**. Postojeći kolektor PID 25364 nastavio je rad bez restarta: stvarni IBKR adapter 10.49.2.0, paper TWS 7497, Connected, AnalysisOnly. Command-line zaštite potvrđuju read-only, isključene stock/live/FX paper naloge i automatske FX signale. Read-only provjera operativne baze u 09:08:54 UTC nalazi nula naloga, izvršenja, tradeova, rezervacija i modela. [Početni strukturirani dokaz](evidence/fx12-day-session-start-2026-11-20.json).

Izdvojen i provjeren **prvi uzorak 09:00–09:05 UTC**, 26 Parquet datoteka / 12.995 manifest redaka; od toga 12.846 događaja unutar raspona, odnosno **6.423 potpuna bid/ask para**. Nema nepotpunih parova, starosti >2 s ni prekoračenja future tolerancije. Postoje 32 događaja (16 parova) s brokerovim vremenom do 66,963 ms ispred primitka, unutar postojeće granice 250 ms; izvorna vremena nisu mijenjana. Medijan receive-minus-event je 747,5 ms, p95 1.285,6 ms, uz sekundnu rezoluciju brokerskog vremena.

Offline rekonstrukcija prvih pet minuta daje **59/59 završenih 5s barova**, bez praznih intervala, i **34 framea s najmanje 26 barova**. Za 15s daje 19/19 barova; pet minuta nije dovoljno za 26 takvih barova. Rubni nezavršeni prozori ne broje se. Živi kolektor ima i raniji warmup: u 09:07:02 UTC sažetku ima 133 uzastopna 5s bara. To nije ista početna povijest kao u izdvojenom auditu niti potpuni live/replay dokaz.

Pet dovršenih latency log prozora s krajem između 09:00 i 09:05 UTC broji 12.704 događaja, bez processing grešaka i stale događaja. Ponderirani prosjek obrade je 1,68 ms, internog čekanja 46,39 ms; maksimumi su **1.286,47 / 1.332,50 ms**. Ovaj izolirani zastoj ostaje za daljnju korelaciju; nije dokaz uklanjanja svih problema kašnjenja. Granice log prozora razlikuju se od quote audita. Windows stripchart u 10:07 lokalno mjeri zaostajanje 111,5–114,1 ms prema referenci; to nije mjerenje brokerskog sata niti dugoročne stabilnosti.

Kolektor ostaje aktivan bez naloga. Za nastavak ostaviti računalo budnim i paper TWS prijavljen barem do 12 sati. Nije instaliran dodatni nadzor ili automatska završna analiza. Nakon sesije napraviti stabilne snimke i audit, pregled kašnjenja/praznina te izolirani replay s pripremljenim scenarijima. Zbog dnevnog prometa i limita od 200.000 događaja unaprijed koristiti kronološke 30-minutne raspone (po potrebi manje fiksne podjele), bez biranja prema profitu; odvojeni replayi ne prenose otvorene pozicije preko granice. Lokalni početni artefakti su u `TestResults/fx12-day-20261120`. Ova provjera ne završava FX-12 niti opravdava trening/odobrenje modela na pet minuta podataka. Promijenjeni su samo dokumentacija i dokaz; aplikacija i konfiguracija ostaju iste.

## Provjera nakon pauze — 19.11.2026.

Runtime `2a73e9a` nastavio je prikupljanje nakon 22:15 UTC / 23:15 Zagreb, bez restarta ili promjene postavki. TWS je Connected, host AnalysisOnly, adapter read-only. Nema lokalnih naloga, izvršenja, tradeova, rezervacija ni modela. [Strukturirani dokaz](evidence/fx12-postpause-2026-11-19.json) čuva odvojene granice svakog mjerenja.

### Učinak optimizacije i preostala kašnjenja

Stabilna kopija uzorka **22:15–22:20 UTC** sadrži 28 provjerenih Parquet datoteka, 238 događaja i **119 potpunih bid/ask parova**. Svi su Healthy, bez nepotpunih parova, budućih vremena ili starosti >2 s. Medijan receive–event je **973,7 ms**, p95 **1.471,7 ms**, maksimum **1.716,6 ms**. Brokerova vremena imaju rezoluciju sekunde; to nije čista mrežna latencija.

Pet dovršenih minutnih logova do 22:20 UTC pokriva 218 događaja: srednja obrada **4,49 ms**, čekanje u internom redu **12,81 ms**, maksimumi **106,30 / 130,94 ms**. Sredine su ponderirane brojem događaja. Nema processing iznimke, negativne queue razlike ni SDK dispatch batcha >250 ms. Log se ispisuje tijekom obrade, može razdvojiti dvije strane para i ne uključuje još neispisan kraj minute; zato njegov broj nije jednak Parquet uzorku. SDK dispatch mjeri cijelo pražnjenje reda poruka, ne pojedini mrežni paket.

Aktivni proces potrošio je **0,375 CPU sekundi u 30,05 s**, oko 1,25% jedne jezgre. Ranije je potrošio 2,125 CPU sekundi u 2,14 s uz potvrđeni skupi SQL cooldown upit. To je jasan napredak lokalnog opterećenja, ali nije kontrolirani benchmark jednakog quote prometa. Novi uzorak ima oko 24 para/min, znatno manje nego prije pauze. Raniji desetominutni uzorak već je imao nula stale događaja prije SQL popravka; ne tvrdimo da je popravak uklonio uzrok povremenog starog 6,37s bursta. Za to treba ponovna pojava uz istodobne queue/pipeline/dispatch metrike ili dulji reprezentativni uzorak. Windows stripchart u 22:19 UTC izmjerio je zaostatak od 44,6–47,8 ms.

### Kontinuitet i usporedba patterna

Od 59 mogućih finalnih 5s prozora nastala su **32 bara**, a **27 prozora nema kotacije**. Najdulji niz ima sedam barova, bez framea s 26. Za 15s nastalo je 14 od 19 prozora, najdulji niz osam; profil nije prebačen na 15s. Nijedan opaženi prozor nije izgubljen zbog odbijanja vremena. Pet minuta ionako nije dovoljno za 26 × 15s barova.

Od 22:16:17 do 22:17:17 UTC nije bilo novih callbackova. Postojeći 60s heartbeat timeout obnovio je pretplatu nakon 15s odmaka, uz reset agregata i trajni zapis nepoznate pokrivenosti do 22:17:32. Nema prijavljenog queue overflowa/dropped događaja. Bez dodatnog brokerskog dokaza ne možemo razlikovati odsutnost promjene kotacije od upstream prekida. Taj prekid je odvojen od starosti primljene kotacije. Praznine se ne popunjavaju fiktivnim barovima, a heartbeat i 2s freshness pravila nisu ublaženi.

Read-only rekonstrukcija proširena je na deset spremljenih polja: reference price, confidence, feature verziju, režim, ATR/price, hard conditions, score components, spread, vrijeme uparene kotacije i prisutnost pattern kandidata. **1.120/1.120 ranijih zapisa (112 barova, 21:41:48–21:52 UTC)** i **320/320 novih zapisa (32 bara)** podudara se bez nedostajućih live redaka. To potvrđuje navedene vrijednosti i pattern izračune u ovim uzorcima, ne sve featuree, vremenske gateove, izvršne labele, model ili order lifecycle.

### Ponovljivost i daljnji FX-12 rad

Lokalna stabilna kopija je u `TestResults/fx12-postpause-snapshot-20261119` radnog worktreea. Izvorni manifest sačuvan je kao `_source-manifest.json`; izdvojene su samo EUR/USD bid/ask datoteke koje preklapaju raspon, uz nepromijenjene file hashove. Izvedeni manifest ima zaseban SHA-256 i `snapshot-provenance.json`. To je provjeren podskup, ne nova provjera cijelog aktivnog dataseta. Raw podaci i dijagnostički pomoćni alati ostaju lokalni, izvan Gita; dokaz sadrži njihove SHA-256 otiske.

Ponoviti službeni audit iz korijena istog checkouta, s novim izlaznim imenom:

```powershell
dotnet TradingBot.ModelOps/bin/Release/net10.0/TradingBot.ModelOps.dll fx-collection-audit --config "$PWD/TradingBot.Web/appsettings.ForexCollection.json" --dataset "$PWD/TestResults/fx12-postpause-snapshot-20261119" --instrument-id FX-CASH-EUR-USD-IDEALPRO --from 2026-11-19T22:15:00Z --to 2026-11-19T22:20:00Z --output "$PWD/TestResults/fx12-postpause-audit-repeat.json"
```

Službeni audit stabilne kopije prolazi. Preflight u **22:21 UTC** prolazi sesiju, izolaciju/no-live, instrument, bazu i lokalni kapacitet; i dalje nedostaju research ugovor, pregledana izvršna pravila, evaluirani/odobreni model i model shadow/PaperReady. Ne stvarati odobrenje samo da bi test poslao nalog. Slijedi **60–120 minuta aktivnije dnevne sesije**, zatim provjera kontinuiteta i reprezentativnosti prije FX-10 replaya/evaluacije. Taj vremenski uzorak nije jamstvo dovoljnog trening skupa. Stari 92-minutni historical gap ostaje otvoren i minute ne popravljaju tick praznine.

Ova dopuna mijenja samo dokumentaciju i dokaze. Runtime ima prethodnih **726/726 testova** i [uspješan CI](https://github.com/dejanakadex/aiTradingBot/actions/runs/44625350139). Nije bilo potrebe ponovno pokretati kolektor ili mijenjati strategiju. FX-12 ostaje otvoren.

## Istraga kašnjenja i nastavak FX-12 — 19.11.2026.

Provjera aktivnog Parquet manifesta završila je bez grešaka: **4.481 datoteka / 1.052.479 redaka**, hash je u [dokazu](evidence/fx12-latency-2026-11-19.json). To je provjera integriteta snimke, ne tvrdnja da su svi redci pogodni za trgovanje ili da postoji neprekidna tick pokrivenost.

Raspon **21:41:48–21:52:00 UTC** sadrži 3.798 događaja (1.899 uparenih kotacija), bez budućih/stale događaja. Ima 112 cijelih finalnih 5s prozora i 22 zagrijana framea; svih 112 krajeva prozora podudara se s live zapisima. Jedini dodatni rekonstruirani bar počinje prije početka raspona pa je izuzet kao parcijalan. To ne potvrđuje sve feature vrijednosti, model niti order/fill paritet. Prazni intervali ostaju i 15s profil nije automatski aktiviran.

Raniji raspon **21:32:48–21:41:14 UTC** ima 3.085 quote parova, od kojih 48 prelazi dvije sekunde; maksimum je **6.366,409 ms**. Više događaja s brokerskim vremenima 21:36:37 i 21:36:38 primljeno je u nizu oko 21:36:43.36. To je dokaz nakupljanja prije callbacka, ali ne određuje gdje: TWS/feed, mreža, SDK queue i OS raspoređivanje još nisu razdvojeni. `ReceivedTimeUtc` nastaje u `TickSubscription.PublishQuote` prije kanala/SQLitea/Parqueta. Naknadno pisanje baze zato ne može retroaktivno povećati tu razliku, iako lokalno opterećenje može posredno utjecati na pravovremenost callbacka. Brokerov timestamp ima rezoluciju sekunde; receive-minus-event nije čista mrežna latencija. Nekoliko desetaka ms odstupanja Windows sata ne objašnjava šest sekundi.

**Potvrđen lokalni problem:** kolektor je u kratkom mjerenju trošio približno jednu CPU jezgru. Tri uzastopne snimke najaktivnije niti pokazale su SQLite upit trajnog FX cooldowna. Upit se izvodio i u sekundnim ciklusima bez novog bara; plan je birao indeks s `Direction` i privremeno sortiranje. Na stvarnoj bazi upit je trajao oko **503,9 ms**. Dodana je migracija s pokrivajućim `IX_ResearchCandidate_FxCooldown` indeksom te rani izlaz kada isti profil već ima obrađen najnoviji bar. Novi servis i dalje učitava trajni watermark, a novi bar i dalje čita cooldown. Nakon deploya plan koristi pokrivajući indeks bez sortiranja; isti read-only upit izmjeren je na **0,036 ms**. Jedno mjerenje nije benchmark jamstvo. Ova optimizacija uklanja dokazano opterećenje; nije dokaz da je time riješen svaki brokerski zastoj.

**Nova lokalna mjerenja:** `Tick latency` jednom u minuti bilježi broj događaja, broj timestamp razlika preko dvije sekunde, prosjek/maksimum receive-minus-event, dequeue-minus-receipt i trajanje pipelinea. Za najstariju kotaciju čuva njezina UTC vremena i pripadajuće queue/pipeline trajanje, bez cijene/računa. Queue delta je zidni sat i negativne korekcije se izričito broje; pipeline traje prema monotonom `Stopwatch`. `IBKR dispatch` mjeri cijeli SDK queue-drain batch, bez čekanja signala: broj batcheva, broj preko 250 ms te prosjek/maksimum. To nije per-message wire vrijeme, a velik batch sam po sebi nije dokaz zastoja jedne poruke. Dodatni logovi imaju konstantnu memoriju i ne zapisuju svaki tick. Stale prag, clock tolerancija, strategije, model odobrenja i order zaštite nisu promijenjeni.

Read-only FX-12 preflight ponovno potvrđuje konfiguraciju, bazu i odsutnost lokalnih rezervacija, ali ostaje blokiran nedostatkom istraživačkog ugovora, pregledanih size pravila, kompatibilnog odobrenog modela i aktualnih PaperReady/model-shadow dokaza. Povijesna MIDPOINT praznina od 92 minute i nedostajući tickovi preko prekida nisu proglašeni popravljenima. Slijede dulji dnevni uzorak i korelacija novih latency logova pri ponovljenom zastoju, potom eksplicitni FX-10 research/rule/cost ugovor, replay/evaluacija i stvarni model shadow. Ne izmišljati broker pravila ili odobravati model radi prolaza testa.

Lokalno prolazi **726/726 testova**, uključujući razdvajanje faza kašnjenja, granicu od dvije sekunde, korekcije sata, migraciju uz očuvanje dokaza, stvarni SQLite query plan te idle/restart/new-bar cooldown ponašanje. Obvezni IBKR publish/guard prolazi. Baza je konzistentno kopirana prije deploya; proces je ponovno pokrenut u 22:03:26 UTC s `ReadOnlyApi=true`, `AnalysisOnly`, bez naloga. Ovaj restart dogodio se unutar konfigurirane dnevne FX pauze 22:00–22:15 UTC; svježu aktivnost treba zasebno potvrditi nakon nje. FX-12 ostaje otvoren.

## Vremenska tolerancija — 19.11.2026.

Windows Time je nakon korisnikove promjene aktivan, s `time.windows.com,0x8`; zadnja potvrđena sinkronizacija u početnoj provjeri je 22:24:32 Zagreb. Mjerenja u 22:29 pokazuju zaostajanje 29,8–35,3 ms, a u 22:37 jedan uspješan uzorak 38,4 ms uz dva timeouta. To nije dokaz dugoročno stabilnog sata. W32Time treba ostati uključen; tolerancija u botu nije zamjena za sinkronizaciju.

`ShortAggregates:ForexFutureToleranceMilliseconds` ima zadanu vrijednost **0**, dopušten raspon **0–500 ms**, a samo profil `ForexCollection` eksplicitno uključuje **250 ms**. Početni prag pokriva izmjerenih približno 35 ms i ranije spremljenih 135 ms, uz ograničenu rezervu; nije statistički dokaz točnosti brokerskog sata. Jednaka granica vrijedi za FX bid/ask canonical quality, midpoint agregator i čitač izvršnog replaya. Stock i MIDPOINT bar quality zadržavaju postojeća pravila. Prekoračenje granice u živom quality gateu ostavlja incident i postojeći reconnect/coverage-gap postupak; ne mora stići do brojača agregatora.

Izvorni `EventTimeUtc` i `ReceivedTimeUtc` u Parquetu se ne mijenjaju. Agregati zadržavaju event-time prozore i receive-time finalizaciju; tolerancija ne finalizira budući prozor ranije. Izvršni simulator koristi konzervativnu dostupnost `max(EventTimeUtc, ReceivedTimeUtc)`, uz provjeru izvornog receive redoslijeda, bez djelovanja prije oba vremena. Granica starosti od dvije sekunde, zaštite izvršavanja, nepotpuni/crossed parovi i zabrana izmišljanja praznih barova ostaju. Nema automatskog pomicanja sata niti njegovih timestampova u botu.

Tolerancija ulazi u aggregate/feature/label fingerprint. Nulta vrijednost čuva stari aggregate fingerprint; nenulta vrijednost traži novu evaluaciju i odgovarajuća odobrenja modela. NLog minutni sažetak dodaje `ToleratedFutureEventsDelta`, konfiguriranu granicu i `MaximumFutureLeadMilliseconds` (maksimum od pokretanja procesa). Tolerirani događaji također daju WARN kako drift ne bi postao nevidljiv. U offline auditu `FutureQuoteEvents` i dalje broji sve sirove negativne razlike, `BeyondFutureToleranceQuoteEvents` broji prekoračenja, a `MissingBarsWithFutureQuotes` odnosi se na prekoračenja primijenjene granice.

Ponovna obrada **istih 21.192 događaja od 25.10. 21:15–22:45:02 UTC**, bez izmjene ulaza: svih 154 događaja s negativnom razlikom stane u 250 ms. Broj 5s barova raste **780 → 796**, a frameova s najmanje 26 barova **3 → 33**. Za 15s vrijednosti su **316 → 331** i **27 → 155**. Dva zastarjela događaja i prazni intervali ostaju problem. Ovo je offline usporedba politike, ne dokaz jednakosti rasporeda živih radnika i replaya.

Stvarni novi uzorak **19.11. 21:32:48–21:37:30 UTC** prije deploya ima 4.178 događaja, nula budućih i **96 starijih od dvije sekunde**. Obje politike zato daju istih 50 finalnih 5s barova i nula zagrijanih frameova u tom kratkom rasponu. Razlika event/receipt uključuje brokerovu sekundnu rezoluciju i kašnjenje do callbacka; nije čista mrežna latencija. Nije utvrđeno nastaje li dodatno kašnjenje u brokeru, mreži ili lokalnoj obradi. Ne povećavati stale prag samo radi prolaska warmupa.

FX-12 ostaje otvoren: potreban je dulji dnevni uzorak, analiza zastarjelih kotacija i live/replay pariteta, zatim model/evaluacija/shadow i odvojeno odobreni paper test. Nalozi ostaju blokirani.

Deploy je potvrđen stvarnim TWS feedom: novi proces pokrenut je u 21:41:29 UTC, a prvi minutni sažetak u 21:42:47 UTC prikazuje `Connected`, granicu 250 ms, nula future događaja i dva stale događaja. SQLite backup napravljen je prije zamjene procesa; restart ima eksplicitnu coverage prazninu. Nema naloga, izvršenja, tradeova, rezervacija ni modela. [Strukturirani dokaz i oba offline izvještaja](fx-clock-check-2026-11-19.json). Lokalno prolazi **721/721 testova** i obvezni IBKR publish/guard.

25.10.2026.: pripremljen je i pokrenut zaseban **ForexCollection** profil postojećeg web hosta. Prikupljanje ne zahtijeva model, izvršne limite ni dopuštenje trgovanja. To nije završen FX-11 model shadow ili FX-12 paper trade test.

## Konfiguracija i lokacije

[appsettings.ForexCollection.json](../TradingBot.Web/appsettings.ForexCollection.json) učitava se preko standardnog ASP.NET environment profila, nakon osnovnog `appsettings.json`. Koristi cijeli niz instrumenata: sačuvani SPY s privremeno isključenim prikupljanjem u ovom profilu te aktivni EUR/USD `CASH/IDEALPRO`, strategiju `eurusd-patterns` i minutnu povijest. Osnovni stock profil i njegova konfiguracija nisu promijenjeni.

- `TradingSettings.Enabled=false`, `AnalysisOnly`, `LiveTradingExplicitlyEnabled=false`, `ForexPaperExecutionEnabled=false` i `AutomaticPaperSignalsEnabled=false`.
- `IbkrSettings.ReadOnlyApi=true` prenosi se u postojeću adapterovu blokadu **submit/modify/cancel** za sve instrumente. To je procesna postavka koja se mijenja restartom; Read-Only API može ostati uključen i u TWS-u. Za kasniji paper test potreban je drugi pregledani izvršni profil, ne uklanjanje blokade u kolektoru.
- EUR/USD `BidAsk` tickovi te MIDPOINT barovi; povijesni lookback je 3 dana u segmentima od jednog dana, s postojećim pacingom od najmanje 11 sekundi. Kratki agregati su 5s/15s, uz postojeća pravila kašnjenja, uparivanja i kontinuiteta.
- Pattern/signal profili pripremljeni su za audit. Odobrenja signalnih verzija ostaju prazna; istraživački i izvršni profili nisu izmišljeni. Uspjela geometrija može biti zapisana kao blokirani kandidat; nema model shadow odobrenja ili naloga.
- SQLite je `C:\PrivateGit\aiTradingBot\data\eurusd-collection\trading.db`, Parquet i manifest su u susjednoj mapi `datasets`, aplikacijski paket u `app`, a privatni logovi u `logs`. Cijela runtime mapa je isključena iz Gita. Baza i podaci ostaju nakon restarta i ne šalju se na GitHub.

## Pokretanje i provjera

### Lokalni logovi i pristup bazi

NLog zapisuje u `data/eurusd-collection/logs/trading-YYYY-MM-DD.log` sve botove INFO/WARN/ERROR/FATAL poruke, a u `error-YYYY-MM-DD.log` izdvojene ERROR/FATAL poruke i iznimke. Vrijeme na početku retka lokalno je vrijeme računala; novi FX sažetak dodatno ima eksplicitan `AsOfUtc`. Datoteke se razdvajaju po danu; automatsko brisanje/ograničenje veličine nije uvedeno i stari logovi ostaju sačuvani. `nlog.config` u objavljenoj `app` mapi podržava automatsko ponovno učitavanje.

Dopuna 26.10.2026. filtrira samo `Microsoft.EntityFrameworkCore.Database.Command` na razinama Info i nižima. SQL WARN/ERROR ostaju u postojećim targetima, kao i sve botove INFO poruke. Prije promjene SQL je činio više od 99% poruka; glavni dnevni log narastao je do približno 1,49 GB. Smanjenje logiranja ne mijenja izvršavanje SQL-a, zapis podataka ni poslovne provjere.

Novi `ForexDiagnosticsHostedService` jednom u minuti zapisuje jedan `FX collection summary` po uključenom FX instrumentu, neovisno o trading/signal odobrenju. Čita postojeće stanje bez pokretanja/finalizacije barova, dodatnih SQL upita ili broker zahtjeva. Sažetak sadrži vezu/sesiju, promjene broja budućih, prestarih i ostalih odbačenih događaja, zakašnjele/nepotpune parove, broj resetiranja te za svaki interval:

- `CompletedDelta` i `SuppressedDelta`: novodovršeni i potisnuti otvoreni prozori od prethodnog sažetka. Potpuno prazni intervali ili intervali sa samo odbačenim quoteovima nisu otvoreni prozori i ne ulaze u `SuppressedDelta`; puni coverage audit ostaje `fx-collection-audit`.
- `ConsecutiveBars`: trenutačni neprekinuti niz sačuvanih finalnih barova, ograničen `RetentionLimit`; to nije odobrenje signala/modela. `LastBarEndUtc`, `LastBarAgeSeconds` i `NoRecentBar` otkrivaju zastoj čak i kada povijest ima dug niz.
- `EmptyIntervalsBeforeLastBar`: praznina neposredno prije posljednjeg bara, a ne ukupan broj praznina u minuti. Odbijanja se broje kao pojedinačni bid/ask događaji, pa isti nevaljani callback tipično doprinosi dva događaja.

Sažetak je WARN ako su nastala odbijanja, zakašnjenja, nepotpuni parovi ili potisnuti prozori. Tijekom otvorene FX sesije WARN vrijedi i za prekinutu vezu ili nedostajući/bar stariji od 60 sekundi; inače je INFO. Prag 60 sekundi služi samo log dijagnostici i ne mijenja strategijske/risk pragove. Brojači traju do restarta procesa i preživljavaju reset agregata; prvi sažetak obuhvaća stanje od pokretanja. Sažetak ne zapisuje cijene, account ID, ključeve ili cijele tickove. Greška dijagnostike ne zaustavlja kolektor.

Lokalnu SQLite bazu moguće je čitati u `ReadOnly` načinu dok kolektor radi. Za analizu koristiti transakcijski konzistentne upite; za zaseban istraživački rad SQLite backup API umjesto kopiranja samo `.db` uz aktivan WAL. Ne mijenjati operativne naloge, odobrenja, modele ili registry radi prolaska testa. Logovi, baza i raw tickovi ostaju lokalni i gitignored.

Provjera: četiri nova testa stvarnim NLog file targetima potvrđuju uklanjanje SQL INFO uz očuvanje bot INFO/SQL WARN/ERROR, a stvarnim agregatorom provjeravaju razloge odbijanja, snapshot/delta brojače preko reseta, kontinuitet i starenje bez mutacije barova. Puna regresija ima 707 prolaznih testova. Time se ne zatvara FX-12 niti problem trajne sinkronizacije Windows sata.

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
