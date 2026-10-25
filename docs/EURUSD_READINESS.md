# EUR/USD — priprema nastavka

Status 25.10.2026.: **FX-1 i FX-2 konfiguracijski temelj su provedeni**, ali postojeći bot još nije spreman za EUR/USD podatkovni tok ili paper naloge. Do FX-3/FX-4 forex konfiguracija mora imati `Enabled=false` i `TradingEnabled=false`; primjer ostaje u `AnalysisOnly` načinu. Ovaj dokument ne potvrđuje profitabilnost strategije ni stvarne IBKR fillove.

## Što je već provjereno

- Neovisni read-only poziv službenim `CSharpAPI.dll` potvrdio je `EUR.USD CASH IDEALPRO` bid/ask tok u paper TWS vezi: 549 bid/ask tickova i 52 price callbacka u 30 sekundi. To potvrđuje dostupnost FX podataka na toj vezi, ne botovu FX obradu, trade tickove, povijesni backfill ni naloge. Detalji su u [shadow protokolu](SHADOW_READINESS.md).
- Točka 25 sada može reproducirati kratke signale s research zapisima i labelama čiji horizont počinje tek kada je signal dostupan. Time je uklonjen vremenski leakage koji bi iskrivio EUR/USD istraživanje. Virtualni broker i dalje ima dionički ugovor troška i ne treba ga primijeniti na FX bez prilagodbe.
- Trenutna konfiguracija instrumenta već ima polja `SecurityType`, `Exchange` i `Currency`, pa nije potrebna velika rekonstrukcija domenskog registra.

## Dogovoreni redoslijed FX nadogradnje

Svaka točka završava odgovarajućim testovima, dokumentacijom i zasebnim commitom prije odobravanja sljedeće. Zadržavaju se .NET, Blazor, SQLite/EF Core, Parquet, ML.NET/LightGBM, postojeći risk, arbitraža i lifecycle. SPY i ostali instrumenti ostaju podržani; FX je dodatni konfiguracijski izbor, bez globalne zamjene dioničkih pravila.

| Točka | Opseg i kriterij prihvata | Status |
| --- | --- | --- |
| FX-1 — Inventar i regresijska zaštita | Popis stvarno aktivnih patterna, featurea, strategija i izlaza; stock baseline i testovi prije FX izmjena. [Matrica](STOCK_COMPATIBILITY_BASELINE.md). | Implementirano i lokalno provjereno; rezultati u statusu implementacije |
| FX-2 — Konfiguracija instrumenata | Tip instrumenta, osnovna i kotirana valuta, stabilan identitet para, odvojene strategije/smjerovi/timeframeovi/limiti. Stara stock konfiguracija ostaje kompatibilna; različiti parovi s istom osnovnom valutom ne kolidiraju. | Implementirano i lokalno provjereno: 448/448 testova. Forex aktivacija još blokirana. |
| FX-3 — IBKR ugovori i dijagnostika | CASH/IDEALPRO ugovor iz konfiguracije, contract details i precizna dijagnostika; SPY zadržava STK/SMART. Read-only provjera bez naloga. | Planirano |
| FX-4 — Prikupljanje i backfill | FX sesija i dostupni quote/history izvori, trajna pohrana s podrijetlom, gapovi i neovisnost o dopuštenju trgovanja. Ne izmišljati trade volumen. | Planirano |
| FX-5 — Kratki agregati i featurei | Definirani quote-derived barovi, as-of i warmup pravila, dostupnost featurea i verzije; jednakost live/replay na istom inputu. | Planirano |
| FX-6 — Patterni i bodovanje | Ponovno koristiti geometriju, uvesti FX pragove i eksplicitna pravila za nedostupan volumen/VWAP; sačuvati stock scoreove. Bilježiti i odbijene kandidate. | Planirano |
| FX-7 — Strategije i signali | Isti strategijski kod konfigurirati po paru/profilu, uz izolirane pragove, cooldown i odobrenja; provjeriti istodobni SPY i EUR/USD tok. | Planirano |
| FX-8 — Veličina, risk i troškovi | Količina u osnovnoj valuti, brokerovi koraci/minimumi, pip/tick preciznost, provizije, valutno preračunavanje i izloženost. Mali račun nije izuzeće od gateova. | Planirano |
| FX-9 — Nalozi i pozicije | FX entry/fill/partial/cancel/exit/restart/reconciliation u postojećem lifecycleu; oba FixedBracket i AtrTrailing profila, neovisni sekundni timer. Short izvršavanje nije automatski uključeno. | Planirano |
| FX-10 — Replay, labele i modeli | FX izvršenje i troškovi, SL/TP/timer labele, vremenska evaluacija bez leakagea, dataset/model verzije i operativno odobrenje po paru/strategiji. Preostali produkcijski replay paritet pratiti i kroz točku 25. | Planirano |
| FX-11 — Onboarding, UI i shadow | Automatsko prikupljanje/backfill, odvojeni research/shadow/paper uvjeti, točne valutne jedinice u UI-ju, fake-broker E2E i mješovita regresija te AnalysisOnly mjerenje. | Planirano |
| FX-12 — Stvarni IBKR paper E2E | Zasebno odobren ograničen test stvarnih callbackova: entry, zaštita, izlaz i usklađivanje; trajni dokaz rezultata i preostalih ograničenja. | Planirano |

Price-based patterni i strategijski kod ponovno se koriste gdje ulazni ugovor odgovara. To ne znači preslikavanje SPY volumena, pragova ili odobrenog modela na FX. Funkcionalnosti koje trebaju nedostupne podatke moraju imati jasan razlog blokade ili zasebno verzioniranu FX alternativu, bez uklanjanja njihove dioničke verzije.

## FX-2 — konfiguracija i identitet

Primjer [forex-instruments.example.json](../TradingBot.Web/forex-instruments.example.json) sadrži SPY, EUR/USD i EUR/GBP. To je primjer, ne automatski učitana konfiguracija; stvarni `appsettings.json` nije promijenjen. Kod primjene u postojeću konfiguraciju treba zadržati ostale instrumente i urediti cijeli `TradingSettings:Instruments` popis. .NET konfiguracijski provideri spajaju nizove po indeksima, pa se primjer ne smije naslijepo dodati kao djelomični override postojećeg popisa.

- `SecurityType` je jedini konfiguracijski odabir tipa: `STK` → `InstrumentType.Stock`, `CASH` → `InstrumentType.Forex`. Nema drugog neovisnog polja tipa koje bi moglo proturječiti brokerskom tipu.
- Za forex su obvezni `BaseCurrency` (npr. `EUR`), postojeći `Currency` kao kotirana valuta (npr. `USD`) i `Exchange=IDEALPRO`. Valute moraju imati tri ASCII slova i biti različite. Ovo je provjera strukture, ne potvrda da broker podržava svaki takav kod.
- `Symbol` je interna oznaka cijelog para: `EURUSD`. Može se izostaviti i izvesti iz valuta; `EUR/USD` i `EUR.USD` normaliziraju se na isti par. Sam `EUR` nije dovoljan. `ConfiguredInstrument.BrokerSymbol` zasebno vraća `EUR` za budući FX adapter; SPY i dalje vraća `SPY`. `DisplaySymbol` prikazuje `EUR/USD`, a `QuoteCurrency` izlaže postojeći `Currency`.
- `InstrumentId` ostaje eksplicitni, stabilni identitet, npr. `FX-CASH-EUR-USD-IDEALPRO`. Isti brokerski ugovor ne smije se duplicirati pod drugim ID-jem. Interni simboli ostaju jedinstveni dok ostatak toka ima symbol-based pozivatelje; EUR/USD i EUR/GBP zato mogu zajedno postojati. Signal ID uključuje instrument i strategiju.
- Svaki FX zapis zahtijeva eksplicitni `StrategyIds`; ne nasljeđuje stock strategiju. Smjerovi, timeframeovi i limiti ostaju zasebni po instrumentu. Izostavljena stock strategija i smjer i dalje znače `deterministic-patterns` i `Long`. Izričito navedeni nizovi više ne dobivaju dopisane zadane elemente pri JSON bindingu. Time se uklanja i postojeća lažna greška duplicirane zadane SPY strategije.
- Registry sprema normalizirani par u postojeća polja `Symbol`, `Currency`, `SecurityType` i `Exchange`, bez migracije baze. Čitanje nakon restarta vraća tip i obje valute. Stock configuration hash ostaje nepromijenjen. Promjena para ili prijelaz stock/FX pod postojećim FX identitetom odbija se prije upisa; za drugi ugovor treba novi ID kako se ne bi pomiješali povijest i modeli.
- Privremena FX-2 zaštita odbija `Enabled=true` ili `TradingEnabled=true`, i kroz options validaciju i pri normalizaciji za izravne pozivatelje. Time CASH ne može slučajno završiti u postojećem STK adapteru. FX-3/FX-4 trebaju zamijeniti samo blokadu prikupljanja provjerenim FX putem; dopuštenje trgovanja ostaje zasebno do kasnijih točaka. Postojeće SPY prikupljanje radi neovisno o trading dopuštenju.

## Utvrđene tehničke obveze za FX-3 do FX-12

1. U brokerskom adapteru graditi ugovor iz konfiguriranog instrumenta. Sada `IbkrBrokerService.BuildStockContract` za podatke, povijest, detalje ugovora i naloge uvijek stvara `STK/SMART/USD`. Za EUR/USD treba eksplicitno `Symbol=EUR`, `SecType=CASH`, `Exchange=IDEALPRO`, `Currency=USD`, uz provjeru jednog točnog ugovora i zaštitu od zamjene simbola. `IbkrDiagnosticsService` sada kao podržan prepoznaje samo `STK/SMART/USD`.
2. Uvesti FX pravila za količinu u osnovnoj valuti, minimalnu/promjenjivu veličinu naloga, preciznost cijene, USD protuvrijednost, bid/ask smjer i proviziju; potvrditi ih iz paper contract details i stvarnih callbackova prije slanja naloga. Postojeći `PositionSizer` i replay trošak `CommissionPerShare` pretpostavljaju dionice. FX trošak i risk moraju biti izraženi dosljedno u USD i provjereni na primjeru malog računa, bez pretpostavke da se bilo koja veličina može poslati.
3. Odvojiti FX tržišnu sesiju od sadašnjeg američkog dioničkog rasporeda koji koriste tick i bar prikupljanje te signalni gate. Prikupljanje treba raditi i kada je trading gate zatvoren. Spremanje, backfill, kratki agregati i replay trebaju razlikovati FX bid/ask tok od dioničkih trade printova; ako nema pouzdanog trade ticka, ne smiju sintetizirati volume ni fill iz bid/ask quotea. Treba eksplicitno odrediti koje se strategije i indikatori mogu računati iz quote-derived barova.
4. Dodati zasebne EUR/USD strategijske pragove za spread, volatilnost, SL/TP i trošak, s provjerom live/replay feature jednakosti i vremenskom evaluacijom modela. Isti strategijski kod može se konfigurirati za više instrumenata, ali model, pragovi i odobrenje trebaju biti vezani uz instrument/strategiju i verziju skupa.
5. Provesti deterministički FX replay cijelog entry/fill/exit ciklusa s FX količinom i troškom, zatim automatizirane fake-broker testove, AnalysisOnly shadow uzorak i na kraju zasebno odobren mali IBKR paper test. Ne proglašavati točke 25, 30 ili 31 dovršenima na temelju samog FX feeda.

Sljedeća zasebna točka je FX-3: stvarni IBKR ugovori i dijagnostika iz normalizirane konfiguracije. Read-only contract/data tok dovršava se kroz FX-3/FX-4 uz `TradingEnabled=false` i testove koji dokazuju da SPY ostaje dionički ugovor, a EUR/USD forex ugovor. Nalog se ne šalje u toj fazi.
