# FX-12 — priprema stvarnog IBKR paper testa

25.10.2026.: **u tijeku; priprema implementirana, stvarni paper E2E nije proveden**. Početna osnova bila je čisti `cc1a831` u radnom worktreeu i na zajedničkom `trading-bot-v2`. Nije slan nalog, mijenjano operativno odobrenje niti uključen live trading. FX-12 i točke 25/30/31 ostaju otvorene.

## Utvrđeni preduvjeti

**Kasnija operativna dopuna istog dana:** nedostatak konfiguracije instrumenta i baze riješen je zasebnim [ForexCollection profilom](FOREX_COLLECTION.md). Kolektor je pokrenut na paper TWS-u i spremio je 1.425 povijesnih barova; postoji jedna označena praznina od 92 minute. Naknadni preflight čita cijelu popravljenu operativnu shemu i potvrđuje instrument/bazu, ali model, research, PaperReady i izvršni uvjeti ostaju neispunjeni. Naloga nije bilo. Sljedeći odlomak i raniji JSON ostaju vremenski označen dokaz prije te pripreme.

Read-only provjera zajedničkog `TradingBot.Web/appsettings.json` u 18:41 UTC našla je samo stock instrument, bez FX research/signal/execution profila. Konfiguracija je u AnalysisOnly, bez uključenog FX paper/model opt-ina. Na provjerenoj zadanoj lokaciji `TradingBot.Web/data/trading.db` nema operativne baze; provjera nije tražila ili stvarala alternativnu bazu na cijelom računalu. Ni zadana `data/trading.db` iz korijena checkouta nije pronađena. Nema pripremljenog produkcijskog EUR/USD modela i dokaza stvarnog model shadow toka u ovom checkoutu.

Prema aplikacijskom kalendaru FX sesija tada je zatvorena; sljedeće predviđeno otvaranje je 25.10.2026. u 21:15 UTC, odnosno 22:15 Europe/Zagreb. To nije potvrda brokerove raspoloživosti ni prazničnog rasporeda. Otvaranje sesije samo po sebi neće ukloniti preostale blokade. [Anonimizirani izvještaj](evidence/fx12-preflight-2026-10-25.json) bilježi stanje u trenutku provjere. Raniji FX-11 dokaz veze i 60 MIDPOINT barova ostaje povijesni dokaz, bez živih tickova/model odluka/fillova.

## Ponovljiva preflight provjera

Nova naredba samo čita eksplicitnu JSON konfiguraciju i postojeću SQLite bazu otvorenu u ReadOnly načinu. Ne spaja se na IBKR, ne pokreće host, ne radi migracije, ne odobrava model i ne šalje naloge. Ne spaja environment overrides ili user secrets: prije stvarnog testa treba zasebno provjeriti da konfiguracija pokrenutog hosta odgovara pregledanoj datoteci.

Iz korijena repozitorija, s apsolutnim lokalnim putanjama:

```powershell
dotnet run --project TradingBot.ModelOps -c Release --no-build -- fx-paper-preflight --db C:\PrivateGit\aiTradingBot\TradingBot.Web\data\trading.db --config C:\PrivateGit\aiTradingBot\TradingBot.Web\appsettings.json --instrument-id FX-CASH-EUR-USD-IDEALPRO --strategy-id eurusd-patterns --output C:\PrivateGit\aiTradingBot\TestResults\fx12-preflight.json
```

Izlazna mapa mora postojati, a izvještaj mora biti nov; postojeći dokaz se ne prepisuje. Izlazni kod aplikacije je 0 ako prođu sve offline provjere, 3 za nedostatne preduvjete, 1 za pogrešku naredbe. Omotač koji pokreće naredbu može prikazati drukčiji zbirni kod, pa treba čitati i JSON.

Provjeravaju se sesija, odsutnost live enablementa, USD-kotirani FX instrument/long strategija, izolacija drugih izvršnih instrumenata, istraživački ugovor i točna odobrenja signalnih verzija, izlazni profil/horizont, pregledane granice količine stare najviše 24 sata, postojeći PaperReady registry i rollout star najviše 60 sekundi, točno jedan kompatibilni odobreni model, hashovi modela/evaluacije/replaya i odsutnost aktivnih lokalnih rezervacija. Ovo je inventar dokaza, ne ponovno treniranje, potpuna provjera izvornog dataseta ili dokaz ispravnog izvršavanja model binarije.

`CanSendOrders` je **uvijek false**, uključujući uspješnu provjeru. Stvarni račun, quoteovi, ugovori, minimum/korak količine, price rules, margina, provizije i portfelj moraju se osvježiti kroz postojeći FX-9 put neposredno prije naloga. Preflight ne zamjenjuje runtime gateove niti jamči brokerovo prihvaćanje zaštite.

Pri pripremi je ispravljen ConfigurationBinder problem: vrijeme `ReviewedAtUtc` sa Z/offsetom može biti učitano kao Local. Normalizacija zadržava isti trenutak u UTC-u. Vrijeme bez vremenske zone i dalje je nevaljano; ne izmišlja se datum ni odobrenje pravila.

## Redoslijed stvarnog testa

1. Tijekom otvorene sesije prikupiti zdrave uparene EUR/USD bid/ask zapise i potvrditi Parquet integritet, kratke agregate, featuree i patterne. Prikupljanje ostaje uključeno uz zabranjeno trgovanje; SPY i ostala stock podrška ostaju sačuvani.
2. Nad stvarnim ulazom provesti [FX-10 replay, evaluaciju, trening i pregled modela](FX10_RESEARCH.md). Ako kvaliteta ili edge nakon troška ne prolaze, ostati u istraživanju. Ne odobravati sintetski model ili smanjivati pragove samo radi izazivanja naloga.
3. Provesti [FX-11 AnalysisOnly model shadow](FX11_ONBOARDING.md), odobriti točne verzije i prikupiti propisani uzorak po strategiji. Tek valjani aktualni dokazi daju PaperReady. Ručna promjena registry statusa nije zamjena za uzorak.
4. Pregledati stvarni paper račun i potvrđeni USD sažetak, ugovor, brokerova pravila te dopuštenje zaštite i zatvaranja djelomične/rezidualne količine. Pripremiti dokaz minimuma, koraka i maksimuma osnovnih jedinica. Ako pravila izlaza nisu poznata ili ih adapter ne podržava, test ostaje blokiran; ne zaokruživati rezidual naviše radi prolaza. Hipotetskih 100 USD nije dokaz dovoljne margine ili izvedive količine.
5. Zabilježiti konkretne granice testa: jedan EUR/USD long instrument i strategija, najviše jedan novi trade po nadziranom slučaju, maksimalni EUR iznos, USD risk/gubitak, trošak i holding sekunde. Vrijednosti moraju odgovarati pregledanim pravilima, modelu i postojećem risku. Izolirati izvršavanje ostalih instrumenata, uz očuvano prikupljanje. Ove granice/protokol nisu novi automatski limiter; konfiguraciju i provedbu treba provjeriti prije enablementa.
6. Ponoviti preflight, potvrditi da je račun paper, uskladiti lokalne i brokerske pozicije/naloge te omogućiti samo postojeći FX paper put. Signal prolazi model → FX-9 sizing/troškove/rezervaciju → zajednički execution guard/order lifecycle. Ne koristiti izravni testni `placeOrder` koji preskače te provjere. Ako nema valjanog svježeg signala, nema entryja.
7. Najprije nadzirati FixedBracket slučaj, zatim zasebno AtrTrailing uz odgovarajući istraženi/odobreni exit ugovor. Promjena izlaznog profila koja mijenja verzije zahtijeva novu kompatibilnu evaluaciju/model; ne prebacivati profil usred otvorenog tradea. Za svaki slučaj dovršiti izlaz i usklađivanje prije sljedećeg.

## Dokaz i kriteriji prolaza

| Slučaj | Potreban stvarni dokaz | Trenutno |
| --- | --- | --- |
| Entry | Jedan dopušten signal/rezervacija, broker acknowledgement, stvarni fill s EUR količinom i USD/EUR cijenom | Nije proveden |
| FixedBracket zaštita | Prihvaćeni SL/TP, količina odgovara fillu, stvarni izlaz i potvrđen cancel suprotnog naloga | Nije proveden |
| Timer | Trajni rok u sekundama od prvog filla, izlazni fill, očišćena zaštita i rezervacija | Nije proveden |
| AtrTrailing | Prihvaćena početna zaštita, potvrđene izmjene, stvarni stop ili timer izlaz | Nije proveden |
| Partial/cancel | Stvarni callbackovi i zaštita stvarno ispunjenog dijela, bez over-exita/duplikata | Nije proveden |
| Restart/reconciliation | Kontrolirani restart uz prethodno dokazanu zaštitu; bez duplikata i s dosljednim položajem/nalozima | Nije proveden |
| Završno stanje | Broker i lokalno stanje usklađeni, testni nalozi zatvoreni/otkazani, testna izloženost nula, rezervacija oslobođena | Nije proveden |

Brokerovo Accepted/Submitted nije fill. Ako cijena ne dosegne SL ili TP, taj slučaj ostaje neprovjeren iako timer prođe. Partial fill i disconnect/restart ne smiju se označiti potvrđenima ako se nisu stvarno dogodili; postojeći fake-broker testovi ostaju zaseban dokaz. Ne izazivati mrežni prekid dok zaštita i način oporavka nisu potvrđeni.

Pri nepoznatom nalogu, neusklađenom stanju, odbijenoj zaštiti ili prekoračenom limitu zaustaviti nove ulaze postojećom Pause kontrolom. Zaustavljanje ulaza nije dokaz zatvaranja pozicije. Postojeći Kill Switch otkazuje otvorene naloge i nije zamjena za sigurno zatvaranje izloženosti; ne koristiti ga kao rutinski završetak testa. Provjeriti stvarnu izloženost i zaštitu, provesti nadzirani izlaz/oporavak i potvrditi brokerovo završno stanje prije gašenja procesa. Otkazivati samo naloge ovog testa; ne dirati druge naloge/pozicije. Ako automatizacija ne može zatvoriti rezidual, potrebna je nadzirana intervencija u paper TWS-u, uz zabilježen kvar.

Dokaz čuva UTC vremena, commit i verzije, anonimizirane korelacijske identifikatore, količine/cijene/valute, provizije, slippage/latencije, callbacks i završnu reconciliaciju. Ne objavljivati account ID, ključeve ni cijeli privatni konfiguracijski/log zapis. FX-12 se može zatvoriti tek izvještajem stvarno provedenih slučajeva i jasno navedenih ograničenja; lokalni build i testovi to ne zamjenjuju.

## Lokalna provjera pripreme

Dodano je 12 testnih slučajeva: nepostojeća baza bez stvaranja, sesija/DST, UTC offset normalizacija i odbijanje datuma bez zone, valjani offline inventar bez prava slanja naloga, stari rollout, izmijenjen model, nepregledani size profil, live konfiguracija te CLI izlaz i odbijanje prepisivanja dokaza. Testni model je sintetski inventarski zapis, ne produkcijski model. SHA-256 postojeće testne baze ostaje jednak prije i poslije preflighta. Rezultati pune regresije vode se u [statusu](IMPLEMENTATION_STATUS.md).
