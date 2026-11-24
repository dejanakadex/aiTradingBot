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

### Kontinuirani audit agregata za cijelu sesiju

Od 24.11. dostupan je dodatni offline alat bez ograničenja starog audita na 200.000 događaja:

```powershell
dotnet TradingBot.ModelOps/bin/Release/net10.0/TradingBot.ModelOps.dll fx-collection-stream-audit --dataset ABSOLUTE_FROZEN_DIR --config ABSOLUTE_RESEARCH_JSON --plan ABSOLUTE_PLAN_JSON --from 2026-11-24T08:00:00Z --to 2026-11-24T16:00:00Z --coverage ABSOLUTE_COVERAGE_JSON --output NEW_ABSOLUTE_REPORT_JSON
```

Ulaz se čita u blokovima kroz privremeni diskovni indeks, uz isti agregator i izračun indikatora kao u botu. Stanje se ne resetira na granici datoteke, bloka ili ponoći. Namjerno se resetira kod promjene callback sesije, evidentiranog prekida ili poznatog odbijenog dijagnostičkog para. Coverage intervali potiskuju kotacije zaključno sa svojim krajem. Završni prekid bez sljedećeg ticka također se obrađuje. Izvještaj uključuje hash konfiguracije, rezervacije, manifesta, kanonskih događaja, izuzetih zapisa i featurea; provjera integriteta ponavlja se nakon obrade.

Sirovi `CanPersist=false` zapisi ostaju u izvorniku. Samo izričito prepoznata odbijanja s razlogom ulaze u brojčanu i hash evidenciju dijagnostike; prikazuje se najviše 1.000 primjera, uz oznaku skraćenja. Nepotpun zdrav par, konfliktni duplikat, nepoznata ocjena ili neispravna zdrava strana uz odbijenog para prekidaju audit. Ovo **ne stvara očišćen izvršni dataset** i ne slabi strogi trade replay: `CanUseAsExecutionDataset=false`, `CanSendOrders=false`, `N5Complete=false`.

Raspon je najviše sedam dana; memorija sadrži blok događaja i ograničene sažetke prozora, a ne cijeli niz kotacija. Broj uzastopnih feature barova ograničen je konfiguriranim retentionom (trenutačno 512), pa maksimum 512 nije mjerenje neograničenog trajanja niza. `UsableQuoteMinutes` broji minute s barem jednom potpunom, pravodobnom kotacijom izvan coverage prekida, ne dokazuje cijelu minutu neprekinute likvidnosti. Dnevni prihvat zasebno koristi pokrivenost finalnim barovima. CLI odbija raspon ili snapshot koji zadire u rezervirani holdout; nema novog treninga niti čitanja završnih ishoda.

### Preostali koraci

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

## Cijela prva dnevna sesija — 24.11.2026.

**Dnevni kriterij pokrivenosti prolazi; N5 ostaje otvoren.** [Strukturirani dokaz](evidence/n5-day-session-2026-11-24.json) obuhvaća fiksni trening prozor **09:00–17:00 Zagreb**, bez odabira povoljnih odsječaka. Nema novih strateških ishoda, treninga, approvala ili naloga. Završni holdout nije otvoren.

Snapshot sadrži **10.289 datoteka / 1.025.341 manifest redak**. Hashovi su provjereni prije/poslije audita. Naknadna usporedba u 17:02 s aktivnim manifestom potvrđuje identičnih 10.289 datoteka za isti raspon: nema naknadno zapisane datoteke koja bi nedostajala kopiji. Točno filtrirani ulaz ima **1.025.267 događaja: 512.633 potpuna para označena Healthy i jedan izričito odbijeni dijagnostički bid**. Healthy oznaka prikupljanja nije automatski prihvat strožeg 2s agregacijskog praga.

| Interval | Zdravi finalni barovi / očekivani | Pokrivenost | Price-feature-ready frameovi |
| --- | ---: | ---: | ---: |
| 5s | 5.745 / 5.759 | 99,76% | 5.520 |
| 15s | 1.914 / 1.919 | 99,74% | 1.789 |

Kotacije upotrebljive prema vremenskom i coverage filtru opažene su u **480 različitih minuta**, prvi primitak 08:00:00.0379587 UTC, posljednji 15:59:59.5100513 UTC. Kriterij najmanje 360 opaženih minuta i 95% zdravih finalnih 5s prozora prolazi. To nije dokaz da u svakoj sekundi postoji izvršiva likvidnost. Zadnji prozor koji još nije dostupan do as-of granice ne ulazi u nazivnik; ne dodaje se umjetni završni bar. Od 14 nedostajućih 5s prozora, devet nema opažene kotacije, a pet nema prihvatljiv finalni bar. Najdulji potpuno prazni niz ima pet 5s prozora.

### Prekid, kvaliteta i sirovi ulaz

Zabilježene su dvije faze jednog incidenta **11:17:35–11:18:08 Zagreb**. Odbijeni bid nosi izvorno vrijeme **10:17:37 UTC**, primitak **10:17:53.1513568 UTC**, `Stale/CanPersist=false`; ask nije izmišljen. Jedan potpuni par na granici coverage prekida također je potisnut iz agregacije. Prekidi, originalni raw zapisi i hash izuzetog sadržaja ostaju sačuvani. Novi audit ne proizvodi izvršni dataset; potpuni derivacijski ledger i vezanje coveragea uz kasniji replay ostaju otvoreni.

FX agregator je odbio još **394 događaja / 197 parova** starijih od svojeg 2s praga. Nema događaja iznad dopuštenog budućeg odstupanja; 2.586 događaja tolerirano je unutar konfigurirane granice, najveće opaženo vodstvo 94,59 ms. Tri odvojena NTP uzorka u 16:48 daju +46,36 do +46,64 ms. Sat/tolerancija nisu mijenjani. Ovo ne dokazuje trajnu sinkronizaciju ni uzrok svakog kašnjenja.

Kontrolna noćna obrada istim novim alatom evidentira **1.359.228 događaja, 679.613 potpunih parova i dva poznata odbijena dijagnostička bida**, uz pet coverage-potisnutih parova. Ima 11.392/12.719 zdravih 5s barova i 8.934 zagrijana framea. Veliki noćni prekid ostaje u nazivniku; noć nije proglašena neprekinutom kvalitetnom sesijom. Sedam callback identiteta uključuje identitet koji je dao samo odbijeni zapis, zato nije isto što i šest identiteta s potpunim zdravim parovima iz ranijeg pregleda.

### Performanse: novi dokaz o manifestu

Cijeli dnevni log ima 477 mjernih prozora / 1.025.197 obrađenih događaja, nula processing/storage iznimaka i nula negativnih queue clock delta. Log prozori i točno filtrirani snapshot imaju različite rubove brojanja. Nema ERROR/FATAL poruka; quality odbijanja i WARN reconnect nisu time izbrisani.

- Pipeline maksimum **111,02 ms**, najviši p99 gornji rub pojedinog prozora **0,232 ms**.
- Queue maksimum i najviši p99 gornji rub **434,80 ms**: potonji prelazi N3 prag 100 ms.
- Dataset flush maksimum **12.110,25 ms**, enqueue → durable maksimum **23.502,03 ms**: oba prelaze postojeće pragove. To nisu mjere SQLite commita.
- Najsporiji flush u **15:23:08 Zagreb**: manifest read **2.496,94 ms**, manifest write **8.587,56 ms**, Parquet serialize **145,68 ms**, write-gate čekanje **0,0023 ms**. To locira sporu fazu, ne dokazuje samostalno SSD, antivirus ili scheduler kao uzrok.

Odvojeni raspon 09:00–16:35, prije ovog offline rada, ima iste vršne zastoje. Zato ih nije opravdano pripisati ovom auditu/testovima. Manifest u 16:57 ima **27.051.579 bajtova**. Kod pri promjeni ponovno čita cijeli manifest i zapisuje ga uz `WriteThrough`, izračun hasha i zamjenu datoteke. Sljedeći prioritet performansi je mjerena optimizacija rasta/zapisa manifesta uz očuvanje integriteta i oporavka; nema opravdanja za slabljenje trajnosti ili zamjenu praga većom brojkom. Runtime nije mijenjan tijekom sesije. Prethodni ograničeni N3 prihvat ne prenosi se na ovaj dan ili paper naloge.

### Pravila, provjere i nastavak

[Read-only dokaz u 16:46](evidence/n5-rules-afternoon-2026-11-24.json) zadržan je u konfiguraciji korištenoj za audit. Nakon završetka dnevnog prozora dodatno je snimljen [dokaz u 17:03](evidence/n5-rules-next-day-2026-11-24.json); nova offline konfiguracija zadržava svih pet izvornih opažanja 19./23./24.11. i njihove stvarne vremenske granice. Novo opažanje vrijedi najdulje do **25.11.2026. 17:03:22 Zagreb**, pokrivajući sutrašnji fiksni dnevni prozor. Potvrđuje ugovor/cjenovna pravila i zasebno pregledane standardne veličine, ne račun, marginu, provizije ili fill. Nema retroaktivnog popunjavanja pravila.

**828/828 Release testova prolazi**, bez preskočenih; obvezni IBKR DLL, publish sadržaj i missing/changed-DLL guardovi prolaze. Stvarni jutarnji kontrolni uzorak ima potpuno jednake intervalne statistike u starom i novom auditu (45.914 događaja, 238 zdravih 5s barova, 188 zagrijanih frameova). Testovi pokrivaju više od 200.000 događaja, različite blokove, ponoć, promjenu callback sesije, trailing coverage prekid, zatvorenu sesiju, odbijeni orphan, neispravni zdravi ulaz, konfliktne datoteke, holdout i zabranu prepisivanja izlaza.

Kolektor PID 14584 ostaje na postojećem N4 runtimeu; u 17:01 spojen je i prima svježe kotacije, reconnectCount=7, consecutiveFailures=0, failed quality streams=0. Četiri provjerene zabrane naloga ostaju aktivne; baza ima **0 tradeova, 0 naloga, 0 executiona i 0 numeričkih modela**. Promjene ove točke su offline alat i dokumentirani dokazi, bez deploya/restarta kolektora.

Ostaviti paper TWS i kolektor aktivne. Potvrda lokalne postavke Auto restart još je zatražena, nije pretpostavljena ili promijenjena. Sljedeći pregled **25.11. oko 16:30 Zagreb** omogućuje osvježavanje pravila prije isteka i završni pregled drugog trening dana nakon 17:00. Za N5 još trebaju validacija 26.11., rezervirani završni uzorak 27.11., eksplicitna izvršna derivacija te pravilno vezanje labela/podjela prije N6/N7. Jedan prolaz dnevne pokrivenosti nije prihvat cijelog N5, FX-12 ili paper spremnosti.
