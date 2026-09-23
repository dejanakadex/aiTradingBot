# Plan nastavka — v2

Status: **prijedlog za potvrdu**. Dosad odobreno i napravljeno: pregled, čišćenje i zajednički branch `trading-bot-v2`. Funkcionalna implementacija počinje nakon potvrde ovog plana.

Napredak 2026-09-23: dodani su .NET 10 LTS, `global.json`, GitHub Actions i Dependabot. CI Release build prolazi bez upozorenja i grešaka, uz 195/195 prolaznih testova. U fazi 0 ostaje provjera conditional IBKR adaptera sa službenim `CSharpAPI.dll`.

Osnova: [detaljni pregled i nalazi R01–R16](PROJECT_REVIEW.md). Sve faze koriste isti branch, uz male opisne commitove. Potvrda plana ne uključuje automatsko uključivanje live trgovanja.

## Cilj

Pouzdan SPY day-trading sustav u postojećoj .NET arhitekturi: jedan konzistentan lifecycle pozicije, provjerljivi podaci, deterministički rizik i izvršavanje, reproducibilna evaluacija te izmjerena korist svakog modela. AI izlaz ne može promijeniti limite ili preskočiti broker provjere.

Predloženi razvojni smjer: podaci → featurei → jednostavan numerički baseline → risk/izvršavanje. Postojeći patterni ostaju usporedni baseline i mogući featurei. LLM se evaluira kao sloj tržišnog konteksta/objašnjenja s jasno izmjerenim troškom i kašnjenjem. Konačna zamjena sadašnjeg analyzer/critic toka ovisi o rezultatima evaluacije.

## Faze i kriteriji završetka

| Faza | Rad | Kriterij završetka |
| --- | --- | --- |
| 0 — Ponovljiv razvoj | SDK/toolchain, build/test baseline, CI, zasebna provjera conditional IBKR adaptera i konfiguracije. | Clean checkout se reproducibilno gradi; zabilježeni stvarni rezultati testova i točna broker DLL verzija. |
| 1 — Risk i lifecycle naloga | R01–R08: povijest risk engineu, sizing, durable intent, partial fills, stop/exit, cancellation, reconnect, UI kontrole. | Automatizirani testovi prekida/duplikata; jedan trade intent stvara najviše jedan ulaz; izlazi ne prelaze preostalu poziciju; pause/kill preživljava reconnect. |
| 2 — Pouzdani tržišni podaci | R09–R10 i R13: timestampovi, quote freshness, bid/ask i trades, canonical barovi, sesije, jedinstveni featurei, event-time snapshot. | Svaki signal moguće reproducirati isključivo podacima dostupnim u trenutku odluke; praznine/stari feed blokiraju ulaz uz jasan razlog. |
| 3 — Ledger, dataset i replay | R11–R12: trade/execution/provizija veza, post-trade zapisi; spremanje svih opažanja i odbijenih kandidata; simulator troškova. | Replay daje iste odluke za iste ulaze; količina i P/L usklađeni s executionima; bez budućih podataka u featureima. |
| 4 — Numerički baseline | Jednostavni deterministički/statistički baselinei, zatim kandidat LightGBM; verzionirani feature/label/model ugovori i inferencija iz .NET-a. | Vremenski odvojena evaluacija uz troškove, stabilnost kroz više perioda i usporedba s postojećim pattern/LLM pristupom. Ne prelazi dalje samo zbog visokog win ratea. |
| 5 — Shadow i paper | Model prvo samo zapisuje odluke, zatim nadzirani paper; mjerenje latencije, izvršenja, odstupanja i oporavka. | Unaprijed potvrđen protokol prolazi; nema nerazriješenih order/position mismatcha; operativne kontrole dokazano rade. |
| 6 — Daljnji modeli i podaci | L2/order-book featurei samo ako kvaliteta i evaluacija opravdaju trošak; složeniji modeli tek nakon stabilnog baselinea. | Mjerljivo poboljšanje na netaknutom test razdoblju uz prihvatljiv operativni trošak. DeepLOB/RL nisu početni zadatak. |

## Faza 1: konkretan prvi paket implementacije

### 1A — Risk i račun

- Uvesti jedinstveni account/operating-mode context.
- Dohvatiti trade ledger za jasno definirani trading dan i spojiti ga na produkcijski risk poziv.
- Popraviti nulti kapacitet sizer-a i definirati pravila količine/tick-sizea.
- Uvesti rezervaciju izloženosti između odobrenja i potvrde entryja; otvoreni ostatak naloga ulazi u rezervaciju.
- Testirati dnevni loss/trade/cooldown limit kroz cijeli pipeline, a ne samo izolirani engine.

### 1B — Order i exit stanje

- Persistirati intent prije slanja brokeru; stabilan poslovni ID ne ovisi o novom vremenu retryja.
- Uskladiti executions, order statuse i provizije; callbackovi su idempotentni i mogu stići bilo kojim redoslijedom.
- Za ATR i fixed-bracket put koristiti isti trade ledger i dosljedan lifecycle preostale količine.
- Registrirati zaštitu ranih/djelomičnih fillova; potvrđivati stvarnu broker zaštitu.
- Uvesti `ExitPending`, praćenje timeout/ručnog izlaza i koordinaciju sa stopom.
- Modelirati pending cancel/modify i broker odbijanje; ne proglašavati uspjeh samo zato što je zahtjev poslan.
- Zaključavanje ili serijalizaciju vezati uz jednu poziciju; modelirati crash recovery.

### 1C — Operativne kontrole

- Odvojiti readiness od trajne dozvole za nove ulaze.
- Pause zaustavlja nove ulaze uz nastavak upravljanja postojećom pozicijom.
- Close šalje i prati idempotentno zatvaranje preostale pozicije.
- Kill ima dokumentiranu politiku za nove naloge, otvorene entryje i postojeće pozicije; odabranu politiku testirati.
- Promjenu paper/live konteksta povezati s reconnectom i novim reconciliationom.

## Podatkovni i modelni ugovori

Prije ML implementacije definirati:

- `EventId`, instrument, vrijeme izvora/primitka, sesiju i verziju izvora.
- Bid/ask cijenu i količine, trade cijenu/količinu te closed-bar intervale; L2 je zaseban opcionalni skup.
- Verziju featurea, cutoff vrijeme i status kvalitete/missing podataka.
- Svaki kandidat i razlog odbijanja, uključujući odluke bez tradea; ne trenirati samo na izvršenim ili pobjedničkim tradeovima.
- Labele s unaprijed odabranim horizontima i realističnim entry/exit pravilima, fee/spread/slippage pretpostavkama i jasnim vremenom nastanka labele.
- Walk-forward podjelu po vremenu, razmak za preklapajuće labele i netaknuti završni test; preprocessing i odabir parametara rade se samo na train dijelu.
- Evaluaciju neto rezultata, drawdowna, turnovera, broja tradeova, stabilnosti po sesijama i osjetljivosti na troškove. Ne zaključivati da rezultat jamči budući profit.
- Model artifact/verziju i kompatibilan inference ugovor s .NET-om; odabrati način exporta/servinga nakon malog compatibility testa.

## Odluke za potvrdu uz plan

Predložene početne postavke projekta, uz mogućnost korekcije prije implementacije:

1. **Redoslijed:** prvo faze 0–1; početno zadržati postojeću strategiju dok se popravljaju sigurnost i evidencija.
2. **Pozicije:** jedan long SPY trade od ulaza do potpunog izlaza; bez shorta, pyramidinga i automatskog povećanja gubitničke pozicije u početnoj verziji.
3. **Izlazna politika:** definirati dopušten gubitak, zaštitni stop, maximum holding i ponašanje na kraju sesije. Trenutni kod već dopušta izlaz s gubitkom; pravilo „nikad prodati s gubitkom” nije spojivo s bezuvjetnim zaštitnim izlazom. To ne mijenjati prešutno.
4. **Kill politika:** predlaže se zabrana novih ulaza i otkazivanje preostalih entryja, uz nastavak zaštite postojeće pozicije; eksplicitni Close zasebno provodi izlaz. Prije koda potvrditi željenu semantiku hitnog zatvaranja.
5. **Dataset/model:** nakon stabilizacije prikupljati quote/trade podatke i sve kandidate; jednostavan baseline pa LightGBM kandidat, uz postojeći pattern sustav za usporedbu.

Za početak implementacije dovoljna je potvrda redoslijeda i eventualne korekcije ovih odluka. TWS paper dostupnost, službeni CSharpAPI DLL i sample feed trebat će za stvarnu adapter verifikaciju; API ključeve i račune postavljati lokalno kroz konfiguraciju/environment, bez upisa u Git.
