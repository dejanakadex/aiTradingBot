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
