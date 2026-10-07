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

Za nastavak treba provjeriti IBKR real-time **API** market-data prava za oba ugovora i postoji li konkurentna sesija koja troši tick-by-tick dozvolu. Nakon što read-only probe počne primati oba toka, ponoviti puni uzorak i prikupiti AnalysisOnly odluke sa zabilježenim timingom. Lokalni bounded-channel test s tri instrumenta, tri strategije i 900 kandidata prolazi bez gubitka uz backpressure. Odbijeni pattern queue publish sada spušta engine u `Degraded` i blokira kandidata u research auditu umjesto tihog gubitka.
