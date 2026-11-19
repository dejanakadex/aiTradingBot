# FX-12 — priprema prvog EUR/USD backtesta

Priprema 19./20.11.2026. koristi postojeći FX-10 replay; ne mijenja aktivni kolektor, SPY/FX podršku, operativne approvale ili dopuštenje naloga. Istraživačke baze/configi su zasebni. **Nije odobren model i FX-12 ostaje otvoren.**

**Prva stvarna obrada završena:** snimka 22:58:10–23:02 UTC ima 18 provjerenih datoteka / 1.188 manifest redaka, od toga 1.172 događaja (586 potpunih parova) unutar raspona. Oba scenarija završavaju replay s istim input hashom i po 370 odbijenih pattern provjera, bez signala, labela ili tradeova. Najdulji 5s niz je 13, pa nema 26-bar warmupa. Nulti PnL ovdje nije rezultat izvedivog trgovanja. [Strukturirani dokaz](evidence/fx12-research-preparation-2026-11-20.json). Lokalno **741/741 testova**, Release build i obvezni IBKR publish/negative guards prolaze.

U novom uzorku ponovno se pojavio jedan par starosti **3.175,39 ms**. Za isti događaj čekanje nakon callbacka bilo je **0,178 ms**, a obrada **34,61 ms**; SDK dispatch prozor ima maksimum **0,246 ms**. To razdvaja ovaj zastoj od internog reda kolektora, ali ne razlikuje TWS/feed/mrežu od raspoređivanja readera. Jedan 5s prozor je opravdano odbačen; prag od dvije sekunde nije povećan. Mali negativni skew jednog drugog para (6,82 ms) obrađen je postojećom 250ms tolerancijom. Dnevni uzorak i dalje je potreban.

## Pravila i troškovi

Read-only `TradingBot.ShadowProbe --forex-rules-config` sada snima stvarni contract i `reqMarketRule` cjenovne razrede. Prima konfiguraciju s prethodno pregledanim `ForexExecution` veličinama; ne izvodi veličinu naloga iz indikativnog `MinimumSize`. Prihvaća samo lokalne paper portove, disabled AnalysisOnly i novi izlazni put. Adapter koristi `ReadOnlyDiagnostics`: bez submit/modify/cancel, računa, kolektora ili baze.

TWS je u **22:58:08 UTC 19.11.** potvrdio EUR/USD ConId 12087792, market rule 3188, korak 0,00005 od cijene 0. Metadata minimum/korak količine je 0,01 EUR, ali to **nije potvrda standardnog minimuma ili odd-lot izvršenja**. [Dokaz](evidence/fx12-research-rules-2026-11-19.json).

- Pregledana [IBKR standardna tablica EUR naloga](https://gdcdyn.interactivebrokers.com/en/trading/forexOrderSize.php): 20.000–6.000.000 EUR. Research dodatno ograničava zahtjev na 20.000 EUR i USD izloženost scenarija. To nije potvrda margine, prihvaćanja zaštite ili rezidualnih izlaza konkretnog računa.
- [Spot FX Tier I](https://www.interactivebrokers.com/en/pricing/commissions-spot-currencies.php): 0,20 bps po nalogu, najmanje 2 USD; najmanje 4 USD za ulaz+izlaz. Simulator konzervativno naplaćuje minimum po fillu.
- [Odd-lot izvršenje](https://www.interactivebrokers.com/support/faq/content/odd-lot-fx-transactions-1163260635) nema jamstvo standardnog IDEALPRO spreada. Nije dodana izmišljena mogućnost fill-a za 0,01 EUR samo zato što je metadata pokazala taj korak.

Source review je iz **22:55:17 UTC**, opažanje ugovora iz **22:58:08 UTC**. Originalna vremena ostaju nepromijenjena: ovaj snapshot nije dopušten za ranije odluke. Uz 24h freshness rok pravila vrijede do **20.11. u 22:55:17 UTC** (23:55 Zagreb); to obuhvaća planirani dnevni uzorak 10–12 sati. Za kasnije podatke treba nova stvarna snimka, ne pomicanje datuma stare snimke. Višednevna evaluacija traži odgovarajuću povijest takvih snimki u istom verzioniranom istraživačkom ugovoru.

## Dva odvojena scenarija

| Parametar | Kapital 100 USD | Tehnički scenarij 50.000 USD |
| --- | --- | --- |
| Kapital / kupovna moć simulacije | 100 USD | 50.000 virtualnih USD |
| Maksimalna USD izloženost | 100 USD | 25.000 USD |
| Maksimalni rizik tradea / dnevni gubitak | 1 / 3 USD | 100 / 300 USD |
| Leverage / otvorene pozicije u simulaciji | 1 / 1 | 1 / 1 |
| Bazne jedinice, gornja granica | 20.000 EUR, dodatno ograničeno kapitalom/riskom | 20.000 EUR, dodatno ograničeno kapitalom/riskom |

Scenarij 100 USD je provjera stvarnog korisnikova budžeta; očekivano ne zadovoljava standardni minimum i troškovni risk. **Nula izvršivih tradeova valjan je nalaz.** Veći scenarij služi tehničkom testu simulatora, nije preporuka uplate, promjena paper salda ili dokaz izvedivosti sa 100 USD.

Zajedničko: postojeći 5s patterni i 26-bar warmup, long, FixedBracket, SL 10 pipova, TP 30 pipova, holding 60s od prvog filla, entry timeout 5s, pretpostavljena latency 500ms, maksimalni quote gap 3s, quote age 2s, slippage 0,2 pipa po strani, safety 0,5 bps i minimalni net edge 1 bps. Fill kapacitet do 20.000 EUR po callbacku je **hipotetski**, ne opažena likvidnost. Te vrijednosti nisu podešavane prema rezultatu malog uzorka. Spread dolazi iz stvarnih bid/ask podataka.

`fx-prepare` računa točne feature/pattern/label verzije i postavlja signalne verzije samo u novom offline configu radi baseline replaya. Ne odobrava model niti mijenja postojeće signalne profile. Bez `--rules` proizvodi jasno nedovršen draft s praznim pravilima i bez signalnih approvala; `fx-contract` ga i dalje odbija.

Evaluacija je pripremljena za 2 dana treninga, 1 dan validacije, korak 1 dan i 1 netaknuti holdout dan. Minimumi 100 trening / 30 validacija / 50 holdout kandidata ostaju; model minimumi također nisu spušteni. To je početna vremenska shema, ne potvrda statističke pouzdanosti. Jedna dvosatna sesija nije dovoljna za taj raspored. Ne trenirati/odobravati model na prvom kratkom smoke uzorku.

## Lokalni artefakti i ponavljanje

Aktualni lokalni radni direktorij: `C:\Users\Dorinet\.codex\worktrees\dd5e\aiTradingBot\TestResults\fx12-research-20261119`.

Sadrži `rules-probe.json`, izvorni neuspjeli report dok je TWS bio ugašen, uspješni `rules-evidence-retry.json`, `research-100.json` / `research-50000.json`, njihove preparation/contract izvještaje i zasebne `.db` baze migrirane kroz svih 23 migracije. Operativni `C:\PrivateGit\aiTradingBot\data\eurusd-collection\trading.db` nije mijenjan istraživačkim naredbama. Raw podaci, konfiguracije i baze ostaju izvan Gita.

Nove konfiguracije pripremiti iz korijena izgrađenog checkouta. Izlazna mapa mora postojati, izlazna datoteka mora biti nova:

```powershell
dotnet TradingBot.ModelOps/bin/Release/net10.0/TradingBot.ModelOps.dll fx-prepare --config "$PWD/TradingBot.Web/appsettings.ForexCollection.json" --rules "$PWD/docs/evidence/fx12-research-rules-2026-11-19.json" --equity-usd 100 --output "$PWD/TestResults/research-100-new.json"
```

Za tehnički scenarij zasebno navesti `--equity-usd 50000` i novi izlaz. Ne mijenjati kapital usred istog runa. Svaka promjena risk/cost/rules konfiguracije mijenja label ugovor.

Za osvježavanje opažanja koristiti `--forex-rules-config` s istim sigurnim TradingSettings i jednim `ForexExecution:Profiles` zapisom: instrument, minimum/maksimum iz ponovno pregledanog izvora, stvarni `ReviewedAtUtc` i `EvidenceSource`. Ne osvježavati vrijeme bez pregleda. Primjer nad već pripremljenom lokalnom datotekom:

```powershell
dotnet TradingBot.ShadowProbe/bin/Release/net10.0/TradingBot.ShadowProbe.dll --forex-rules-config "$PWD/TestResults/fx12-research-20261119/rules-probe.json" --port 7497 --client-id 97 --output "$PWD/TestResults/rules-new.json"
```

Novi `fx-snapshot` sprema provjeren podskup quote datoteka i izvorni manifest; ne zaustavlja writer. Odbija postojeći odredišni direktorij, ugniježđivanje sa sourceom, nevaljane putanje/hashove i više od 200.000 manifest redaka. Ako se izvorni manifest promijeni tijekom početnog čitanja, ponoviti naredbu s novim odredištem. Naknadno dodavanje novih source datoteka ne mijenja snimku. Na pogrešci ostavlja djelomične kopije za pregled, bez dovršenog manifesta.

Primjer **nakon** završetka dnevne sesije 20.11. 10–12 Zagreb:

```powershell
dotnet TradingBot.ModelOps/bin/Release/net10.0/TradingBot.ModelOps.dll fx-snapshot --dataset C:\PrivateGit\aiTradingBot\data\eurusd-collection\datasets --output-dir "$PWD/TestResults/eurusd-day-20261120" --instrument-id FX-CASH-EUR-USD-IDEALPRO --from 2026-11-20T09:00:00Z --to 2026-11-20T11:00:00Z
```

Prvo provesti `fx-collection-audit` nad tom snimkom. Ako prelazi 200.000 događaja, odabrati manji unaprijed određen raspon; ne birati samo profitabilne fragmente. Razdvojeni runovi nemaju prijenos otvorene pozicije među batchovima.

Zatim `fx-replay` s eksplicitnim apsolutnim `--db`, `--config`, `--dataset`, instrumentom/strategijom i istim `--from/--to`. Koristiti isključivo zasebnu istraživačku bazu. Za novu bazu: napraviti novu praznu datoteku bez prepisivanja, `ModelOps migrate`, pa `fx-contract` prije replaya. CLI ne pokreće host/brokera i ne kreira stvarne naloge.

Pregledati signal count, odbijene/blocked kandidate, potpune/nepotpune labele, razloge no-fill/gap/reconnect/risk odbijanja, zatvorene tradeove, troškove i output hash. `Completed` bez tradeova potvrđuje samo da je obrada završila. Tek višednevni odgovarajući uzorak vodi na evaluate/train/review, zatim stvarni model shadow i odvojeni FX12 paper protokol.

## Dogovoreni prijedlozi nakon FX-12 — 20.11.2026.

Korisnik je zatražio da se prijedlozi iz pregleda DaviddTech / AI Trading Arena sačuvaju za povratak **nakon završetka FX-12**. Ovo je odgođeni istraživački backlog, nije implementacija, dokaz profitabilnosti niti odobrenje trgovanja. FX-12 i reprezentativni podaci ostaju prioritet; prije izvedbe dogovoriti sljedeću pojedinačnu točku.

Predloženi redoslijed nadogradnje postojeće infrastrukture:

1. **Preglednik istraživačkih eksperimenata:** objediniti uspješne i neuspješne pokušaje, verzije strategije/parametara, hashove podataka, troškove, rezultate i razloge prihvaćanja/odbijanja. Ponovno koristiti postojeći research/model registar; brojati sve pokušaje i uklanjati duplikate.
2. **Jednostavna trend/breakout strategija kao usporedni kandidat:** smjer trenda, proboj kanala i ATR zaštita, s malo parametara. U istim uvjetima usporediti s postojećim patternima. To je inspiracija, ne tvrdnja o točnoj reprodukciji autorove strategije.
3. **Provjera pojedinačnih filtara:** zasebno mjeriti donosi li npr. ADX ili dodatna potvrda poboljšanje izvan razdoblja optimizacije, umjesto samo manjeg broja tradeova ili ljepše povijesne krivulje.
4. **Testovi otpornosti i kontrole odabira:** susjedni parametri, različita tržišna razdoblja, veći troškovi/slippage i kašnjenja kroz odgovarajuće replay scenarije. Evidentirati višestruko testiranje; ne podešavati strategiju prema završnom holdoutu. Odvojiti backtest, naknadnu simulaciju na novim podacima, stvarni shadow i brokerski paper rezultat.
5. **Praćenje slabljenja strategije:** unaprijed definirani kriteriji upozorenja, pauziranja i ponovnog odobravanja. Pauza novih ulaza mora očuvati zaštitu i lifecycle otvorenih pozicija; AI može pomagati analizom, a odluke i risk ograničenja moraju biti provjerljivi.
6. **Usporedba i povezanost strategija:** provjeriti donose li kandidati različite informacije i gube li istodobno; veći broj strategija nije dokaz diversifikacije. Ne pretpostavljati da postojeći pojedinačni replay već simulira zajednički portfelj.

Ograničenja: zadržati .NET/Blazor, postojeći replay, ML.NET, risk/arbitration i order lifecycle; SPY i ostali instrumenti ostaju podržani. Ne prenositi kripto strategije sa satnih svijeća na sekundni EUR/USD bez nove provjere. FX quote count/size nisu stvarni trade volumen. Ne preuzimati autorovu polugu, sizing ni tvrdnje o prinosu. Ne kupovati članstvo niti uvoditi crypto u okviru ovog dogovora. Prvi eksperiment može se temeljiti na javno opisanoj ideji; konačna neto prednost mora se dokazati našim podacima i troškovima.

Izvori pregleda: [poslani video](https://www.youtube.com/watch?v=-bGy0X4uVww&t=365s), [dodatni vodič](https://www.youtube.com/watch?v=KCvSmtKngyQ), [AI Trading Arena](https://aitradingarena.com/). Pregledani su transkripti i javni prikazi; zatvoreni izvorni kod i neovisno verificirani cjeloviti brokerski rezultati nisu provjereni. Prikazani prinosi nisu potvrđena očekivanja našeg bota.

## Dorada korisničkog sučelja nakon trenutačnog plana

**Dogovor 20.11.2026.:** nakon završetka trenutačnog plana detaljnije razraditi doradu postojećeg Blazor sučelja kako bi bilo jasno, pregledno i razumljivo korisniku. Ovo je zasebna odgođena stavka, bez početka implementacije ili promjene redoslijeda aktualnog rada.

Tada zajedno pregledati postojeće ekrane i korisničke zadatke te dogovoriti navigaciju, prioritete prikaza, razumljive nazive i objašnjenja statusa. Posebno razmotriti jasnoću prikaza prikupljanja podataka, strategija, istraživačkih rezultata i stanja trgovanja, uz vidljive razloge blokada i potrebne sljedeće korake. Konkretan opseg, izgled i redoslijed zahvata dogovoriti u toj fazi; postojeće funkcionalnosti ostaju sačuvane.
