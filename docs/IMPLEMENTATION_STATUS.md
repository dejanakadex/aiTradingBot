# Implementation status

Ovaj dokument je checkpoint za nastavak rada na branchu `trading-bot-v2`. Svaka točka mora završiti testovima, zelenim CI-jem i jasnim izvještajem prije prelaska na sljedeću.

## Trenutno stanje

**FX-12 dijagnostika 26.10.2026.: smanjeni SQL logovi i dodani minutni FX sažeci.** NLog filtrira rutinske EF Command INFO poruke uz očuvanje SQL WARN/ERROR i bot INFO. Neovisni read-only worker po FX instrumentu bilježi razloge odbijanja, delta brojače, resetiranja, finalne/potisnute otvorene prozore te kontinuitet i starost barova. Ne mijenja signal, kalendar, vremenske tolerancije, model/risk odobrenja ili naloge. Stari logovi i SQLite/Parquet ostaju sačuvani. Četiri nova testa; **707/707** lokalnih testova prolazi. [Upute i granice metrika](FOREX_COLLECTION.md#lokalni-logovi-i-pristup-bazi). FX-12 i trajna sinkronizacija sata ostaju otvoreni.

**FX-12 provjera 26.10.2026.: noćno prikupljanje nije uspjelo.** Kolektor radi, ali TWS je zatvorio socket 25.10. u 22:45 UTC (23:45 Zagreb), nakon čega 7497 odbija reconnect. U 08:09 UTC nema novih tickova. Spremljeno je 10.596 potpunih bid/ask parova i 1.514 barova; svih 623 Parquet datoteka prolazi hash provjeru. Na 1.080 završenih 5s intervala offline audit nalazi 283 bez kotacija i 17 odbačenih zbog vremena (16 s budućim vremenom, jedan s prestarom kotacijom). Živi audit: 778 barova, samo 3 s 26 uzastopnih barova, nula naloga. Windows Time servis ne radi; 77 parova ima brokerovo vrijeme do 135 ms ispred lokalnog primitka. Uzrok razlike satova još nije dokazan.

Dodan je read-only ModelOps `fx-collection-audit`, bez baze/brokera: provjera stabilnog manifesta, ponovljiva rekonstrukcija postojećim agregatorom/feature engineom, razdvajanje praznih i odbačenih prozora te usporedba 5s/15s. Ne mijenja input, sat, profil ili odobrenja. Offline 780 naspram live 778 barova ostaje eksplicitno ograničenje: završni se bar finalizira nakon prekida, a razlika za 22:01:15 UTC traži dodatnu live dijagnostiku. [Brojevi, upute i daljnji koraci](FOREX_COLLECTION.md#provjera-26-listopada-2026).

Provjera ove dopune: **703/703 testova**, uključujući sedam novih audit slučajeva; obvezni IBKR publish/guard prolazi. Regresija je otkrila stari testni race: worker i polling dijelili su isti SQLite connection; testni factory sada daje zasebne veze na istu imenovanu memorijsku bazu. Nije mijenjano izvršavanje bota. FX-12 ostaje otvoren; CI se provjerava nakon pusha.

**FX-12 operativna priprema, 25.10.2026.: EUR/USD kolektor pokrenut.** Zaseban `ForexCollection` profil i Windows launcher koriste postojeći host, stalnu SQLite/Parquet mapu te `ReadOnlyApi` blokadu svih broker order mutacija. SPY je sačuvan, privremeno neaktivan samo u ovom profilu. Stvarna TWS veza na 7497 i aktivne 1m/BidAsk pretplate potvrđene su; sesija je zatvorena. Pohranjeno je 1.425 MIDPOINT barova u bazu i četiri provjerene Parquet datoteke. Jedna praznina od 92 minute ostaje eksplicitno označena. Nema naloga, izvršenja, rezervacija ili odobrenog modela. [Operativne upute i dokaz](FOREX_COLLECTION.md).

Startup je otkrio nedostajuće rollout/post-trade tablice zbog starih neregistriranih migracija. Nova kompatibilna forward migracija čuva postojeće podatke; prije primjene spremljena je konzistentna kopija operativne baze. Test sada provjerava sve EF tablice/stupce, stvarno spojenu konfiguraciju, restart i adapterovu zabranu naloga. Puna regresija: **696/696 testova**. Restore/publish i obvezni IBKR guard prolaze. Stvarni FX-11 model shadow i FX-12 fillovi ostaju otvoreni; CI se provjerava nakon pusha.

CI infrastruktura: dva pokušaja commita `4e01489` zaustavljena su prije builda jer download automatski odabranog SDK-a 10.0.403/runtimea 10.0.14 vraća HTTP 409. CI je eksplicitno vezan na prethodno uspješni i dostupan SDK 10.0.402; postojeći `global.json` ga dopušta kroz `latestFeature`. Runtime kolektora i konfiguracija trgovanja nisu promijenjeni tom doradom.

**FX-12, 25.10.2026.: priprema implementirana; stvarni paper test blokiran preduvjetima.** Dodan je read-only ModelOps `fx-paper-preflight` za konfiguraciju, postojeću bazu, FX-11/model/replay dokaze i pregledane izvršne granice. Ne spaja se na brokera, ne migrira, ne odobrava i nikad ne daje dopuštenje naloga. Ispravljen je UTC binding datuma pregledanih broker pravila uz odbijanje datuma bez zone. Provjera stvarne projektne konfiguracije našla je zatvorenu sesiju, nedostatak aktivnog FX instrumenta/profila i operativne baze na provjerenoj zadanoj lokaciji. Odobren produkcijski model i stvarni model shadow nisu pripremljeni. [Protokol, dokazi i preostali redoslijed](FX12_PAPER_PROTOCOL.md). Nije slan nalog; FX-12 i 25/30/31 ostaju otvoreni.

Lokalna provjera: **692/692 testova**, uključujući 12 novih slučajeva; restore uspješan, Release build bez upozorenja/grešaka. Obvezni IBKR publish i guard za nedostajući/promijenjeni DLL prolaze. Stvarni CLI preflight nad projektnom konfiguracijom vraća neispunjene preduvjete i ne stvara bazu. Završna dorada obrade konfiguracije ponovno prolazi svih 12 preflight testova. GitHub CI provjerava se nakon pusha pripreme.

**FX-11, 25.10.2026.: programski dio implementiran, stvarni model shadow uzorak još nije potvrđen.** FX onboarding sada priznaje odgovarajuće MIDPOINT barove, izvršne labele iz provjerenog replaya, vremensku evaluaciju i točno odobren model/profil po paru i strategiji. AnalysisOnly model opažanja traju neovisno o trading zastavicama; uzorak, trajanje, latencija, starost i predloženi net edge zasebni su uvjeti za PaperReady, koji ne preskače FX-9 gateove. Novi `/instruments` i postojeći prikazi imaju FX/stock jedinice. Prikupljanje/backfill i restart ostaju zajednički; FX live approval se ne dopušta.

Provjera: **680/680 testova** (16 novih), Release bez upozorenja/grešaka, restore i obvezni IBKR publish/guard prolaze. Obuhvaćeni su operativne migracije/restart, mixed registry/backfill, shadow servisni tok bez rezervacija/naloga, integritet modela/replaya/evaluacije, readiness suspenzije, quote/gap/performance gateovi i Blazor render. Stvarna read-only TWS proba ponovno je spremila 60 povijesnih MIDPOINT barova; sesija je zatvorena, tickova/pattern odluka/naloga je 0. To nije potvrđen živi model shadow niti paper fill. GitHub CI provjerava se nakon pusha ovog commita. [FX-11 postupak, dokaz i otvorene stavke](FX11_ONBOARDING.md).

**FX-10, 25.10.2026.: implementirano i provjereno lokalnom simulacijom.** Dodani su zaseban quote-driven FX replay u postojećem servisu, izvršne SL/TP/timer/trailing labele, povijesni rule/risk proof, hashirani dataset/label/model ugovori, purged vremenska evaluacija te LightGBM net-win klasifikacija i gross-return regresija. Postojeći approval/rollback/cache workflow radi po paru/strategiji; dodatni, zadano isključen opt-in veže odobren model uz FX-9 pripremu i zajednički order kanal. ModelOps `fx-contract`/`fx-replay` provjeren je od JSON konfiguracije i migrirane SQLite baze do Parquet replaya. Popravljeni su binder duplikati konfiguriranih nizova, opcionalna IBKR metadata, normalizacija UTC datuma i zatvaranje CLI SQLite veze.

Lokalno: **664/664 testova**, 26 novih slučajeva prema FX-9; restore uspješan, Release build sa stvarnim obveznim IBKR adapterom bez upozorenja/grešaka. Provjereni su sintetski bid/ask fill/partial/SL/TP/timer/trailing, coverage/future-input odbijanja, deterministički hash, vremenski ML workflow i integritet, holdout izolacija, odobrenje/rollback, model → FX-9 rezervacija → fake nalog te oslobađanje rezervacije kod odbijanja reda. Publish i guard za nedostajući/promijenjeni DLL prolaze. GitHub CI treba pratiti za novi commit pri pushu.

Granice: bounded batch do 7 dana/200.000 događaja, hipotetska fill likvidnost, jedan par/strategija po backtestu, long USD izvršenje; nema stvarnog odobrenog EUR/USD modela, IBKR fill dokaza ni potvrde profitabilnosti. Ostaju FX-11 onboarding/UI/shadow, FX-12 stvarni paper i preostali produkcijski replay paritet točke 25. Točke 25/30/31 nisu zatvorene. [FX-10 detalji i operativni redoslijed](FX10_RESEARCH.md).

**Build politika od 25.10.2026.:** službeni IBKR API `10.49.02.0` pohranjen je u repozitoriju kao obvezna ovisnost. Stvarni adapter uvijek se kompilira i registrira; fallback build bez DLL-a više nije podržan. Raniji rezultati u dva build načina niže ostaju povijesni dokaz. Aktualne upute su u [README-u](../README.md#required-ibkr-dependency).

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
| 21 — Kratki agregati i indikatori | Implementirano i lokalno provjereno | Finalizirani 5s/15s UTC trade agregati (opcionalno 1s), dopušteno kašnjenje, as-of dostupnost i eksplicitne praznine; verzionirani `features-v3` s FacioQuo EMA/RSI adapterom. Lokalno 313/313 testova u realnom i fallback buildu, uključujući Parquet tick round-trip; stvarni IBKR paper tok još nije potvrđen. |
| 22 — Kratki signalni okidači | Implementirano i lokalno provjereno | Opt-in profili po instrumentu/strategiji nad finaliziranim kratkim barovima; izbor patterna, prag, cooldown, maksimalna starost i zaseban model contract. Kandidati idu u postojeći decision/risk/arbitration tok. Lokalno 323/323 testova u realnom i fallback buildu; stvarni IBKR paper signal/fill još nije potvrđen. |
| 23 — BUY → fill → fiksni SL/TP | Implementirano i lokalno provjereno | Bracket child zaštita, partial-fill usklađivanje i potvrđeni cancel/modify prijelazi; stvarni IBKR paper redoslijed još nije potvrđen. |
| 24 — Neovisni vremenski izlaz u sekundama | Implementirano i lokalno provjereno | Trajni rok od prvog filla, neovisni monitor i fail-closed restart/reconnect postupanje; stvarni IBKR paper test još nije potvrđen. |
| 25 — Replay cijelog trgovanja | U tijeku | Izolirani virtualni broker reproducira long fixed-bracket trade iz replay signala, tickova i troškovnih pretpostavki. Kratki tick-derived signali sada se stvaraju u izoliranom virtualnom satu, uključujući zatvaranje agregata timerom. Puna live model/strategy/risk/arbitration/order jednakost još nije izvedena. |
| 26 — Pokrivene labele i vremenska ML evaluacija | Implementirano i lokalno provjereno | `labels-v2` traži pokrivene trade tickove i računa SL/TP/timer neto ishod zasebno od prinosa na horizontu; `evaluation-v2` bira završni prag na vremenskoj validaciji, uz purge i netaknuti holdout. Labele, evaluacija i trening vežu se na hash ulaza/verzije. Lokalno 369/369 testova; stvarna IBKR paper potvrda nije provedena. |
| 27 — Operativno upravljanje modelima | Implementirano i lokalno provjereno | Zasebni `TradingBot.ModelOps` CLI radi evaluaciju, trening, usporedbu, odobrenje, odbijanje, rollback i audit bez pokretanja bota; inferencija koristi predmemorirani model i sigurne paralelne prediction engine instance. Lokalno 376/376 testova u realnom i fallback buildu; stvarni IBKR paper test nije proveden. |
| 28 — Onboarding novog instrumenta | Implementirano i lokalno provjereno | Registry, neovisna collection i backfill nastavljaju se nakon restarta; rollout vodi `Collecting` → `WarmingUp` → `ResearchReady` na temelju trajnih barova, gapova i labela. Shadow traži završenu kompatibilnu evaluaciju, paper još odobren model i shadow uzorak nakon ulaska u status. Lokalno 383/383 testova u službenom DLL i fallback buildu; stvarni IBKR paper test nije proveden. |
| 29 — Automatizirani end-to-end test | Implementirano i lokalno provjereno | Ponovljiv SQLite/fake-broker tok obuhvaća onboarding, backfill, feature/pattern, lokalni LightGBM model, strategiju, risk, arbitražu, BUY/fill, zaštitu i izlaz. Stop, target, timer, djelomični fill, odbijanje, prekid, restart i dva istodobna instrumentna signala imaju provjere naloga, pozicije i rezervacija. Lokalno 391/391 testova; stvarni IBKR paper test ostaje točka 31. |
| 30 — Shadow provjera i performanse | U tijeku | Read-only IBKR probe, pragovi, SQLite procjena odluka i lokalni multi-instrument load test implementirani. TWS API veza je potvrđena, ali SPY/QQQ tickovi nisu dostupni (10089/10189). Stvarni AnalysisOnly uzorak i usporedba po strategiji čekaju zdrav feed i spreman instrument/model. [Mjerenje i preostali koraci](SHADOW_READINESS.md). |

## Dogovor za SPY nakon FX nadogradnje — 25.10.2026.

Korisnik je prihvatio [povratak na besplatni Alpaca podatkovni test nakon FX točaka](EURUSD_READINESS.md#dogovoreni-povratak-na-spy-nakon-fx-točaka--25102026): najprije read-only provjera povijesnih SPY SIP transakcija/kotacija starijih od 15 minuta i ograničenog IEX live feeda, zatim plan uvoza u postojeći Parquet/replay i konfigurabilnog izvora. Uplata 500 USD IBKR-u nije preduvjet za taj istraživački put. Pristup novog pružatelja još nije testiran, integracija nije implementirana i pretplata nije kupljena. Povijesni razvoj/testiranje strategija ne potvrđuje IBKR paper izvršenje; točke 25/30/31 ostaju u postojećem statusu. Aktualni FX status i stvarna provjera ugovora navedeni su niže.

## Obvezni IBKR DLL u repozitoriju — 25.10.2026.

- Prema zahtjevu korisnika, službeni `CSharpAPI.dll` verzije `10.49.02.0` dodan je u `third_party/ibkr/10.49.02`, uz SHA-256 identitet, licencu i arhivu pripadajućeg C# izvora. Ovisnost se povlači običnim Git cloneom; ne ovisi o lokalnoj `C:\TWS API` instalaciji.
- `Infrastructure` i `ShadowProbe` uvoze zajednički `IBKR.Api.targets`. Stvarni adapter, njegova registracija i dijagnostika više nisu uvjetno kompilirani. `Google.Protobuf` 3.29.5 je obvezna NuGet ovisnost. Nedostajući DLL prekida restore/build s `IBKR001`, a izmijenjeni s `IBKR002`. Testni unavailable/fake servisi ostaju za izolirane testove, ali nisu automatski produkcijski fallback.
- Postojeći DI test više ne preskače provjeru ako stvarni adapter nedostaje: zahtijeva zajedničku realnu instancu za order/account/position/bar/tick servise i `Real` dijagnostiku s verzijom API-ja, bez otvaranja broker veze.
- Lokalna završna provjera: restore, Release build **0 upozorenja / 0 grešaka**, solution testovi **419/419**. Publish web aplikacije potvrđuje identičan DLL, `Google.Protobuf.dll`, runtime dependency manifest te kopiju licence, izvora i provenance dokumenta. Provjere nedostajuće i izmijenjene datoteke moraju pasti čak i uz postojeći build output; stvarni DLL tijekom provjere ostaje netaknut.
- CI sada koristi isti uključeni DLL, cijeli testni skup i provjeru publish paketa/negativnih build slučajeva. GitHub rezultat prati se nakon pusha; lokalna provjera nije tvrdnja da je CI već završen.
- Bot, ShadowProbe i TWS veza nisu pokretani. Ova promjena rješava trajnu build/runtime ovisnost, ne potvrđuje market-data prava ili paper naloge. U tom commitu FX-2 i daljnje FX točke još nisu bile započete.

## FX nadogradnja — trenutni status

| Točka | Status | Dokaz i granica |
| --- | --- | --- |
| FX-1 — Inventar i regresijska zaštita | Implementirano i lokalno provjereno | Matrica postojećih funkcionalnosti i FX gapova; 13 novih fiksnih stock baseline slučajeva, ukupno 419/419 testova u službenom DLL i fallback buildu. Produkcijsko ponašanje nije promijenjeno. |
| FX-2 — Konfiguracija i identitet valutnih parova | Implementirano i lokalno provjereno | STK/CASH tip, osnovna/kotirana valuta, jedinstveni simbol para i eksplicitne FX strategije; registry restart i zaštita od promjene ugovora pod istim ID-jem. 448/448 lokalnih testova; forex je još disabled. |
| FX-3 — IBKR ugovori i dijagnostika | Implementirano i stvarno read-only provjereno | Konfigurirani ugovori, contract-details dijagnostika i CLI; ispravljen regionalni decimalni parsing službenog dekodera. 481/481 testova; paper TWS potvrdio SPY/EURUSD/EURGBP. Nema potvrde podataka ili izvršavanja. |
| FX-4 — Prikupljanje i povijesna dopuna | Implementirano; stvarna povijest/storage provjereni, live tickovi čekaju otvorenu sesiju | BidAsk + MIDPOINT profil, null volumen, FX kalendar i trajni SQLite/Parquet/backfill. 501/501 lokalnih testova; dva uzastopna read-only paper TWS pokušaja spremila po 60 stvarnih barova. Nema potvrde aktualnih tickova ili izvršavanja. |
| FX-5 — Kratki agregati i featurei | Implementirano i lokalno provjereno | Upareni quote MIDPOINT 1s/5s/15s agregati, as-of/warmup pravila, zasebna verzija i nedostupni volumenski featurei. 521/521 testova; 40 identičnih feature frameova nakon Parquet replaya. Stvarni živi FX-5 tok nije potvrđen. |
| FX-6 — Patterni i bodovanje | Implementirano i lokalno provjereno | Osam cjenovnih patterna, zasebni FX pragovi/verzije, eksplicitno nedostupan volumen i odbijanje VWAP-a, zapis svih procjena bez stock labela. 543/543 testova; stvarni live FX pattern tok nije potvrđen. |
| FX-7 — Strategije i signali | Implementirano i lokalno provjereno | Automatski FX shadow tok, zajednički kod odabira sa stock signalima, verzijska odobrenja, freshness/spread/volatility gateovi i trajni cooldown/checkpoint. 561/561 testova; stvarni živi FX signal nije potvrđen. |
| FX-8 — Veličina, risk i troškovi | Implementirano i lokalno provjereno | Deterministička read-only procjena base units, pregledanih broker pravila, USD troškova/riska i valutnih krakova. 602/602 testova; bez stvarne provizije/margine/filla. Integracija rezervacija i izvršavanja pripada FX-9. |
| FX-9 — Nalozi i pozicije | Implementirano i provjereno simulacijom, uz navedene granice | Eksplicitna paper priprema, zajednička atomska rezervacija/proof, finalni FX refresh, CASH callbackovi i postojeći lifecycle oba izlazna profila. 638/638 testova; stvarni broker fill/exit još nije potvrđen. |
| FX-10 — Replay, labele i modeli | Implementirano i provjereno lokalnom simulacijom | 664/664 testova; zaseban bid/ask execution ugovor, vremenski ML, ručno odobrenje/rollback i opt-in FX-9 spoj. Bez stvarnog fill/model dokaza; [granice i workflow](FX10_RESEARCH.md). |
| FX-11 do FX-12 | Planirano | Sljedeće: onboarding/UI/shadow, zatim zasebno odobren stvarni paper E2E. |

### FX-9 — Nalozi i pozicije

Provjera 25.10.2026., polazni commit `5d57eb8` na `trading-bot-v2`. [Pravila i granice](EURUSD_READINESS.md#fx-9--nalozi-i-pozicije), [dokaz provjere](evidence/fx9-lifecycle-verification-2026-10-25.json).

- `ForexPaperPreparationService` osvježava potvrđeni ugovor/market-rule, pregledane limite, račun, pozicije, otvorene naloge, pending rezervacije, dnevne gubitke/provizije i upareni quote. U istoj transakciji/gateu kao stock kapacitet sprema FX rezervaciju i `ForexExecutionProofRecords`; migracija čuva postojeće zapise.
- Postojeća arbitraža, finalni `ScalpingExecutionGate`, `ApprovedOrderExecutionBackgroundService`, `OrderManager` i `ExitManagementService` provode slanje i izlaz. FX zahtijeva izričiti opt-in, PaperReady, reconciled verified-paper, long USD par i potvrđen USD account summary. Promijenjeni forecast, pravila, cijena, veličina ili kapacitet blokiraju slanje. FX-7 shadow signali još se ne šalju automatski; FX-10 mora povezati odobreni model.
- EURUSD/EURGBP callbackovi čuvaju cijeli par i decimalne base units. Dodani su protobuf execution/commission callbackovi i valuta provizije. ATR trailing koristi FX cjenovne featuree i bid; sekundni timer i restart koriste postojeći trajni deadline. Aktivna/ambiguous FX rezervacija ne istječe vremenski prije razrješenja izloženosti.
- **638/638 solution testova**, uključujući **36 novih slučajeva**: oba profila, SL/TP/timer, partial/cancel, restart, odbijanje i nepoznato prihvaćanje naloga, duplikati/konkurentna priprema, stock+FX kapacitet, finalni gate, oba callback protokola, market-rule, valuta računa/provizije i migracija. Restore i Release build prolaze, **0 upozorenja / 0 grešaka**. Required-DLL publish i negativne DLL provjere prolaze izvan sandboxa; početni sandbox publish ponovno nije dao MSBuild dijagnostiku.
- Nema nove brokerske veze, naloga ili izmjene aktivnih postavki/odobrenja. Stvarno prihvaćanje CASH bracket/stop naloga, rezidualne količine ispod pregledanog minimuma, stvarne provizije i margin/cash ponašanje ostaju za FX-12. Ostale valute računa i cross-par izvršavanje ostaju blokirani. Nedostupni trailing podaci zadržavaju postojeći stop i ne ukidaju timer. Točke 25/30/31 ostaju otvorene. GitHub CI provjerava se nakon pusha.

### FX-8 — Veličina, risk i troškovi

Provjera 25.10.2026., polazni commit `ef54b33` na `trading-bot-v2`. [Pravila, ulazi i granice](EURUSD_READINESS.md#fx-8--veličina-risk-i-troškovi), [dokaz provjere](evidence/fx8-risk-verification-2026-10-25.json).

- `ForexRiskService` registriran je u DI-u uz opcionalne `ForexRisk` profile po instrumentu/strategiji. Koristi postojeće USD monetarne `RiskSettings` limite i instrumentni limit, ali ne propušta FX kroz stock sizing/reservation pretpostavke. Aktivna konfiguracija i stock servisi nisu promijenjeni.
- Količina u osnovnoj valuti zaokružuje se dolje, cijene po pregledanim razredima; provjeravaju se ConId, ruta/market-rule ID, min/max/korak, starost pravila i quote/portfolio/tečaj ulaza. Ne tumači se indicative/suggested size kao izvršivo pravilo.
- Uključeni su minimalna/postotna provizija ulaza i izlaza, više naloga, spread, slippage, buffer, pip vrijednost, stop-risk i neto prednost u USD. Cross-par i račun u drugoj valuti koriste konzervativni bid/ask preračun. Oba valutna kraka i postojeća/pending portfeljna izloženost ulaze u limite. Mali račun ne dobiva izuzeće.
- **602/602 solution testova**, uključujući **41 novi FX-8 slučaj**. Restore prolazi; Release build **0 upozorenja / 0 grešaka**. Required-DLL/publish i negativne provjere nedostajućeg/promijenjenog DLL-a prolaze izvan sandboxa (prvi sandbox publish nije dao MSBuild dijagnostiku). Stock regresije prolaze. GitHub CI provjerava se nakon pusha.
- Ovo je eksplicitni read-only API s `fx-risk-v1` i hashom punog ulaza/postavki. `Passed` nije odobrenje naloga, `ExecutionAuthorized=false`; nema automatske rezervacije ili FX-7 signal/risk routinga. Potpun broker snapshot, stvarna izvršiva pravila i atomska rezervacija s lifecycleom slijede u **FX-9**. FX trade replay/labele su FX-10. Nema nove broker veze, stvarnih FX provizija, margine ni fillova. Točke 25/30/31 ostaju otvorene.

### FX-7 — Strategije i signali

Provjera 25.10.2026., polazni commit `c9acd8e` na `trading-bot-v2`. [Pravila i operativne granice](EURUSD_READINESS.md#fx-7--strategije-i-signali), [lokalni dokaz](evidence/fx7-signal-verification-2026-10-25.json).

- Postojeći `ShortSignalProfile` i zajednički `PatternSignalSelection` određuju izbor patterna, confidence prag i determinističko rangiranje za stock i FX. Zaseban `ForexSignals` profil veže FX-6 konfiguraciju po instrumentu/strategiji/intervalu; `ShortSignals` ne smije usmjeriti CASH kroz stock tok. Stock očekivani rezultati nisu promijenjeni.
- Novi sekundni hosted worker koristi dovršene FX agregate i postojeći feature/pattern/research kod, uz `TradingEnabled=false`. Radi odvojeno od tick kolektora; greška pojedinog profila zapisuje se i ponavlja bez zaustavljanja drugih profila. Na disconnected vezi ne procjenjuje nove signale. Timer dovršava bar i bez novog ticka.
- Signal zahtijeva otvorenu FX sesiju, dovoljno uzastopnih barova i spremne featuree, svjež signal/uparenu kotaciju, spread i ATR/price unutar profila, dopušten smjer i confidence. Odobrenja moraju točno odgovarati `signals-fx-shadow-v1` config verziji, FX-6 pattern verziji i FX feature verziji. Promjena praga, para ili verzije ne nasljeđuje odobrenje drugog profila.
- Postojeći SQLite research zapis čuva svaki prihvaćen/odbijen/blokiran rezultat s konkretnom fazom i razlogom. `FxShadowSignal` znači prihvaćen shadow signal, ne izvršiv nalog. Checkpoint i cooldown obnavljaju se iz trajnog zapisa; isti bar/verzija ne duplicira se nakon restarta. Cooldown obuhvaća smjerove/patterne unutar profila i ostaje aktivan kroz promjene verzije, uz izolaciju para/strategije/intervala.
- **561/561 solution testova**, **18 novih FX-7 slučajeva**: istodobni SPY/EURUSD/EURGBP, dvije strategije i 5s/15s profili, stale/spread/volatility/direction/confidence/approval blokade, promjene verzija, future cutoff/gap, konkurentni pozivi, restart i privremeni kvar spremanja. Testovi potvrđuju prazan FX stock pattern/trade/order kanal, zadržan stock signal i odsutnost stock FX labela. Release build: **0 upozorenja / 0 grešaka**; required-DLL/publish provjera prolazi. Sandbox build bez compiler dijagnostike ponovljen je uspješno izvan sandboxa. GitHub CI prati se nakon pusha.
- Aktivne konfiguracije, odobrenja i brokerska sesija nisu mijenjani; nalog nije slan. Shadow pragovi nisu kalibrirani, stvarna latencija/tick pokrivenost nisu izmjereni. FX model/risk/arbitration/order tok ostaje za FX-8/FX-9/FX-10; puno shadow/paper dokazivanje za FX-11/FX-12. Točke 25/30/31 ostaju otvorene. Sljedeće: **FX-8**.

### FX-6 — Patterni i bodovanje

Provjera 25.10.2026., polazni shared commit `c80f411` (radni `178c4c1`, identično stablo). [Pravila i granice](EURUSD_READINESS.md#fx-6--patterni-i-bodovanje), [sažetak lokalne provjere](evidence/fx6-pattern-verification-2026-10-25.json).

- `ForexPatternDetector` ponovno koristi postojeću geometriju osam long/short cjenovnih patterna, uz zasebne `ForexPatterns` profile po instrumentu/strategiji/intervalu. Profil se validira i kopira prije izračuna; config SHA-256, feature verzija i izvor ostaju dio identiteta. Stock pragovi, fingerprint i očekivani scoreovi nisu promijenjeni.
- Volumen ima `IsAvailable=false`, težinu 0 i razlog; dostupne težine normaliziraju se na 1. Nema neutralnog volumenskog bonusa ni zamjene quote countom. VWAP reclaim/reject i zahtjev za volumenskom potvrdom odbijaju se s razlogom. FX quality/context/confirmation i neckline pragovi eksplicitno se provjeravaju.
- Koriste se finalni zdravi kratki midpoint barovi dostupni do UTC as-of trenutka; input mora biti homogen, gap resetira warmup. Zadano se traži 26 uzastopnih barova i zagrijani cjenovni featurei. Ovo je research, bez execution freshness/spread gatea; potonji pripada strategijskom/risk putu FX-7/FX-8.
- `ForexPatternResearchService` eksplicitno sprema prihvaćene i odbijene procjene u postojeći SQLite. FX verzija ulazi u research/pattern ključ i signal ID; ponavljanje iste procjene je idempotentno, drukčiji profil ne prepisuje raniju procjenu. `labels-fx-deferred` nema label retke: stock per-share labeling se ne primjenjuje na FX. Automatsko pokretanje iz signalnog toka i cooldown ostaju FX-7; trade replay i FX labele ostaju FX-10.
- **543/543 solution testova**, uključujući **22 nova FX-6 slučaja**. Provjereni svi cjenovni patterni, VWAP/volume odbijanje, pragovi, decimalna preciznost, config binding/version snapshot, as-of/gap i povezani quote → aggregate → feature → pattern → SQLite tok uz `TradingEnabled=false`. SPY baseline prolazi. Release kompilacija bez upozorenja; required-DLL/publish provjera prolazi. Sandbox publish ponovno je zahtijevao ponavljanje izvan sandboxa, bez promjene koda. GitHub CI provjerava se nakon pusha.
- Nije pokrenuta nova brokerska veza niti slan nalog. Pragovi nisu empirijski kalibrirani, stvarni live/paper pattern tok nije potvrđen, a točke 25/30/31 i FX-12 nisu zatvorene. Sljedeće: **FX-7**, uz obvezni `TradingEnabled=false`.

### FX-5 — Kratki agregati i featurei

Provjera 25.10.2026., polazni shared commit `e06b292` (radni `9d26e95`, identično stablo). [Točan ugovor i granice](EURUSD_READINESS.md#fx-5--kratki-quote-agregati-i-featurei).

- Zasebni FX put u postojećem latest-data servisu gradi finalne UTC midpoint agregate iz oba dijela istog BidAsk callbacka. Novi `.v2` izvor, zajednički pair ID i receive vrijeme već stanu u postojeći canonical/Parquet ugovor. Stock agregati ostaju trade-based; FX nema lažni trade/volumen.
- 5s/15s zadani i opcionalni 1s intervali, dopušteno kašnjenje, freshness, deterministički tie redoslijed, ograničeni bufferi, brojači odbačenih/nepotpunih/kasnih quoteova. Ne popunjavaju se praznine niti prepravljaju završeni barovi. Poznato neispravan quote isključuje svoj otvoreni prozor; reconnect/sesija resetira zagrijavanje.
- Postojeći feature engine i FacioQuo ponovno se koriste kroz eksplicitni FX midpoint ugovor. Cjenovni indikatori dobivaju FX verziju i as-of kontrolu dostupnosti; volumen/VWAP ostaju null s razlogom. Gap resetira kontinuitet zagrijavanja. Feature verzija uključuje aggregate pravila i timeframe; stock feature i short-replay fingerprint nisu zamijenjeni.
- Postojeći replay dobio je odvojeni read-only `ReplayForexFeaturesAsync`: zdrav v2 Parquet input, receive-time virtualni sat, input SHA-256 i svi feature frameovi, bez operational DB/pattern/order poziva. Generički stock trade replay odbija FX do FX-6/FX-10. Ovo ne zatvara puni FX backtest ili točku 25.
- **521/521 lokalnih solution testova**, 20 novih slučajeva. Sva feature polja na 40 sintetičkih barova numerički su jednaka između live servisa i stvarnog Parquet replaya; ponavljanje, raniji cutoff, nedostupan volumen, gap/reconnect, 1s/5s/15s i istodobni EUR/USD/EUR/GBP/SPY pokriveni su testovima. Release build ima **0 upozorenja / 0 grešaka**. Publish i required-DLL provjera prolaze; prvi sandbox publish završio je bez compiler dijagnostike, a ponavljanje izvan sandboxa prošlo je bez izmjene koda. GitHub CI prati se nakon pusha.
- ShadowProbe izvještaj proširen je brojem FX agregata, dijagnostikom i feature snapshotom za buduću read-only probu u otvorenoj sesiji. Nije otvarana nova broker veza ni slan nalog. FX-4 historical uzorak nije dokaz živih FX-5 featurea; ta provjera ostaje odvojena. Sljedeće: FX-6, uz obvezni `TradingEnabled=false`.

### FX-4 — Prikupljanje i povijesna dopuna

Provjera 25.10.2026., polazni shared commit `f132515` (radni `1f09f3e`, identično stablo). [Podatkovni ugovor i granice](EURUSD_READINESS.md#fx-4--prikupljanje-i-povijesna-dopuna).

- FX konfiguracija smije uključiti prikupljanje uz obvezni `TradingEnabled=false`. CASH/IDEALPRO koristi samo BidAsk tickove i MIDPOINT barove sa svim satima sesije. STK ostaje AllLast/BidAsk + TRADES. Dodan zasebni primjer `forex-collection.example.json`; aktivna konfiguracija nije mijenjana.
- Nedostupan trade volumen ostaje null kroz adapter, canonical podatke, Parquet i SQLite. Nova migracija čuva stock retke i indekse; testira se nad izoliranom bazom. Domenski model čuva zasebnu dostupnost volumena, a postojeća stock analiza i kratki signalni okidači ne obrađuju FX podatke prije FX-5/FX-6. Migracija nema automatski downgrade koji bi null pretvorio u 0; za povratak na staru shemu potreban je prethodni backup.
- Zajednički FX collection kalendar prati New York sesiju, vikend, dnevni prekid i DST. Backfill ima postojeće trajne checkpointe/retry/pacing, FX gap report uključuje rubove i prazne otvorene dane, a tick gapovi isključuju zatvorene intervale. Onemogućeni instrumenti ne pokreću backfill. Blagdanske/izvanredne iznimke nisu automatski preuzete. Minutni backfill ne popravlja tick pokrivenost.
- Pri stvarnoj probi prvi zahtjev povremeno nije dobio odgovor. Pregled je pokazao da se zahtjevi šalju prije potvrđenog API handshakea; adapter sada čeka automatski `nextValidId`, uz timeout i zatvaranje neuspjele veze. Nakon izmjene prošla su dva uzastopna pokušaja istim client ID-jem 96. To je ograničena provjera ponovnog spajanja, ne dugotrajni test pouzdanosti.
- Stvarni paper TWS, server 225/API 10.49.2.0: **60 EUR/USD 1m MIDPOINT barova** za 23.10.2026. 20:00–21:00 UTC, spremljenih u SQLite i Parquet uz validan manifest, null volumen i 0 analysis događaja. [Prva proba](evidence/fx4-data-probe-2026-10-25.json) i [ponovno spajanje](evidence/fx4-data-probe-reconnect-2026-10-25.json). Oba pokušaja bila su tijekom zatvorene nedjeljne sesije: 0 quote tickova, `LiveQuotesConfirmed=false`, bez market-data greške ili lažnog vikend gapa. Za stvarnu live potvrdu treba ponoviti read-only probu u otvorenoj FX sesiji.
- Završni Release build: **0 upozorenja / 0 grešaka**, solution **501/501 testova**, bez preskakanja. Obuhvaćeni su stock/FX regresija, callbackovi, migracija i Parquet null-volume round-trip, miješani kolektori, noćna sesija, prekid jednog toka, restart backfilla i DST. Prošla je publish/required-DLL provjera. GitHub CI prati se nakon pusha.
- Read-only probe pokreće samo kolektore/writer i ograničeni povijesni zahtjev uz order-blocked adapter i novu izoliranu bazu. Nije poslan nalog. FX-12 te točke 25/30/31 ostaju otvorene; sljedeća razvojna točka je FX-5.

### FX-1 — Popis funkcionalnosti i zaštita dioničkog ponašanja

Provjera 23.–25.10.2026., polazni shared commit `d4bf83c` (radni `75fe6e0`, identično stablo). [Stock compatibility baseline](STOCK_COMPATIBILITY_BASELINE.md) dokumentira postojeće komponente, testove, načine izlaza i ograničenja potrebna za aditivnu FX implementaciju.

- Dodan `StockCompatibilityBaselineTests`: fiksni rezultati 10 aktivnih long/short patterna i njihovih score komponenti, default canonical featurei na 40-bar SPY uzorku te dva odbijanja VWAP patterna bez volumena. Sintetički fixturei nisu povijesni podaci ni dokaz profitabilnosti. Short patterni ostaju research-only; `HeadAndShoulders` i `Triangle` postoje samo u enumu, bez detektora.
- Početni Release baseline: **406/406**. Novi izdvojeni razred: **13/13**. Konačni solution: **419/419**, bez preskočenih testova, u Release buildu sa službenim DLL-om i Debug fallback buildu bez njega. Oba puna builda imaju **0 upozorenja i 0 grešaka**. Potvrđen je `IBKR_API_AVAILABLE` u službenom buildu i identičan SHA-256 kopiranog DLL-a; fallback nema taj kompilacijski simbol.
- Lokalni TRX dokazi su u ignoriranom `TestResults/fx1-before`, `TestResults/fx1-official` i `TestResults/fx1-fallback`. GitHub CI za ovaj commit nije pokrenut niti se lokalni rezultat predstavlja kao CI potvrda.
- U ograničenom izvršnom okruženju pomoćna IBKR skripta/višestruki MSBuild procesi vraćali su failure bez compiler greške, a fallback testni proces zastao je bez završnog rezultata. NuGet je prijavio `NU1900` zbog nedostupnog mrežnog izvora. Izravni jednoprocesni buildovi prošli su; ponovljeni restore, fallback testovi i cijela `scripts/verify-ibkr-adapter.ps1` skripta izvan ograničenja prošli su bez promjene koda ili isključivanja NuGet audita. Točan uzrok zastoja nije izoliran; nije zabilježen pad pojedinog testa. Za fallback je eksplicitno korištena nepostojeća putanja `C:\__fx1_no_ibkr__\CSharpAPI.dll`. Završni restore vraćen je na zadani službeni DLL.
- Nema izmjene produkcijskog koda, konfiguracije, paketa ili migracija. Oba izlazna profila i svi postojeći testovi ostaju. Nije pokrenut bot niti slan nalog. FX order tok i stvarni paper E2E ostaju neimplementirani/nepotvrđeni; točke 25, 30 i 31 nisu ovime zatvorene.

### FX-2 — Konfiguracija i identitet valutnih parova

Provjera 25.10.2026., polazni shared commit `49f6b67` sa službenim obveznim IBKR DLL-om. [Pravila konfiguracije i primjer](EURUSD_READINESS.md#fx-2--konfiguracija-i-identitet).

- Postojeći `SecurityType` bira `Stock`/`Forex`; `BaseCurrency` je novo konfiguracijsko polje za CASH, a `Currency` ostaje kotirana valuta. Interni simbol para (`EURUSD`/`EURGBP`) odvojen je od budućeg broker simbola (`EUR`). Alias formati normaliziraju se; duplikati, neusklađene valute i nepodržani tipovi prijavljuju konkretnu options grešku.
- Strategije, smjerovi, timeframeovi i limiti pripadaju instrumentu. FX strategije moraju biti eksplicitne. JSON binding više ne dopisuje inicijalizirane stock zadane strategije/smjerove uz izričito zadane nizove; zadane vrijednosti primjenjuju se nakon bindinga samo kad su izostavljene. Zadržani su stock `deterministic-patterns`/`Long` defaulti i legacy `Symbols` normalizacija.
- Registry koristi postojeće SQLite stupce i vraća valutne metapodatke nakon restarta, bez migracije baze. Dodavanje disabled FX parova ne mijenja stock configuration hash ni onboarding napredak. Promjena FX ugovora, uključujući prijelaz stock/FX pod istim ID-jem, odbija cijeli sync prije upisa i traži novi `InstrumentId`.
- `forex-instruments.example.json` je testiran stvarnim JSON binderom; nije automatski učitan i ne mijenja `appsettings.json`. Aktivacija FX prikupljanja/trgovanja u ovoj fazi izričito je blokirana kako CASH ne bi otišao u još uvijek STK brokerski put. Read-only ugovori/feed ostaju FX-3/FX-4, izvršavanje kasnije.
- Dodano **29 testnih slučajeva**; ciljana konfiguracija/registry regresija **38/38**, puni solution **448/448**. Release build sa službenim DLL-om: **0 upozorenja, 0 grešaka**. Prošla je cijela `verify-ibkr-adapter.ps1` provjera, uključujući publish i odbijanje nedostajućeg/izmijenjenog DLL-a. GitHub CI prati se nakon pusha. Nije pokrenut bot niti slan nalog; ovo nije stvarni IBKR paper dokaz i ne zatvara točke 25/30/31.

### FX-3 — IBKR ugovori i dijagnostika

Provjera 25.10.2026., polazni shared commit `b972879` (radni `3257976`, identično stablo). [Ugovor i ponovljiva read-only provjera](EURUSD_READINESS.md#fx-3--ibkr-ugovori-i-dijagnostika).

- Zajednička tvornica koristi normaliziranu konfiguraciju za STK/SMART/USD i CASH/IDEALPRO. Interni EURUSD/EURGBP prevodi u brokerski EUR s različitom kotiranom valutom; nepoznati/neusklađeni simboli i ID-jevi odbijaju se prije API poziva. Postojeći SPY/QQQ put koristi isti mehanizam.
- Contract potvrda traži točno jedan odgovarajući odgovor s pozitivnim ConId-jem; odvojeni su missing/ambiguous/mismatch/error/timeout ishodi. Dijagnostika dodaje traženi i dobiveni identitet, count i dostupne tick/size/session/market-rule metapodatke; nema izmišljanja nepoznatih minimuma ili izvoza računa/sirovih poruka. Potvrda ugovora ne otključava prikupljanje ili naloge.
- Novi contract-only način postojećeg ShadowProbea čita zasebnu konfiguraciju, provjerava disabled FX parove i zahtijeva AnalysisOnly uz isključeno trgovanje. Adapterov read-only način blokira sve order mutacije i readiness; nema pokretanja hosta, baze, kolektora ili trading lifecyclea. FX podatkovni i order pozivi ostaju izričito blokirani do sljedećih pripadajućih točaka. Shared runtime konfiguracija i baze nisu mijenjane.
- Dodano **33 testna slučaja**, uključujući stvarne adapter callbackove sa sintetičkim IBKR odgovorima i istodobne parove s istim base simbolom. Završni Release build: **0 upozorenja / 0 grešaka**; puni solution **481/481**, bez preskakanja. Testni projekt sada izravno uvozi isti obvezni `IBKR.Api.targets`. Prošla publish/required-DLL provjera; lokalni TRX je u ignoriranom `TestResults/fx3`. GitHub CI prati se nakon pusha.
- Stvarna proba otkrila je regionalni bug službenog protobuf dekodera (`Util.StringToDoubleMax` koristi `double.Parse` bez kulture): hr-HR pogrešno pretvara FX tick 0.00005 u 5 i stock tick 0.01 u 1. Obrada brokerskih poruka sada koristi scoped invariant culture i vraća prethodnu kulturu i kod iznimke, bez izmjene vendored DLL-a ili globalnog UI formata. Pet dodatnih regresijskih slučajeva uključuje stvarni SDK protobuf decoder pod hr-HR/de-DE/en-US.
- Nakon pokretanja TWS-a završna **stvarna read-only proba** na `127.0.0.1:7497`, client ID 94, server 225/API 10.49.2.0 potvrđuje SPY **756733**, EURUSD **12087792** i EURGBP **12087807**, svaki jednim točnim odgovorom; tickovi su 0.01/0.00005/0.00005. [JSON dokaz, 25.10.2026. 12:23 UTC](evidence/fx3-contract-probe-2026-10-25.json). Ranije nedostupna veza i jedan timeout/prekid pri prvom spajanju ostaju zabilježeni u FX protokolu; ne pripisuju se pretplati. Nije pokrenut bot, zatražen feed ili poslan nalog. Ovo zatvara **FX-3 ugovore**, ne FX podatkovni tok/izvršavanje niti točke 25/30/31. Sljedeća zasebno odobrena točka je FX-4.

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

## Točka 21 — kratki agregati i indikatori

- `ShortAggregates` zadano izrađuje 5s i 15s UTC trade prozore, a konfiguracija može uključiti 1s. Prozor se finalizira kada receive-time watermark dosegne kraj prozora plus dopušteno kašnjenje (zadano 2s). Collection monitor pomiče watermark i bez novog ticka. `AvailableAtUtc` označava najraniju dopuštenu dostupnost; stvarna finalizacija može kasniti do sljedećeg monitora.
- Snapshot i `GetCompletedShortAggregates(instrumentId, intervalSeconds, asOfUtc)` vraćaju samo finalizirane barove. Prazni intervali ne stvaraju lažne OHLC vrijednosti; broj preskočenih intervala nalazi se u `EmptyIntervalsBefore`. Tick za već zatvoren prozor ne mijenja bar i broji se kao late; ponovljeni `EventId` ne povećava OHLC/volumen unutar bounded deduplikacijskog prozora. Per-instrument povijest je bounded.
- `FacioQuo.Stock.Indicators` 3.0.1 je izoliran malim adapterom. Testovi uspoređuju EMA/RSI s dosadašnjim formulama na svim warmup duljinama; njihova razlika je samo pretvorba `double`/`decimal`. Runtime ih koristi pod `features-v3`. ATR namjerno ostaje dosadašnji rolling mean true range: FacioQuo Wilder ATR daje drugačije vrijednosti, pa se ne mijenja bez zasebne evaluacije. Isti `FeatureEngine` i verziju koriste live, backfill i postojeći replay.
- Test iz Parquet tickova ponovno gradi agregate u receive-time redoslijedu i uspoređuje as-of vrijednosti s live izračunom; testovi zasebno pokrivaju kašnjenje, nedostajuće prozore, duplikate i izolaciju instrumenata. To potvrđuje determinističku agregaciju nad istim tick inputom, ali postojeći replay još ne pokreće kratke signale ni order/fill/exit ciklus. Točke 22 i 25 ostaju zasebne.
- Promjena `features-v2` → `features-v3` namjerno blokira stare odobrene modele na exact-version provjeri. Potrebno ih je ponovno evaluirati, trenirati i odobriti prije njihova korištenja. `AnalysisOnly` i zabrana automatskog short izvršavanja ostaju.

## Točka 22 — kratki signalni okidači

- `ShortSignals:Profiles` uključuje odabrani interval (1s/5s/15s), postojeće pattern tipove, minimalan broj uzastopnih finaliziranih barova, minimalni confidence, cooldown i maksimalnu starost. Profil mora odgovarati omogućenom instrumentu/strategiji i intervalu iz `ShortAggregates`; zadano nema aktivnih profila. Prazan interval prekida warmup niz. Zakašnjeli ili prestari bar ne proizvodi signal.
- Tick collection worker nakon zdravog događaja i tijekom monitor ticka obrađuje novofinalizirane barove. Isti prozor se ne objavljuje dvaput, a odabrani kandidat ima deterministički signal ID iz instrumenta, strategije, patterna, kratkog timeframea i kraja prozora. Svaki instrument ima vlastitu serijalizaciju okidača; različiti instrumenti mogu se obraditi paralelno. Event bus zadano koristi backpressure umjesto tihog `DropOldest` gubitka kandidata.
- Kratki bar koristi postojeći `PatternDetector`; 1m/5m/15m snapshot ostaje širi kontekst u postojećem `PatternDecisionBackgroundService`. Kandidat prolazi isti quality gate, odobreni lokalni model, strategiju, portfolio risk i signal arbitration. Za kratke kandidate LLM put je zabranjen. Short smjer i dalje ostaje research-only na postojećem quality gateu.
- Contract kratkog signala dodaje sufiks `+short-{interval}s-v1` na kanonsku feature verziju. Time minute-model ne može slučajno ocijeniti kratki signal; za istodobnu minutu i kratku strategiju treba odvojen `StrategyId` i zasebno treniran/odobren model. Bez njega signal može biti istraživački kandidat, ali nalog je fail-closed.
- Lokalni testovi pokrivaju signal unutar minute iz stvarnog detektora na agregiranim tickovima, dva instrumenta i više strategija, ponovljenu obradu, cooldown, as-of bez budućih barova, prekid na praznom intervalu, gate konfiguraciju, lokalni model bez LLM poziva, profilnu starost i event-bus backpressure. Ovo nije potvrda stvarne TWS paper latencije, fillova ni kratkog replaya cijelog order/exit ciklusa; točke 25 i 31 ostaju otvorene. Siguran primjer konfiguracije je `TradingBot.Web/short-signals.example.json`.

## Točka 23 — BUY → fill → fiksni SL/TP

- Početni exit profil sada je `FixedBracket` uz konfigurirani limit držanja 180 sekundi; `AtrTrailing` ostaje zasebna mogućnost. `AnalysisOnly` je i dalje zadani operativni način, a live trgovanje nije uključeno. Neovisno provođenje vremenskog limita dovršeno je u točki 24.
- Bracket zahtijeva isti simbol i količinu za parent BUY, stop i target. Parent i stop šalju se s `Transmit=false`, a posljednji target s `Transmit=true`. Ako stop nije potvrđen, target se ne šalje; ako bilo koji child zakaže, traži se otkazivanje parenta, rezultat ostaje `PendingBrokerConfirmation`, rezervacija se ne oslobađa na temelju same cancel naredbe i entry/child/pozicija zahtijevaju broker reconciliation. Exit zapis ostaje `Faulted` i blokira nove ulaze.
- IBKR child naloge veže `ParentId` i ista OCA grupa. OCA način 2 proporcionalno smanjuje drugi izlaz pri djelomičnom fillu uz brokerovu zaštitu od overfilla. Lokalno se nakon entry i izlaznih partial fillova potvrđuje resize preostalog stopa i targeta; izmjena čuva parent/OCA vezu i računa ukupnu količinu kao vlastiti kumulativni fill plus preostalu poziciju. Odbijanje zaštitnog childa ili izmjene degradira engine i traži ručnu provjeru otvorenih naloga i neto broker pozicije. Uočen zbroj izlaza iznad entry fillova također degradira engine umjesto da se samo prešutno odreže u evidenciji.
- Upravljani izlaz najprije traži otkazivanje oba childa i čeka zasebne brokerske `Cancelled` potvrde prije tržišnog SELL-a. Potvrde se trajno čuvaju u SQLite; restart s neriješenim otkazivanjem ili `Faulted` stanjem ostaje fail-closed. Kada zaštitni nalog nije prihvaćen, operator mora potvrditi otkazivanje preostalog BUY-a, otvorene child naloge i neto poziciju u IBKR paper računu te po potrebi ručno zaštititi ili zatvoriti poziciju prije nastavka.
- Lokalni fake testovi pokrivaju djelomični entry, oba smjera djelomičnog izlaza, odbijeni stop/target, potvrde otkazivanja i detekciju mogućeg overfilla. Release build sa službenim `CSharpAPI.dll` potvrđuje kompilaciju adaptera. Nema stvarne IBKR paper potvrde fill/OCA/cancel redoslijeda; točka 31 ostaje obvezna.

## Točka 24 — neovisni vremenski izlaz u sekundama

- `ExitManagementHostedService` nakon startup restorea odmah provjerava rokove i ponavlja provjeru svake sekunde, bez svijeće ili ticka. Vremenska provjera uklonjena je iz trailing candle obrade. Konačno odobreni horizont u sekundama ulazi u exit zapis, ograničen strožim instrument/exit pravilom; nema zaokruživanja na minute.
- `OpenedUtc` i trajni `ExitDeadlineUtc` nastaju na prvom potvrđenom fillu. Dodatni entry fillovi ne produljuju rok; zakašnjeli execution callback može ga samo pomaknuti ranije. Ako se fill dogodio prije registracije exit zapisa, najraniji spremljeni execution timestamp ima prednost. Restore starijih otvorenih zapisa bez roka koristi `Trade.OpenedUtc`, a tek bez tradea konzervativni raniji registracijski timestamp. Restart rok ne pomiče kasnije.
- Ako je BUY djelomično izvršen, prije vremenskog izlaza traži se otkazivanje preostalog BUY-a i čeka brokerova `Cancelled` potvrda. Eventualni dodatni fill tijekom otkazivanja ulazi u istu poziciju i ne pomiče rok. Zatim se čekaju zasebne potvrde otkazivanja stopa i targeta, a market SELL koristi samo preostalu fillanu količinu. Dok je ostatak BUY-a u otkazivanju novi ulazi su blokirani.
- Ako veza padne ili račun/pozicije/nalozi nisu ponovno provjereni, istek se trajno bilježi kao `TimeExitDeferred` i trading se degradira. Reconnect prvo uspoređuje brokerovu neto poziciju i otvorene naloge sa SQLite stanjem; čak i pri uspješnoj usporedbi neriješeni izlaz ne vraća `Ready`. Nadzor potom nastavlja iz potvrđenih cancel stanja. Nepoznat ishod cancel/submit zahtjeva ostaje `EntryCancelPending`, `ExitCancelPending` ili `Faulted` bez slijepog ponovnog SELL-a; operator mora pregledati IBKR paper naloge/poziciju i uskladiti stanje ako callback ili reconciliation ne razriješe slučaj.
- Lokalni testovi pokrivaju start bez tržišnog događaja, prvi fill i dodatne fillove, horizont od 5 sekundi, djelomični BUY i target, restart s postojećim i starim zapisom, prekid veze prije i tijekom otkazivanja, ponovno spajanje i zabranu `Ready` dok izlaz čeka. Ovo je provjereno simulacijom; stvarni IBKR paper redoslijed i latencija ostaju točka 31. Zadani način rada i dalje je `AnalysisOnly`, bez pokretanja naloga u ovoj provjeri.

## Točka 25 — replay i backtest cijelog trgovanja (u tijeku)

- Dovršeni deterministički replay sada se može backtestirati zasebnim virtualnim brokerom preko `POST /api/replays/{replayRunId}/backtests`; `GET /api/replays/backtests/{backtestId}` vraća trajni rezultat. Prije simulacije ponovno se provjeravaju Parquet manifest, hash točno odabranog ulaza i replay output hash. Identitet rezultata uključuje backtest zahtjev i fingerprint risk/execution postavki; jednaki input i konfiguracija za isti run vraćaju isti SQLite zapis i output hash.
- Replay koristi stvarne spremljene pattern signale i postojeći `PositionSizer`, `ScalpingExecutionSettings` za entry politiku/quote pragove/net-edge buffer te long `FixedBracket` semantiku stopa, targeta i roka od prvog filla. Stop i target za sada dolaze iz eksplicitnih backtest bps parametara, a ne iz stvarno odobrenog `StrategyDecision` plana. Virtualni sat je vrijeme tržišnog događaja; signal ne koristi buduće tickove. Ne postoji ovisnost o brokerskom adapteru ni order kanalu.
- Virtualni nalog čeka zadanu latenciju, ima rok isteka i ograničenje količine po dostupnoj veličini i participation stopi. Passive limit zahtijeva trade strogo ispod limita, marketable limit ask strogo ispod limita; sam dodir limita ne dokazuje fill. Entry i exit mogu biti djelomični. Stop, target i timer računaju izlaz po bidu uz slippage; minimalna provizija se primjenjuje na obje strane. Neto edge nakon troška mora proći zadani prag prije simuliranog naloga.
- Rezultat razlikuje neizvršen nalog, nedostatnu pokrivenost do isteka, otvorenu poziciju na kraju dataseta i zatvoren trade. Neto P&L se iskazuje samo za potpuno zatvoren trade. Zasebni `HorizonReturnBps` računa se iz kasnijeg bida na kraju holding horizonta i ne zamjenjuje stvarni stop/target/timer rezultat. Bez idućeg odgovarajućeg ticka ne izmišlja se fill ni prisilni izlaz iz OHLC bara.
- Nastavak 22.10.2026.: replay sada prihvaća i tick-only ulaz kada su instrument i strategija povezani s omogućenim `ShortSignals` profilom. Izolirani `LatestMarketDataService` gradi kratke agregate u virtualnom vremenu, a `ShortSignalTriggerService.CollectAsync` koristi ista produkcijska pravila za profil, minimalne barove, kvalitetu patterna i cooldown bez objave na live event bus. Izvorni događaj signala je tick koji je agregat učinio dostupnim. Virtualni broker računa entry latenciju i istek od tog događaja, a ne retroaktivno od kraja kratke svijeće. Fingerprint kratkog profila/agregata i dopuštenih smjerova ulazi u replay verziju te se promjena nakon checkpointa odbija. Tick-only 5s signal → entry → target provjeren je integracijskim testom, uključujući resume kroz novu instancu servisa.
- Nastavak virtualnog sata: kratki agregat sada se zatvara točno na `WindowEndUtc + AllowedLatenessSeconds` i kada nema novog ticka. Signal čuva taj trenutak u `replayAvailableAtUtc`, a njegov izvor ostaje zadnji već viđeni tržišni događaj; backtest ne dopušta nalog prije dostupnosti. Tick primljen nakon zatvaranja njegova prozora ne može retroaktivno promijeniti agregat. Kratki replay odbija skup u kojem redoslijed primitka proturječi redoslijedu događaja, umjesto da prešutno koristi buduće podatke. Testovi pokrivaju virtualni timer bez novog ticka, izostanak izmišljenog filla, kasno primljen trade, nastavak nakon checkpointa i signal prije stvarnog entry događaja.
- Nastavak 23.10.2026.: virtualni broker obrađuje signale po trenutku kada su stvarno dostupni, a kotaciju za odluku uzima samo iz dotad dostupnih događaja. Signali s ranijim izvornim tickom, ali kasnijim virtualnim timerom više ne mogu preskočiti raniju odluku drugog signala. Pasivni BUY nakon trade-through događaja konzervativno dobiva postavljeni limit, ne povoljniju cijenu trade printa. Nakon stop/target okidača ili isteka horizonta preostali BUY se smatra otkazanim i ne skuplja nove fillove; aktivirani stop/timer čeka sljedeći bid s raspoloživom veličinom, dok target i dalje zahtijeva bid iznad limita. Ugovor simulacije podignut je na `replay-backtest-v2`, pa postojeći v1 zapis ne može prikriti novi rezultat za isti zahtjev. Ovo je istraživački model otkazivanja, ne potvrda stvarnog IBKR callback redoslijeda. Dodani su usmjereni testovi za sve navedene slučajeve; Release solution: 404/404 testova prolazi.
- Nastavak 23.10.2026.: kratki replay sada sprema i istraživačke zapise za prihvaćene i filtrirane patterne. Prihvaćeni zapis i replay signal dijele isti `SignalId`. Vrijeme odluke i početak label horizonta su trenutak kada je agregat stvarno dostupan, a početna cijena je sredina svježeg bid/ask quotea poznatog u tom trenutku. Bez takvog quotea labela je `InsufficientData`, bez neto rezultata; raniji tickovi ne ulaze u račun ni ulazni hash. Novi fingerprint postavki labele i replay verzija sprječavaju miješanje sa starim rezultatima. Release solution: 406/406 testova prolazi. Ovo je provjera determinističkom simulacijom bez naloga.
- Lokalni Release solution testovi nakon nastavka: 399/399 prolazi. Build nema upozorenja. Provjera je isključivo simulacijska; nije korišten TWS niti je poslan nalog.
- Provjera nastavka: Release build bez upozorenja; 397/397 solution testova prolazi. Jedan raniji puni prolaz imao je izolirani pad nepovezanog vremenski osjetljivog `BackgroundPipelineTests` testa; zasebno ponavljanje i sljedeći puni prolaz prošli su bez izmjene tog testa. Ovo ostaje simulacijska provjera, bez pokretanja bota ili slanja naloga.
- Lokalna Release provjera sa službenim IBKR DLL-om i fallback Debug provjera bez njega: 356/356 solution testova u oba načina, Release build bez warninga. Testovi uključuju djelomični fill, minimum provizije, limit touch, stop, target, timer, nedostatnu likvidnost, as-of zaštitu za sljedeći signal, idempotenciju, migraciju te odbijanje izmijenjenog ulaza. Stvarna IBKR paper potvrda nije provedena.

### Preostalo za punu točku 25

- Kratki replay research zapis sada postoji, ali se još ne izvršava puni odobreni numerički model. Nema pune reprodukcije `StrategyEngine`, `RiskEngine` s portfeljnim rezervacijama ni brokera s cancel/reconciliation callbackovima. Testirana je deterministička research simulacija jednog long položaja po runu; višestruka paralelna strategijska izvršenja i short ostaju izvan tog ugovora. Skupovi s drukčijim redoslijedom primitka i event-timea zasad se odbijaju; njihovo reproduciranje traži zaseban ugovor za as-of sortiranje i izvršenje. Za početak zasebne EUR/USD implementacije nije nužno proglasiti cijelu točku 25 završenom; FX replay će trebati vlastitu provjeru ugovora, veličine pozicije i troškova.
- Fill model koristi dostupni quote/trade `Size` i participation pretpostavku, ne poznaje stvarni red čekanja ni dubinu knjige. Slippage, latenciju, provizije i participation treba kalibrirati na stvarnim paper fillovima. Bez te provjere backtest nije dokaz ostvarivog profita.
- Za odvojeni FX nastavak pripremljen je [EUR/USD opseg i redoslijed provjere](EURUSD_READINESS.md); njegov dokazani feed ne znači da je dionički virtualni broker primjenjiv na forex.

## Točka 26 — pokrivene labele i vremenska ML evaluacija

- `labels-v2` odbija sekundne labele bez zdravih, finalnih trade tickova s razmacima većim od `CandidateLabeling:MaximumTickGapSeconds` (zadano 5 s), uključujući početak i kraj horizonta. Bar sam ne dokazuje redoslijed izvršenja. Istodobni dodiri stopa i targeta ostaju `Ambiguous`; `InsufficientData` i `Ambiguous` nemaju upotrebljiv neto rezultat i ne ulaze u evaluaciju ili trening.
- `GrossReturnBps` i `NetReturnBps` sada opisuju prvi stop, konzervativno potvrđen target ili vremenski izlaz uz konfigurirani spread, proviziju i slippage. `HorizonGrossReturnBps` i `HorizonExitPrice` ostaju zasebni dijagnostički prinos na kraju horizonta. Ovo je research simulacija konfiguriranog SL/TP/timer pravila, a ne stvarni brokerski fill ni puni order lifecycle backtest iz točke 25.
- Svaka izračunana labela sprema SHA-256 točnih Parquet događaja u svom prozoru; čitanje prethodno provjerava manifest/hash datoteka. `labels-v2`, `evaluation-v2`, postojeće feature/pattern verzije i input hash ulaze u identitet evaluacije/treninga. Model prije treninga ponovno računa evaluacijski hash nad spremljenim labelama te odbija promijenjen skup. Stare `labels-v1` labele ne pretvaraju se automatski: potrebno je ponovno stvoriti kandidate/labele iz provjerenih podataka i novu evaluaciju/model/odobrenje.
- Fold trening završava prije početka validacije samo ako su završili i njegovi label horizonti; isto vrijedi za granicu netaknutog holdouta. Završni evaluacijski prag bira se iz walk-forward validacije, a završni prag LightGBM vjerojatnosti iz predikcija na vremenskim validacijskim foldovima. Holdout se ne koristi za izbor praga. Prikazane prosječne povijesne neto vrijednosti nisu pojedinačna prognoza za sljedeći nalog; entry planning koristi zasebnu konfiguriranu ciljnu pretpostavku.
- Lokalni Release restore/build prolazi bez upozorenja; 369/369 solution testova prolazi. Testovi pokrivaju manjak tick pokrivenosti, neodređen redoslijed događaja, stop nasuprot horizon rezultatu, vremenski purge, netaknuti holdout, verzije i odbijanje izmijenjene labele prije treninga. Nije pokrenut bot ni poslan nalog. Rezultati s pravog IBKR paper računa i kalibracija troškova ostaju za točku 31.

## Točka 27 — operativno upravljanje modelima

- `TradingBot.ModelOps` je zaseban .NET CLI koji ne registrira Web host, broker adapter ni trading workere. Traži apsolutnu putanju postojeće SQLite baze i čita iste research, labeling i model postavke iz `TradingBot.Web/appsettings.json` uz standardne environment overrideove. `migrate` je eksplicitna naredba; ostale naredbe odbijaju bazu s neprimijenjenim migracijama. Dostupni su `evaluate`, `train`, `list`, `show`, `compare`, `approve`, `reject`, `rollback` i `history`. Odobrenje i rollback traže pregledani output SHA-256, identitet pregledavatelja i razlog. CLI primjeri su u README-u.
- Svaka promjena statusa modela ima trajni audit redak s modelom, prije/poslije statusom, akcijom, pregledavateljem, razlogom, vremenom i pregledanim output hashom. Novi odobreni model u istoj instrument/strategija skupini supersedira prethodni. Rollback je dopušten samo na prethodno odobren `Superseded` model koji još prolazi hash, odobravanje i aktualni pattern/label ugovor; sadašnji se model pritom supersedira. Odbijeni model nije rollback kandidat.
- Odobreni LightGBM artefakt učitava se jednom u singleton predmemoriju po instrumentu/strategiji. Paralelni pozivi koriste zasebne `PredictionEngine` instance iz poola. Lokalna odluka o odobrenju/rollbacku odmah invalidira cache; drugi proces se otkriva periodičkom provjerom identiteta/hash vrijednosti (zadano svake sekunde). Neuspjela provjera ne koristi zastarjeli cache. Runtime zahtijeva točan instrument, strategiju, feature verziju i aktualni pattern/label ugovor. Tijekom zamjene već započeta inferencija može završiti na prethodnoj verziji, a idući poziv učitava novu.
- Migracija 27 dodaje audit tablicu i uvjetno stvara `NumericalModelRecords` ako ga nema. To zatvara postojeću rupu u kojoj stara `AddNumericalModels` klasa nije registrirana kao EF migracija; već postojeća model tablica ne briše se niti popunjava iznova. Operater treba pregledati i arhivirati stvarnu bazu prije primjene migracija; ova točka testira migraciju samo na privremenoj bazi.
- Release build prolazi bez upozorenja; 376/376 solution testova prolazi sa službenim IBKR DLL-om i u fallback buildu. Testovi provjeravaju usporedno predviđanje bez ponovnog učitavanja za svaki signal, promjenu odobrenog modela u drugom procesu, rollback, odbijanje, audit, migraciju te nekompatibilnu feature/label konfiguraciju. CLI `migrate` i `list` provjereni su na privremenoj SQLite bazi. Stvarna tržišna evaluacija/trening nad korisničkim podacima i IBKR paper end-to-end nisu provedeni.

## Točka 28 — onboarding novog instrumenta

- Konfiguracijski registry pri pokretanju dodaje enabled instrument u `BackfillPending`; collection i historical backfill rade neovisno o trading dozvoli. Backfill nastavlja trajne segmente nakon restarta. Rollout monitor sada prepoznaje već završene jobove i nastavlja `Backfilling` → `Collecting` čak i ako je proces stao između spremanja zadnjeg segmenta i registry prijelaza. Završeni automatski gap-fill ponovno gradi gap report i osvježava status backfill joba.
- `Collecting` prelazi u `WarmingUp` tek kada svi konfigurirani timeframe jobovi završe bez prijavljenih rupa. `ResearchReady` traži još minimalno 50 zdravih finalnih svijeća po timeframeu i 100 potpunih aktualnih `labels-v2` labela po strategiji; pragovi su podesivi u `InstrumentRollout` konfiguraciji. Ne traži odobren model. `GET /api/rollout?instrumentId=...&count=1` vraća konkretne razloge čekanja za povijest, praznine, svijeće, labele, evaluaciju, model i shadow uzorak.
- `ResearchReady` prelazi u `ShadowReady` uz zdrav feed i dovršenu evaluaciju kompatibilnu s aktualnim feature/pattern/label/evaluation ugovorom. `PaperReady` dodatno traži odobren model za svaku konfiguriranu strategiju, valjan hash spremljenog artefakta i dovoljan broj shadow odluka nastalih nakon ulaska u shadow status. Gubitak research podataka vraća `ShadowReady` u `ResearchReady`; gubitak modela ili kritično stanje na paper/live statusu pokreće postojeću suspenziju. Ručno live odobrenje i dalje zahtijeva zasebne paper metrike i eksplicitno dopuštenje.
- Restore i Release build sa službenim IBKR DLL-om prolaze bez upozorenja; 383/383 lokalnih testova prolazi i u Release i u fallback Debug načinu. Dodani testovi pokrivaju restart checkpoint, praznine i osvježavanje gap reporta, manjak labela/evaluacije/modela te shadow odluke prije statusnog prijelaza. Ova provjera ne pokreće bot niti šalje naloge. Backfill bez rupa ovisi o brokerski dostupnoj povijesti; povijesne rupe koje automatski gap-fill ne popravi zadržavaju instrument u čekanju. Evaluacija, trening i odobrenje modela ostaju eksplicitni operatorov ModelOps postupak. Stvarni IBKR paper test ostaje točka 31.

## Točka 29 — automatizirani end-to-end test

- `PaperFlowEndToEndTests` koristi privremeni SQLite i deterministički broker. Prolazi stvarni registry/backfill/rollout do `PaperReady`, računa feature i pattern iz spremljenih svijeća, koristi lokalno LightGBM predviđanje iz sintetičkog odobrenog artefakta te stvarne strategy, risk, arbitration, order i exit servise. Test ne pokreće Web host ni stvarnog IBKR adaptera. Model/trening skup su sintetički i ne potvrđuju tržišnu prednost.
- Scenariji pokrivaju stop-loss, take-profit, vremenski izlaz s potvrđenim otkazivanjem child naloga, djelomični BUY i otkazivanje ostatka, odbijeni BUY, prekid nakon prihvata naloga, restart order/exit servisa i istodobne signale SPY/QQQ na risk/arbitration granici. Nakon zatvaranja provjeravaju se trajni nalozi, broker neto pozicija, virtualna alokacija, trade, execution/audit trag te oslobođena rezervacija i nulta zauzetost kapitala. Nepoznat broker ishod čuva rezervaciju i blokira duplicirani BUY pri novom order manageru.
- Integracijski test otkrio je i ispravio dva lifecycle propusta: zbroj izlaznih fillova sada uključuje upravo primljeni fill prije spremanja, a potpuno zatvaranje oslobađa portfolio rezervaciju. Kod djelomičnog entryja potvrda otkazivanja ostatka više ne pokušava ponovno mijenjati otkazanu zaštitu niti označava već ravnu poziciju kao neispravnu zbog sibling-cancel potvrde.
- Lokalni Release solution testovi: 391/391. Ovi testovi provjeravaju servisne granice uz kontrolirane callbackove. Ne mjere stvarni IBKR callback/OCA redoslijed, tržišnu latenciju, trošak ili fill kvalitetu; točka 31 ostaje obvezna stvarna paper provjera, a točka 25 i dalje ima zasebne replay praznine.

## Točka 30 — shadow provjera i performanse (u tijeku)

- Dodani su read-only `TradingBot.ShadowProbe` i [operativni protokol](SHADOW_READINESS.md) s unaprijed zadanim pragovima za trajanje, tick pokrivenost, p95 lag, odbijanja, starost/obradu signala, red, očekivani neto edge i model naspram determinističkog baselinea. SQLite procjena otvara bazu samo za čitanje i uparuje scalping odluke s timingom i odobrenim risk auditom po signal ID-u.
- Lokalni test opterećenja provjerava 900 kandidata kroz tri instrumenta i tri strategije u bounded pattern kanalu bez gubitka. Ako publish kandidata bude odbijen, engine prelazi u `Degraded`, trgovanje se isključuje i kandidat se označava blokiranim u research auditu.
- Read-only TWS API dijagnostika 2026-10-07 potvrdila je vezu (server version 225), ali za SPY i QQQ nije primila tickove. Brokerske greške su 10089 i 10189. Rezultat je [spremljen](shadow30-feed-diagnostic-2026-10-07.json). Treba omogućiti/provjeriti real-time API market-data prava i eventualnu konkurentnu sesiju, zatim prikupiti pun AnalysisOnly uzorak. Stvarni latency, očekivani edge i kontinuitet kontrola nisu potvrđeni; točka 30 ostaje otvorena.
- Ponovljena SPY proba s drugim API client ID-om opet je dala 10089. Zasebni test izravno sa službenim `CSharpAPI.dll`, bez botovog adaptera, potvrdio je jedinstvene SPY/QQQ ugovore, ali su i `reqMktData` i `reqTickByTickData(AllLast)` za oba simbola vratili 10089 i nula podataka. To upućuje na IBKR market-data pravo/sesiju, ne na našu obradu tick callbacka. Detalji su u [shadow protokolu](SHADOW_READINESS.md).
- Tijekom provjere Client Portala izravni `SPY@BATS` zahtjev prijavio je 10197 (konkurentna sesija); nakon odjave ta je greška nestala. Međutim, službeni API za BATS i dalje nije vratio cijene (10089) ni tickove (10189), a botov SMART read-only probe i dalje je primio nula tickova uz 10089. [Izvještaj nakon odjave](shadow30-after-session-close-2026-10-07.json) potvrđuje da je sesija bila jedan uzrok, ali nije preostala prepreka. Paper sharing je uključen; potrebna je IBKR potvrda API market-data prava za planirane instrumente/ugovore prije pretplate i ponovne pune provjere.
- Nakon potpisa `Market Data API Acknowledgement` i potvrde `Non-Professional` statusa, novi read-only paper TWS probe povezao se (server version 225), ali u 25 sekundi nije primio nijedan SPY/QQQ tick. SPY je dobio 10089, QQQ 10189; [izvještaj](shadow30-after-api-ack-2026-10-07.json). Potpis i otklanjanje konkurentne sesije nisu bili dovoljni za tražena real-time API prava. Točka 30 ostaje otvorena do aktivnog feeda i punog shadow uzorka.
- Isključen `Read-Only API` u TWS-u nije promijenio rezultat: novi 30-sekundni paper probe dobio je nula tickova i 10089 za oba simbola; [izvještaj](shadow30-after-readonly-off-2026-10-07.json). To pokazuje da prava za market data ostaju zaseban problem; nalog nije poslan.
- Kontrolni read-only test izravno preko službenog API-ja potvrdio je da EUR/USD forex feed radi (52 price callbacka, 549 bid/ask tickova u 30 s), dok SPY@BATS u istoj vezi nije dao podatke (10089/10189). TWS/API veza je funkcionalna, ali postojeći bot obrađuje konfigurirane dioničke ugovore i ovaj FX uzorak ne validira njegov SPY/QQQ signalni ni order ciklus; detalji su u [shadow protokolu](SHADOW_READINESS.md).
- Restore i Release solution testovi sa službenim IBKR DLL-om: 395/395. Fallback Debug build bez DLL-a: 0 upozorenja i 0 grešaka; fallback testovi: 395/395. Ove lokalne provjere nisu zamjena za stvarni 30-minutni feed i shadow uzorak.
