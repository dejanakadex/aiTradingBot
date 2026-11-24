# N5 — zamrznuti podaci i unaprijed rezervirane vremenske podjele

23.11.2026. **N5 je u tijeku.** Implementirana je dodatna offline provjera i priprema istraživačkog skupa. Buduće reprezentativne sesije i završni uzorak još ne postoje; prolaz tehničkih testova nije završetak N5. Kolektor ostaje AnalysisOnly/read-only, bez naloga i model approvala. SPY i postojeći FX put ostaju sačuvani.

## Razvojni podaci i dokazi

Inventura aktivnog manifesta našla je kotacije na datumima 25./26.10. i 19./20./23.11., s velikim prazninama. Broj datuma nije broj potpunih sesija. Listopadski podaci ostaju povijesni dijagnostički izvor zbog tadašnjih problema sata, prekida i drukčije provjerene konfiguracije; nisu dio novog razvojnog snapshot-a. Nisu obrisani niti popravljani pomicanjem timestampova.

Postojeći `fx-snapshot` zamrznuo je raspon **19.11. 22:58:10 UTC → 23.11. 13:40:00 UTC** u novoj lokalnoj mapi `TestResults/n5-20261123/quotes-november`. Kopija sadrži 13.280 datoteka i 2.053.027 manifest redaka. Datoteke se kopiraju cijele; reader dodatno filtrira rubove raspona. Paritet hashova svake datoteke i novi manifest potvrđuju integritet kopije, ne potpunost parova ili izvršivost tradeova. Izvorni collector nije zaustavljen.

Zasebno je zamrznuto **82 zapisa** iz `TickCoverageGapRecords`. To nisu nužno 82 različita brokerska incidenta: zapisi uključuju nepoznatu pokrivenost i različite faze istog prekida. Čuvaju se izvorni intervali/razlozi; ne zamjenjuju se minutnim backfillom. Praznine između quote callbackova također mogu uključivati zatvorenu sesiju, rijedak promet ili planirani restart. Ne proglašavaju se automatski gubitkom podataka.

Lokalna konfiguracija `research-100.json` zadržava 100 USD kao istraživački kapital, postojeće rizike, troškove i originalne minimume uzoraka 100/30/50. Ne znači da je standardni EUR/USD nalog izvediv sa 100 USD. Nije pokrenut novi replay rezultata, trening ili model odobrenje u N5. Ranije već analizirani podaci služe razvoju i ne predstavljaju neviđeni završni test.

## Pravila brokera

Ponovno je pregledana [službena IBKR tablica veličina](https://gdcdyn.interactivebrokers.com/en/trading/forexOrderSize.php), te read-only TWS probe snima novi ugovor i cjenovna pravila u [dokazu od 23.11.](evidence/n5-rules-2026-11-23.json). Dokaz od 19.11. ostaje nepromijenjen. Nova konfiguracija nosi oba opažanja s izvornim vremenima.

Konfigurirana valjanost pravila je 24 sata od `VerifiedAtUtc`; pravilo je dostupno tek od stvarnog `ObservedUtc`. Novo opažanje **ne popunjava prošlost**, niti ga smijemo produžiti promjenom datuma. Za buduće dane prije isteka treba ponovno pregledati veličine i snimiti contract/market rule kroz postojeći `--forex-rules-config` postupak. Nije uvedena automatska obnova ili tvrdnja da isti price rule dokazuje marginu, provizije, odd-lot ili stvarno izvršenje.

## Rezervacija budućeg uzorka

[Strojno čitljiva rezervacija](evidence/n5-reserved-splits-2026-11-23.json) zapisana je prije početka svih prozora. Granice su UTC u datoteci; tablica je **Europe/Zagreb**:

| Namjena | Početak | Završetak, isključivo |
| --- | --- | --- |
| Trening | 24.11.2026. 09:00 | 26.11.2026. 09:00 |
| Validacija | 26.11.2026. 09:00 | 27.11.2026. 09:00 |
| Završni holdout | 27.11.2026. 09:00 | 28.11.2026. 09:00 |

To je prvi kandidat prema postojećoj minimalnoj postavci 2/1/1 kalendarskih dana, **ne obećanje prihvata nakon četiri dana**. Uključuje noćne/zatvorene intervale; treba zasebno mjeriti stvarnu pokrivenost, različite uvjete i broj nezavisnih uzoraka. Ako sesije, pravila ili uzorci nisu dovoljni, N5 ostaje otvoren. Novi plan mora se verzionirati prije uvida u njegov završni rezultat; ne birati povoljne dane prema PnL-u. Sada nije analiziran holdout niti su na njegovoj osnovi mijenjani modeli ili pragovi.

Za prvi prihvat unaprijed tražimo četiri različita otvorena dana: dvije trening dnevne sesije, jednu validacijsku i jednu završnu. U svakom fiksnom dnevnom prozoru **09:00–17:00 Zagreb** treba barem šest sati promatranja i najmanje 95% očekivanih zdravih finalnih 5s prozora tijekom otvorene FX sesije. Nepotpuni/konfliktni parovi moraju biti eksplicitno izuzeti s izvornim dokazom, a pravila moraju biti dostupna i valjana za svaki kandidat koji ulazi u izvršnu simulaciju. Zabilježene prekide zadržati u coverage dokazu; ne birati samo povoljne kontinuirane odsječke. Ovi operativni kriteriji nisu statističko jamstvo i trenutno se provjeravaju zasebnim izvještajem prihvata, ne samim `fx-dataset-review` izlazom. Ne smanjivati ih nakon pregleda ishoda; nedostatak podataka znači novi budući krug. Originalni minimumi 100/30/50 labela ostaju donja granica za kasniju evaluaciju.

Na obje strane granice dodan je buffer od 120 sekundi, veći od trenutačnog simulatorova maksimalnog prozora od 69 sekundi (entry timeout + holding + quote gap + dvije latencije). `ContainsLabel` dodatno provjerava **stvarni završetak labele**, redoslijed vremena i pripadnost samo jednoj particiji. Nepoznat izlaz ili prozor koji prelazi granicu nije prihvatljiv. Promjena horizonta zahtijeva ponovno provjeren buffer. Warmup smije koristiti samo ranije dostupne feature podatke, bez budućih ishoda.

Rezervacija nije globalna zabrana pristupa na računalu: novi review CLI odbija raspon i snapshot koji zadiru u holdout, ali ostale postojeće generičke replay/evaluate naredbe ne učitavaju ovaj plan automatski. Prije N6/N7 treba vezati odabrani plan i stvarne label intervale uz točan replay/evaluation, provjeriti postojeći purging te spriječiti upotrebu holdouta pri izboru varijanti. Helper nije zamjena za tu operativnu provjeru.

## Ponovljiva provjera

```powershell
dotnet TradingBot.ModelOps/bin/Release/net10.0/TradingBot.ModelOps.dll fx-dataset-review --dataset ABSOLUTE_FROZEN_DIR --config ABSOLUTE_RESEARCH_JSON --plan ABSOLUTE_PLAN_JSON --from 2026-11-19T22:58:10Z --to 2026-11-23T13:40:00Z --output NEW_ABSOLUTE_REPORT_JSON
```

Naredba ne zahtijeva operativnu bazu, ne pokreće host/broker i ne šalje naloge. Koristi postojeći streaming reader i privremeni diskovni replay indeks: provjerava manifest prije/poslije, parove, duplikate, UTC/redoslijed, crossed/future/nepotpune ulaze. Zadržana su postojeća stroga pravila; neispravan ulaz prekida pregled, bez završenog pozitivnog izvještaja. Ne prepisuje postojeći izlaz. Raspon je ograničen na 31 dan, memorija cijena na blokove i minutne sažetke; diskovni indeks zahtijeva slobodan prostor. Samo provjereni podskup može dati izvještaj, ali i tada je `N5Complete=false`.

Izvještaj povezuje hash konfiguracije, plana, manifesta i kanonskog sadržaja. Po danu navodi potpune quote parove, stale kotacije, nedostupna/istekla pravila, broj/vrh razmaka, quote-weighted spread distribuciju i prosječni opaženi minutni raspon midpointa. To nisu potvrda kontinuiranosti, zagrijani feature barovi, nepristrana procjena volatilnosti ili brokerski execution dokaz. Te razlike ostaju eksplicitne.

## Što još treba za prihvat N5

1. Prikupiti buduće prozore uz uključen kolektor/TWS i zabilježiti prekide i promjene verzije.
2. Pravodobno osvježavati stvarna pravila; razdoblja bez pravila ostaju neizvršiva u simulaciji.
3. Zamrznuti svaki skup s nepromjenjivim manifestom i provjeriti kvalitetu/pokrivenost, bez tihog brisanja problematičnih razdoblja.
4. Vezati konfiguraciju, proofove i rezervaciju uz kasnije labele/replay; potvrditi vremenske granice i purge stvarnih preklapanja.
5. Prije završnog uvida zaključati varijante/model. Dovoljan broj dana/tradeova, neto prednost i statistička pouzdanost ne proizlaze iz broja tickova.

N5 ne zatvara FX-12, model shadow, puni 25/30/31 ili stvarni paper ciklus. Nema naloga.

## Rezultati ove provjere

**816/816 Release testova prolazi**, uključujući N5 streaming/CLI, odbijanje holdouta i neispravnih planova, nepotpune parove, preklapanje/unknown labele i zabranu prepisivanja izvještaja. IBKR publish te missing/changed-DLL guardovi prolaze. [Hashovi, rasponi i izvještaji](evidence/n5-preparation-2026-11-23.json).

| Razvojni raspon, UTC | Nalaz |
| --- | --- |
| Cijeli 19.11. 22:58:10 → 23.11. 13:40 | Hashovi kopije prolaze, ali jedinstveni replay ulaz odbijen zbog nepotpunih parova; nije prihvaćen dataset |
| 23.11. 08:00 → 11:45 | 33 nepotpuna para, prvi primitak 08:17:59.6836452, posljednji 08:30:17.6264365; jutarnji dijagnostički raspon konzervativno izuzet iz prvog razvojnog pregleda, izvornik sačuvan |
| 23.11. 11:45:15 → 13:40 | 268.406 događaja / 134.203 potpuna para; bez duplikata/stale parova; 17 razmaka >3 s, max 5,674 s; prosječni quote-weighted spread 0,1493 pipa; 115 opaženih minuta; svi parovi bez tada važećih pravila |
| 20.11. 09:00 → 21:55 | 1.434.750 događaja / 717.375 potpunih parova; bez duplikata; 133 stale para, 449 razmaka >3 s, max 32,103 s; prosječni spread 0,1369 pipa; 775 opaženih minuta; postojeće opažanje pravila vremenski pokriva parove |

U jutarnjem problematičnom razdoblju log ima 33 `Future` upozorenja. Podudarnost broja i vremena podupire vezu s tadašnjim clock/quality problemom, ali nije dokaz točnog per-event mehanizma nedostajuće strane. Nisu dopisivane kotacije, slabljen validator niti popravljani timestampovi. To ostaje posebna povijesna integrity bilješka za prihvat N5.

Današnji dokaz pravila opažen je tek u **15:04:56 Zagreb** i vrijedi najdulje do **24.11. oko 15:04 Zagreb**, ovisno o konfiguriranoj provjeri. Zato ne može učiniti prihvatljivim raniji 23.11. razvojni raspon. Stale i praznine 20.11. nisu uklonjeni da bi rezultat bio ljepši; kasniji replay mora primijeniti postojeći coverage/risk režim. Nije izračunat novi PnL.

Operativna baza ima nula naloga, tradeova, executiona i numeričkih modela. Kolektor nije restartan zbog offline N5 izmjena; ostaje na provjerenom N4 runtimeu. Istraživačka opterećenja diska ne smatraju se novom N3 prihvatnom sesijom.

## Jutarnja provjera — 24.11.2026.

**N5 ostaje otvoren.** [Strukturirani dokaz](evidence/n5-overnight-2026-11-24.json) povezuje nove snapshotove, provjeru quote parova/barova, logove i read-only coverage zapis. Rezervacija treninga/validacije/holdouta od 23.11. ostaje nepromijenjena; nije pregledan budući holdout niti računat novi strateški PnL. Produkcijski kod, runtime i odobrenja nisu mijenjani.

### Veza i sat

Kolektor PID 14584 ostao je aktivan. U 09:37 Zagreb prima svježe kotacije, ima nula uzastopnih neuspjeha i nula failed quality streamova. Sve četiri provjerene zabrane naloga ostaju uključene; operativna baza nema tradeova, naloga, executiona ni numeričkih modela.

Noć ima šest reconnect događaja i 12 zapisa coverage faza. Važniji prekidi, **Zagreb**:

- 23.11. oko 18:51: IBKR 1100/1102, veza prema IBKR-u izgubljena pa obnovljena.
- 23.11. oko 23:42: timeout pa prestara kotacija i obnova pretplate.
- **23.11. 23:45:01 → 24.11. 01:03:50:** socket 10054, zatim 10061 na lokalnom 7497; TWS API nije slušao. Korisnik potvrđuje da se TWS ugasio te ga je kasnije ručno pokrenuo. Bot je sam obnovio vezu, bez restarta kolektora.
- 24.11. 06:27–06:33: potvrđeni IBKR prekid/oporavak 1100/1102.
- 24.11. oko 08:12: odbijena prestara kotacija i obnova pretplate.

Gašenje u 23:45 odgovara [IBKR-ovu dokumentiranom zadanom auto logoff vremenu](https://www.interactivebrokers.com/support/faq/content/29091366). Za provjeru na računalu: **Global Configuration → Lock and Exit → Auto restart** umjesto Auto logoff. Lokalna postavka nije očitana ni promijenjena ovim checkpointom, pa je auto logoff vjerojatno objašnjenje vremena, a ne potvrđena konfiguracijska činjenica. Ponovno pokretanje nije neprekidan feed i još zahtijeva bilježenje/reconciliation prekida; nije obećano trajno automatsko prijavljivanje.

Windows Time prijavljuje sinkronizirano stanje (`Leap Indicator=0`), posljednji uspjeh 08:50:10 Zagreb. Tri read-only uzorka oko 09:25 daju +55,46 do +56,12 ms prema time.windows.com. Nisu mijenjani sat, tolerancija ili napajanje. Ovo je trenutna provjera, ne dokaz buduće trajne sinkronizacije.

### Noćni sirovi uzorak

Snapshot 23.11. **14:05 UTC → 24.11. 08:00 UTC** sadrži **19.922 datoteke / 1.360.144 manifest retka**, s provjerenim hashovima. Strogi N5 pregled odbija ga zbog **dva nepotpuna para**. Pregled izvornih redaka precizira zašto:

| Event vrijeme UTC | Primitak UTC | Sačuvana strana | Izvorna ocjena |
| --- | --- | --- | --- |
| 23.11. 22:42:40 | 22:42:54.4925111 | bid | Stale, CanPersist=false |
| 24.11. 07:12:23 | 07:12:39.2444708 | bid | Stale, CanPersist=false |

`MarketDataPipeline.ProcessCanonicalEventAsync` šalje u dataset i odbijeni događaj s ocjenom radi dijagnostike. Nakon `CanPersist=false`, `TickMarketDataHostedService` prekida tu pretplatu i obnavlja je; odgovarajući ask nije obrađen. Ovo nisu dokazano izgubljene zdrave kotacije ili oštećene datoteke. Naziv `CanPersist=false` ne znači da dijagnostički redak fizički ne postoji u Parquetu. Sirovi dataset zato nije isto što i čisti izvršni input.

Nije dopisan ask, promijenjena ocjena ili tiho filtriran izvor. Prije replaya takvog raspona treba **eksplicitna derivacija kvalitetnog podskupa s popisom izuzetih događaja i očuvanim coverage intervalima**; nepotpun zdrav par mora i dalje biti greška. Praznine ostaju nepoznata izvršna pokrivenost, ne pozicije zatvorene izmišljenom cijenom. Ta priprema ostaje otvoren N5 ulaz. Današnja analiza ne tvrdi da je cijela noć prošla replay provjeru.

### Početak prvog trening dana

Zaseban snapshot **24.11. 09:00–09:20 Zagreb**: 428 datoteka / 46.002 manifest retka; nakon točnog filtriranja rubova **45.914 događaja / 22.957 potpunih parova**, bez duplikata ili nepotpunih parova, jedna callback sesija. Svi parovi imaju vremenski dostupno opažanje pravila. Prosječni quote-weighted spread je 0,1466 pipa; maksimum 0,4 pipa. To nije dokaz izvršivog spreada ili profita.

- 5s: **238/239 očekivanih zdravih finalnih barova (99,58%)**, 188 price-feature-ready frameova, najdulji niz 210 barova.
- 15s: **78/79 barova**, 45 price-feature-ready frameova.
- 104 stale događaja / 52 para odbijena su u FX agregaciji; jedan bar potisnut je na svakom intervalu. 44 događaja unutar dopuštenog budućeg odstupanja, najviše 33,37 ms; nema prekoračenja tolerancije.
- Nalet oko 09:17 ima najstariju kotaciju 08:17:29 UTC, primljenu u 08:17:33.0088891 UTC, uz samo **0,1213 ms** internog čekanja. Kašnjenje tog događaja postoji prije/pri broker callbacku; ovaj dokaz ne razlikuje brokerovu mrežu/poslužitelj od lokalnog SDK dispatcha i rezolucije timestampa.

Prvih 20 minuta ne zadovoljava uvjet šest sati promatranja dnevnog prozora niti zamjenjuje cijeli dan. Gubitak jednog bara i prekid warmupa ostaju u rezultatu. Nema novog treninga, evaluacije ishoda ili model approvala.

### Latencija i sljedeći prihvat

Logovi 23.11. 14:05 → 24.11. 08:20 UTC daju 1.001 minutni prozor i 1.408.570 obrađenih događaja (log prozori i snapshot rubovi nisu isti način brojanja). Nula processing/storage iznimaka i nula negativnih queue clock delta; odbijanja kvalitete broje se odvojeno.

| Mjera | Cijeli pregled logova | Prvih 20 trening minuta |
| --- | ---: | ---: |
| Pipeline max | 86,27 ms | 52,54 ms |
| Najgornji p99 pipeline prozora | 25,14 ms | 0,108 ms |
| Queue max | 759,35 ms | 53,41 ms |
| Najgornji p99 queue prozora | 137,81 ms | 53,02 ms |
| Dataset flush max | 3.613,18 ms | 1.895,02 ms |
| Enqueue → durable max | 8.859,21 ms | 7.654,11 ms |

Noć ne prolazi sve N3 pragove: queue i dataset storage imaju repove, a jutarnji durability max malo prelazi 7.500 ms. To nisu mjerenja SQLite commita: riječ je o zasebnim dataset/queue metrikama. Quality checkpoint maksimum od pokretanja doseže 2.006 ms, pending oko 3.003 ms, bez failed streamova. Ne pripisujemo sve repove SSD-u bez dodatnog dokaza i ne prenosimo prethodni ograničeni N3 prihvat na ovu noć ili paper izvršavanje.

### Pravila i nastavak

Ponovno pregledana [IBKR tablica veličina](https://gdcdyn.interactivebrokers.com/en/trading/forexOrderSize.php); [novi read-only TWS dokaz](evidence/n5-rules-2026-11-24.json) opažen je **24.11. u 09:25:48 Zagreb**, prije isteka prethodnog. Novi offline config zadržava sva tri opažanja 19./23./24.11., s izvornim vremenima. Novi prozor valjanosti završava najkasnije **25.11. oko 09:25:47 Zagreb**. Nema retroaktivnog pokrivanja praznina.

Ostaviti paper TWS i kolektor aktivne; sljedeći dnevni pregled ima smisla **24.11. nakon 17:00 Zagreb**, kada završava prvi planirani dnevni prozor. Tada ponovno provjeriti kvalitetu/barove, odvojiti dokumentirane odbijene dijagnostičke zapise i osvježiti pravila prije idućeg dana. N5 se ne zatvara nakon jednog dana. Lokalna puna regresija nije ponavljana jer nema izmjene koda; postojeći N5 commit ima 816 prolaznih testova i zeleni CI, a sada su stvarno izvršeni snapshot/review/audit/latency alati i read-only TWS probe.
