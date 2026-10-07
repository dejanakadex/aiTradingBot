# Točka 30 — shadow mjerenje

Status: **u tijeku**. Lokalna mjerenja i provjere mehanike postoje, ali stvarni tržišni uzorak i završna procjena odluka nisu prikupljeni. Ovaj dokument ne odobrava paper ni live naloge.

## Unaprijed definirani kriteriji

Za svaki instrument traži se najmanje 30 minuta opažanja, po 1000 trade, bid i ask tickova, p95 kašnjenja od brokerskog vremena do primitka najviše 1500 ms, bez izgubljenih ili vremenski neurednih tickova i bez IBKR market-data grešaka. Brojevi su početni operativni pragovi koje treba kalibrirati na stvarnom uzorku, ne tvrdnja o tržišnoj prednosti.

Za svaku kombinaciju instrumenta i strategije traži se najmanje 100 završnih scalping execution odluka, barem jedna odobrena odluka, potpuna signal timing pokrivenost, p95 starosti signala najviše 1500 ms, p95 obrade signala najviše 1000 ms, p95 starosti quotea najviše 1500 ms, dubina čekajućeg pattern reda najviše 192 (zadani kapacitet 256), prosječni očekivani neto edge odobrenih odluka barem 3 bps, odobrena risk audit odluka za svaki odobreni signal i veći holdout expectancy odobrenog modela od determinističkog baselinea. U istom prozoru ne smije biti quality incidenata, tick gapova, izgubljenih tickova ni broker order zapisa. Holdout usporedba je povijesna modelna provjera, a očekivani neto edge je procjena prije izvršenja; nijedno nije ostvareni P&L.

## Alati i doseg

`TradingBot.ShadowProbe` izravno čita službeni IBKR tick-by-tick adapter kroz zasebnu TWS API vezu. Nema put za slanje naloga. Primjer dijagnostičkog pokretanja iz repozitorija:

```powershell
dotnet run --project TradingBot.ShadowProbe -c Release -- --output "$env:TEMP\shadow-feed.json" --port 7497 --seconds 30 --symbols SPY,QQQ
```

Za puni 30-minutni uzorak koristite `--seconds 1800`; za procjenu operativnih odluka dodajte `--database` s putanjom do odgovarajuće SQLite baze. CLI bazu otvara samo za čitanje. Bez baze izvještaj ostaje nepotpun. `ShadowPerformance:Enabled` uključuje zapis trajanja pattern obrade, starosti signala i dubine čekajućeg reda u postojećoj `AiAnalysisRecords` tablici; zadano je isključeno. Signal age je razlika od detekcije do početka obrade i uključuje vrijeme u redu; dubina je snimka na početku obrade, a ne egzaktno trajanje čekanja. Rejection stage dolazi iz research kandidata.

`AnalysisOnly` može provesti strategy/risk/scalping execution gate nakon uspješnog reconciliationa i zabilježiti hipotetski plan. `TradingExecutionGuard` odbija brokersku predaju u tom načinu rada. Za stvarne shadow odluke ipak trebaju zdrav feed, spreman instrument, odobren kompatibilan model i ispunjeni ulazni uvjeti; sama dijagnostička API veza ništa od toga ne potvrđuje. Postojeći `PaperTrading` put može slati naloge i ovdje se ne koristi za mjerenje. Zadani `ShadowPerformance:Enabled` je `false`; za prikupljanje timing zapisa mora se uključiti u AnalysisOnly konfiguraciji.

## Zabilježena provjera 2026-10-07

TWS paper sesija prihvatila je API vezu na `127.0.0.1:7497` (server version 225). Izravni 15-sekundni read-only tick probe primio je nula trade/bid/ask tickova za SPY i QQQ; IBKR je prijavio 10089 za SPY i 10189 za QQQ. Izvještaj je u [shadow30-feed-diagnostic-2026-10-07.json](shadow30-feed-diagnostic-2026-10-07.json). Kratki dijagnostički prozor nije validacijski uzorak; nijedan feed prag nije prošao.

Ponovljeni read-only probe samo za SPY, s novim API client ID-om, opet je primio nula tickova i 10089. Zatim je privremeni **neovisni** C# test izravno koristio službeni `CSharpAPI.dll`, bez botovog adaptera: `reqContractDetails` za `STK/SMART/USD` vratio je točno jedan ugovor za SPY i jedan za QQQ; za oba su `reqMktData` (10 s) i `reqTickByTickData(AllLast)` (10 s) vratili 10089 i nula cijena/tickova. Naknadna greška 300 nastala je pri otkazivanju već odbijenog zahtjeva. Ovo reproducira problem izvan naše obrade callbackova i potvrđuje da simbol/ugovor nije uzrok. Pokušaj delayed `reqMktData` nije vratio cijene ni brokersku grešku u 10 s, pa iz njega ne donosimo zaključak.

U dostupnoj staroj Git grani `main` tržišni tok koristi `reqHistoricalData(..., keepUpToDate: true)`, ne WebSocket. Ako je raniji uspješni WebSocket bio Client Portal Web API ili drugi program, treba usporediti točan IBKR username, vrstu podataka (live/delayed) i ugovor; taj tok ne potvrđuje automatski pravo na TWS API tick-by-tick podatke.

Kasnija provjera otkrila je i odvojenu konkurentnu sesiju: izravni read-only `SPY@BATS` zahtjev vratio je 10197 uz 354/10189 dok je Client Portal bio otvoren. Nakon korisnikove odjave iz Portala, 10197 je nestao. Isti službeni API zatim je potvrdio `SPY@BATS` ugovor, ali `reqMktData` je i dalje vratio 10089, a `reqTickByTickData(AllLast)` 10189, bez podataka. Botov `SPY@SMART` read-only probe nakon odjave također je dobio nula tickova i 10089; [izvještaj](shadow30-after-session-close-2026-10-07.json). Paper market-data sharing postavljen je na `Yes`, ali aktivne pretplate prikazane u Client Portalu ne uključuju Network B/C L1, nego samo besplatni US non-consolidated feed i druge nepovezane pakete. Točna pretplata/API entitlement treba potvrdu IBKR-a prije plaćenog odabira; pokus ne dokazuje da kupnja pojedinog paketa sama otklanja sve prepreke.

Korisnik je zatim potpisao `Market Data API Acknowledgement` (portal prikazuje API pristup omogućenim) i potvrdio `Non-Professional` subscriber status. Nakon nove prijave u paper TWS i odjave iz Client Portala, službeni adapter se povezao sa server version 225, ali 25-sekundni read-only `SPY,QQQ@SMART` probe opet je primio nula tickova: SPY je dobio 10089, QQQ 10189. [Izvještaj nakon potpisa](shadow30-after-api-ack-2026-10-07.json). TWS je dodatno odbio `ReqNextValidId` kodom 321 jer je API u read-only načinu; to nije tržišni podatkovni zahtjev, a nalog nije poslan. Potpis obrasca stoga sam po sebi nije omogućio traženi tick-by-tick feed. Treba potvrditi i aktivirati odgovarajuća real-time API tržišna prava prije nastavka mjerenja.

Na korisnikov zahtjev ponovljen je isti probe nakon što je u TWS-u isključio `Read-Only API`. Veza na portu 7497 uspjela je; 30-sekundni `SPY,QQQ@SMART` uzorak opet je imao nula trade/bid/ask tickova, a oba su instrumenta vratila 10089. [Izvještaj nakon promjene](shadow30-after-readonly-off-2026-10-07.json). Read-only postavka zato nije bila prepreka za market data; treba je vratiti na uključeno tijekom daljnjih dijagnostičkih provjera. CLI nije poslao nijedan nalog.

Kontrolni read-only test sa službenim `CSharpAPI.dll` zatražio je `reqMktData` i `reqTickByTickData(BidAsk)` za `EUR.USD CASH IDEALPRO` i `SPY STK BATS` u istoj paper TWS vezi. U 30 sekundi EUR/USD je dao 52 pozitivna price callbacka i 549 bid/ask tickova, dok je SPY@BATS dao nula uz 10089 (`reqMktData`) i 10189 (`reqTickByTickData`). Nisu upućeni order zahtjevi. [IBKR navodi](https://www.interactivebrokers.com/docs/general/market-data-subscriptions/introduction) da za forex API podatke ne treba dodatna market-data pretplata. Ova provjera potvrđuje TWS/API vezu i primitak FX podataka; ne potvrđuje postojeći stock-only adapter, SPY/QQQ signalni tok, strategije ili paper order ciklus. Točka 30 i dalje traži pun uzorak na konfiguriranim instrumentima.

Za nastavak treba provjeriti IBKR real-time **API** market-data prava za oba ugovora i postoji li konkurentna sesija koja troši tick-by-tick dozvolu. Nakon što read-only probe počne primati oba toka, ponoviti puni uzorak i prikupiti AnalysisOnly odluke sa zabilježenim timingom. Lokalni bounded-channel test s tri instrumenta, tri strategije i 900 kandidata prolazi bez gubitka uz backpressure. Odbijeni pattern queue publish sada spušta engine u `Degraded` i blokira kandidata u research auditu umjesto tihog gubitka.
