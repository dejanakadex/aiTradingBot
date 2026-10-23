# EUR/USD — priprema nastavka

Status 23.10.2026.: **može početi implementacija**, ali postojeći bot još nije spreman za EUR/USD paper naloge. Do tada `Trading:OperatingMode` ostaje `AnalysisOnly`, a za novi instrument `TradingEnabled=false`. Ovaj dokument ne potvrđuje profitabilnost strategije ni stvarne IBKR fillove.

## Što je već provjereno

- Neovisni read-only poziv službenim `CSharpAPI.dll` potvrdio je `EUR.USD CASH IDEALPRO` bid/ask tok u paper TWS vezi: 549 bid/ask tickova i 52 price callbacka u 30 sekundi. To potvrđuje dostupnost FX podataka na toj vezi, ne botovu FX obradu, trade tickove, povijesni backfill ni naloge. Detalji su u [shadow protokolu](SHADOW_READINESS.md).
- Točka 25 sada može reproducirati kratke signale s research zapisima i labelama čiji horizont počinje tek kada je signal dostupan. Time je uklonjen vremenski leakage koji bi iskrivio EUR/USD istraživanje. Virtualni broker i dalje ima dionički ugovor troška i ne treba ga primijeniti na FX bez prilagodbe.
- Trenutna konfiguracija instrumenta već ima polja `SecurityType`, `Exchange` i `Currency`, pa nije potrebna velika rekonstrukcija domenskog registra.

## Što se mora napraviti u EUR/USD implementaciji

1. U brokerskom adapteru graditi ugovor iz konfiguriranog instrumenta. Sada `IbkrBrokerService.BuildStockContract` za podatke, povijest, detalje ugovora i naloge uvijek stvara `STK/SMART/USD`. Za EUR/USD treba eksplicitno `Symbol=EUR`, `SecType=CASH`, `Exchange=IDEALPRO`, `Currency=USD`, uz provjeru jednog točnog ugovora i zaštitu od zamjene simbola. `IbkrDiagnosticsService` sada kao podržan prepoznaje samo `STK/SMART/USD`.
2. Uvesti FX pravila za količinu u osnovnoj valuti, minimalnu/promjenjivu veličinu naloga, preciznost cijene, USD protuvrijednost, bid/ask smjer i proviziju; potvrditi ih iz paper contract details i stvarnih callbackova prije slanja naloga. Postojeći `PositionSizer` i replay trošak `CommissionPerShare` pretpostavljaju dionice. FX trošak i risk moraju biti izraženi dosljedno u USD i provjereni na primjeru malog računa, bez pretpostavke da se bilo koja veličina može poslati.
3. Odvojiti FX tržišnu sesiju od sadašnjeg američkog dioničkog rasporeda koji koriste tick i bar prikupljanje te signalni gate. Prikupljanje treba raditi i kada je trading gate zatvoren. Spremanje, backfill, kratki agregati i replay trebaju razlikovati FX bid/ask tok od dioničkih trade printova; ako nema pouzdanog trade ticka, ne smiju sintetizirati volume ni fill iz bid/ask quotea. Treba eksplicitno odrediti koje se strategije i indikatori mogu računati iz quote-derived barova.
4. Dodati zasebne EUR/USD strategijske pragove za spread, volatilnost, SL/TP i trošak, s provjerom live/replay feature jednakosti i vremenskom evaluacijom modela. Isti strategijski kod može se konfigurirati za više instrumenata, ali model, pragovi i odobrenje trebaju biti vezani uz instrument/strategiju i verziju skupa.
5. Provesti deterministički FX replay cijelog entry/fill/exit ciklusa s FX količinom i troškom, zatim automatizirane fake-broker testove, AnalysisOnly shadow uzorak i na kraju zasebno odobren mali IBKR paper test. Ne proglašavati točke 25, 30 ili 31 dovršenima na temelju samog FX feeda.

Prvi implementacijski korak je read-only FX instrument/contract/data tok uz `TradingEnabled=false` i testove koji dokazuju da SPY ostaje dionički ugovor, a EUR/USD forex ugovor. Nalog se ne šalje u toj fazi.
