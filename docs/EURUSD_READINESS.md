# EUR/USD — priprema nastavka

Status 25.10.2026.: **FX-1 priprema i regresijska osnova su provedene**, ali postojeći bot još nije spreman za EUR/USD paper naloge. Do tada `TradingSettings:OperatingMode` ostaje `AnalysisOnly`, a za novi instrument `TradingEnabled=false`. Ovaj dokument ne potvrđuje profitabilnost strategije ni stvarne IBKR fillove.

## Što je već provjereno

- Neovisni read-only poziv službenim `CSharpAPI.dll` potvrdio je `EUR.USD CASH IDEALPRO` bid/ask tok u paper TWS vezi: 549 bid/ask tickova i 52 price callbacka u 30 sekundi. To potvrđuje dostupnost FX podataka na toj vezi, ne botovu FX obradu, trade tickove, povijesni backfill ni naloge. Detalji su u [shadow protokolu](SHADOW_READINESS.md).
- Točka 25 sada može reproducirati kratke signale s research zapisima i labelama čiji horizont počinje tek kada je signal dostupan. Time je uklonjen vremenski leakage koji bi iskrivio EUR/USD istraživanje. Virtualni broker i dalje ima dionički ugovor troška i ne treba ga primijeniti na FX bez prilagodbe.
- Trenutna konfiguracija instrumenta već ima polja `SecurityType`, `Exchange` i `Currency`, pa nije potrebna velika rekonstrukcija domenskog registra.

## Dogovoreni redoslijed FX nadogradnje

Svaka točka završava odgovarajućim testovima, dokumentacijom i zasebnim commitom prije odobravanja sljedeće. Zadržavaju se .NET, Blazor, SQLite/EF Core, Parquet, ML.NET/LightGBM, postojeći risk, arbitraža i lifecycle. SPY i ostali instrumenti ostaju podržani; FX je dodatni konfiguracijski izbor, bez globalne zamjene dioničkih pravila.

| Točka | Opseg i kriterij prihvata | Status |
| --- | --- | --- |
| FX-1 — Inventar i regresijska zaštita | Popis stvarno aktivnih patterna, featurea, strategija i izlaza; stock baseline i testovi prije FX izmjena. [Matrica](STOCK_COMPATIBILITY_BASELINE.md). | Implementirano i lokalno provjereno; rezultati u statusu implementacije |
| FX-2 — Konfiguracija instrumenata | Tip instrumenta, osnovna i kotirana valuta, stabilan identitet para, odvojeni profili. Stara stock konfiguracija ostaje kompatibilna; različiti parovi s istom osnovnom valutom ne kolidiraju. | Planirano |
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

## Utvrđene tehničke obveze za FX-2 do FX-12

1. U brokerskom adapteru graditi ugovor iz konfiguriranog instrumenta. Sada `IbkrBrokerService.BuildStockContract` za podatke, povijest, detalje ugovora i naloge uvijek stvara `STK/SMART/USD`. Za EUR/USD treba eksplicitno `Symbol=EUR`, `SecType=CASH`, `Exchange=IDEALPRO`, `Currency=USD`, uz provjeru jednog točnog ugovora i zaštitu od zamjene simbola. `IbkrDiagnosticsService` sada kao podržan prepoznaje samo `STK/SMART/USD`.
2. Uvesti FX pravila za količinu u osnovnoj valuti, minimalnu/promjenjivu veličinu naloga, preciznost cijene, USD protuvrijednost, bid/ask smjer i proviziju; potvrditi ih iz paper contract details i stvarnih callbackova prije slanja naloga. Postojeći `PositionSizer` i replay trošak `CommissionPerShare` pretpostavljaju dionice. FX trošak i risk moraju biti izraženi dosljedno u USD i provjereni na primjeru malog računa, bez pretpostavke da se bilo koja veličina može poslati.
3. Odvojiti FX tržišnu sesiju od sadašnjeg američkog dioničkog rasporeda koji koriste tick i bar prikupljanje te signalni gate. Prikupljanje treba raditi i kada je trading gate zatvoren. Spremanje, backfill, kratki agregati i replay trebaju razlikovati FX bid/ask tok od dioničkih trade printova; ako nema pouzdanog trade ticka, ne smiju sintetizirati volume ni fill iz bid/ask quotea. Treba eksplicitno odrediti koje se strategije i indikatori mogu računati iz quote-derived barova.
4. Dodati zasebne EUR/USD strategijske pragove za spread, volatilnost, SL/TP i trošak, s provjerom live/replay feature jednakosti i vremenskom evaluacijom modela. Isti strategijski kod može se konfigurirati za više instrumenata, ali model, pragovi i odobrenje trebaju biti vezani uz instrument/strategiju i verziju skupa.
5. Provesti deterministički FX replay cijelog entry/fill/exit ciklusa s FX količinom i troškom, zatim automatizirane fake-broker testove, AnalysisOnly shadow uzorak i na kraju zasebno odobren mali IBKR paper test. Ne proglašavati točke 25, 30 ili 31 dovršenima na temelju samog FX feeda.

Nakon FX-1 sljedeća je zasebno odobrena FX-2 konfiguracija. Read-only instrument/contract/data tok uvodi se kroz FX-2 do FX-4 uz `TradingEnabled=false` i testove koji dokazuju da SPY ostaje dionički ugovor, a EUR/USD forex ugovor. Nalog se ne šalje u toj fazi.
