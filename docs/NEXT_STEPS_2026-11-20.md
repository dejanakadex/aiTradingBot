# Pregled dovršenosti i redoslijed nastavka

**N5 nastavak, 25.11.2026.:** implementiran je offline `fx-derive`: izvor ostaje nepromijenjen, izvedeni skup nosi potpuni popis izuzetih dijagnostičkih zapisa i obvezno vezane prekide. Stvarni skup 24.11. daje isti hash značajki i isti broj finalnih barova; **842/842 testa** i IBKR publish/guards prolaze. Prekinuti 25.11. nije prihvaćen: 120 heartbeat timeout faza, planirana pauza i nepotpun par označen Healthy; neobične spremljene cijene zahtijevaju dodatnu provjeru. Nova buduća rezervacija predviđa trening 26./27.11., validaciju 30.11. i holdout 1.12., uz nepromijenjene kriterije i uvjet stvarnog prihvata podataka. Kolektor je ponovno pokrenut bez naloga. **N5 ostaje otvoren.** [Detalji](N5_DATASET_READINESS.md#nastavak-i-izvedeni-skup--25112026), [dokaz](evidence/n5-resume-2026-11-25.json).

**N5 dnevna provjera, 24.11.2026.:** [prvi fiksni trening prozor 09–17 Zagreb](N5_DATASET_READINESS.md#cijela-prva-dnevna-sesija--24112026) zadovoljava kriterij pokrivenosti: **5.745/5.759 zdravih 5s barova (99,76%)**, 480 opaženih minuta i 5.520 zagrijanih frameova. Novi kontinuirani offline audit čuva prekide i izričito evidentira odbijene dijagnostičke zapise; sirovi skup time nije odobren za izvršni replay. **828/828 testova**, jutarnji paritet i IBKR guardovi prolaze. N5 ostaje otvoren. Dnevni dataset flush doseže 12,11 s, durable čekanje 23,50 s; spora faza je zapis punog manifesta, ne dokaz SQLite zastoja. Pravila osvježena do 25.11. 17:03 Zagreb. Kolektor nastavlja bez naloga i bez promjene runtimea. [Dokazi](evidence/n5-day-session-2026-11-24.json).

**N5 jutarnja provjera, 24.11.2026.:** [noćno prikupljanje i početak trening prozora](N5_DATASET_READINESS.md#jutarnja-provjera--24112026) potvrđuju aktivan read-only kolektor. TWS je bio nedostupan 23:45–01:03 Zagreb; korisnik potvrđuje naknadno ručno pokretanje. Noćni raw snapshot ima dva nepotpuna dijagnostička para već označena `Stale/CanPersist=false`, pa strogi replay opravdano odbija cijeli sirovi ulaz. Prvih 20 minuta današnje sesije ima 22.957 potpunih parova, 238/239 zdravih 5s barova i 188 zagrijanih frameova; jedan bar potisnut je zbog zakasnjelih kotacija. Brokerska pravila obnovljena su prije isteka, bez promjene starih vremena. **N5 ostaje otvoren**, rezervirane podjele nisu mijenjane. Noćna queue/storage kašnjenja prelaze dio pragova; nema naloga, approvala ili promjene runtimea. [Dokazi](evidence/n5-overnight-2026-11-24.json).

**N5 priprema, 23.11.2026.:** [zamrznuti podaci i rezervirane podjele](N5_DATASET_READINESS.md) dodani su uz offline streaming pregled; **816/816 testova** i IBKR publish/guards prolaze. Kopija s 2.053.027 manifest redaka sačuvana je uz 82 coverage zapisa. Širi replay ulaz ispravno je odbijen zbog 33 nepotpuna jutarnja para; zasebno su provjerena 717.375 potpunih parova 20.11. i 134.203 para kasnije 23.11. Za kasniji 23.11. uzorak nedostaju tada važeća pravila; novo današnje opažanje nije retroaktivno. Budući trening/validacija/holdout unaprijed su rezervirani 24.–28.11. **N5 ostaje otvoren** do stvarnog prikupljanja i prihvata reprezentativnih sesija. Nema novih strateških rezultata, approvala ili naloga. [Dokaz](evidence/n5-preparation-2026-11-23.json).

**N4, 23.11.2026.: završeno implementacijom i simulacijom.** [Signal → execution → automatski post-trade](N4_TRADE_LIFECYCLE.md) sada daje jedan rezultat po zatvorenom tradeu, ponderirane fillove i ažuriranje kasnih provizija bez duplikata. Sačuvan je izvorni feature/model/risk kontekst; stock history/snapshot dobio je strogu `asOf` granicu. **810/810 Release testova**, IBKR publish/guards i offline feature/decision parity prolaze. Migracija i read-only kolektor provjereni su nakon restarta u 14:45 Zagreb, bez naloga. N5 je sljedeća planirana točka. Stvarni paper ciklus, model shadow, FX-12 i puni 25/30/31 ostaju otvoreni. [Strukturirani dokaz](evidence/n4-trade-lifecycle-2026-11-23.json). Stariji checkpointi niže su povijesni.

**N3 prihvat, 23.11.2026.:** [Dnevna sesija i paritet](N3_COLLECTION_LATENCY.md#prihvatna-sesija-i-paritet--23112026) potvrđuju završetak N3 za AnalysisOnly EUR/USD prikupljanje: 69 minutnih prozora, pipeline max 45,77 ms, queue max 40,73 ms, bez gapova/dropova/grešaka; 73.047 potpunih parova i 836/836 5s barova. Paritet stvarnih kotacija i kontroliranog prihvaćenog signala provjeren je; **803/803 testa** prolaze. Pozadinski SQL i dalje može trajati oko 2 s, pa paper izvršavanje nije time potvrđeno. N4 je sljedeći odobravani korak; FX-12 i 25/30/31 ostaju otvoreni. Nema naloga ni model approvala.

**N3 quality checkpoint, 23.11.2026.:** [Periodično spremanje izvedenog stanja](N3_COLLECTION_LATENCY.md#periodično-spremanje-quality-stanja--23112026) implementirano je i uključeno samo u AnalysisOnly EUR/USD kolektoru (1 s / maksimalno pending 5 s). Svaka kotacija i dalje se provjerava; incidenti se spremaju odmah, a neuspjeh ili zaostajanje blokira tok. Paper/live zadržava per-event spremanje. **802/802 testa** i IBKR guards prolaze; nova verzija je pokrenuta uz zabrane naloga. Potrebna je nova ≥60-minutna prihvatna sesija; **N3, FX-12 i 25/30/31 ostaju otvoreni**, N4 nije započet.

**N3 NVMe test uz aktivni feed, 23.11.2026.:** [dvadesetominutni test i povratna faza](N3_COLLECTION_LATENCY.md#nvme-usporedba-uz-aktivni-feed--23112026) završeni su uz isti PID i bez prekida. Pipeline maksimum je 986 ms uz NVMe AC 0/0, a 909 ms nakon vraćanja Balanced plana; **obje faze ne prolaze N3**. Potvrđena su 28.394 potpuna para i svih 479 očekivanih 5s barova, bez gapova/dropova. Izvorne postavke su vraćene, nema naloga. Slijedi pregled blokirajućeg spremanja quality stanja; trajna promjena napajanja nije opravdana ovim rezultatom.

**N3 stabilna sesija, 20.11.2026.:** [analiza i SQL dopuna](N3_COLLECTION_LATENCY.md#stabilna-večernja-sesija-i-dopuna-sql-mjerenja--20112026): 77.772 potpuna EUR/USD para, svih 1.259 finalnih 5s barova i 1.234 zagrijana framea. Nema gapova/dropova, ali 17 minuta ima lokalni pipeline maksimum >250 ms (najviše 1,883 s). Novo ReaderLifetime mjerenje stvarno je uhvatilo 1,553 s u dovršavanju UPDATE-a. Dublji I/O uzrok nije još dokazan; **N3 ostaje otvoren**. 781/781 testova i IBKR publish guard prolaze. Nema naloga ni model approvala.

**N3 checkpoint, 20.11.2026.:** [fazna mjerenja i stanje provjere](N3_COLLECTION_LATENCY.md) uvedeni su uz ispravak vremenskog flush-a i urednog završetka zapisa pri stopu. Nova mjerenja potvrđuju da gotovo dvije sekunde mogu nastati u SQLite spremanju quality stanja; dublji uzrok nije još dokazan. 780/780 .NET testova, 2 testa izvještaja i nepromijenjen N2 replay rezultat; stvarna prihvatna sesija i uklanjanje outliera još nedostaju. **N3 ostaje otvoren**, N4 nije započet; nema naloga ni model approvala.

**N2, 20.11.2026.:** [kontinuirani streaming research i dokazi](N2_STREAMING_RESEARCH.md) implementirani su i offline provjereni: 773/773 testa, isti rezultat pri različitim blokovima i oporavku u novi pokušaj. Stvarni dvosatni replay ima 223.922 događaja bez resetiranja portfelja; provjeren je i prelazak ponoći. Aktualne FX labele su v3. Reprezentativni višednevni podaci/evaluacija ostaju N5/N7; N3 je sljedeća planirana točka. FX-12 i puni opseg 25/30/31 ostaju otvoreni. Nema approvala ili naloga.

**N1 dopuna 20.11.2026.:** [pravila kotacija i ponovljena analiza](N1_QUOTE_COVERAGE.md) implementirani su i lokalno provjereni s 764/764 testa. Četiri izlaza ostaju opravdano nepoznata; zabilježeni TWS prekid sada se zasebno koristi u simulaciji. N1 commit `ed5355e` prošao je CI; N2 dopuna iznad ima prednost. N3–N12 ostaju plan.

Pregled 20.11.2026., kod `cd1e653`, zajednički branch `trading-bot-v2`. Ovo je prijedlog redoslijeda za odobravanje **jedne točke po jedne**, a ne početak implementacije ili dopuštenje za naloge. Zadržavamo .NET 10, Blazor, SQLite/EF, Parquet, ML.NET/LightGBM, postojeći risk/arbitration/lifecycle te stock i FX podršku. Live, short proširenje i crypto nisu dio ovog nastavka.

## Zaključak provjere

**Nije sve ranije planirano završeno.** Velik dio softverske infrastrukture je implementiran i testiran, ali nemamo dovršen stvarni model shadow, brokerski paper ciklus ni dokaz održive neto prednosti. Broj završenih razvojnih točaka nije postotak puta do profitabilnosti.

| Raniji plan | Potvrđeno | Što nije potvrđeno ili je još otvoreno |
| --- | --- | --- |
| 1–18 | Implementacija glavnih servisa i lokalni testovi prema checkpointima | Svi operativni kriteriji i stvarni rezultat strategija; posebno automatska post-trade analiza i cjelovita vremenska konzistentnost svih putova |
| 19–24 | Obvezni službeni IBKR DLL, adapter, feed, agregati, signalni tok, bracket i neovisni timer; lokalni testovi i stvarni FX feed | Stvarni fill/zaštita/izlaz na IBKR-u; stock entitlement nije dokazan FX feedom |
| 25 | Deterministički research replay, troškovi, virtualni ulazi/izlazi i izvršne labele; dnevni EUR/USD uzorak | Kontinuirani višednevni replay, razjašnjena pokrivenost kotacijama, paritet cijelog produkcijskog lifecyclea i zajedničkog portfelja; nije dovršena točka |
| 26–29 | Vremenska evaluacija, model workflow, onboarding i deterministički servisni E2E s fake brokerom | Reprezentativna EUR/USD evaluacija, stvarno treniran/odobren model, stvarni onboarding do PaperReady i brokerska potvrda |
| 30 | TWS veza, stvarne EUR/USD kotacije, mjerenja prikupljanja | Model shadow odluke i njihovi kriteriji; povremeni lokalni zastoji nisu razjašnjeni |
| 31 | Postoje testni postupci | Stvarni IBKR paper entry → fill → zaštita → exit nije proveden |
| FX-1–FX-9 | Konfiguracija, ugovori, quote obrada, featurei/patterni, risk i long USD-quoted izvršni put; lokalni testovi, stvarni ugovori/feed | Stvarno izvršavanje FX naloga, provizije, margina i callback ponašanje |
| FX-10–FX-11 | Research/model i onboarding mogućnosti implementirane | Operativna višednevna evaluacija/model/shadow nisu dovršeni |
| FX-12 | Kolektor, trajna pohrana, audit, izolirane research konfiguracije, read-only preflight i paper protokol | Model/shadow preduvjeti i ograničeni stvarni paper test; ostaje otvoren |
| Odgođeni prijedlozi | Zapisani | DaviddTech/AI Trading Arena inspirirani eksperimenti, preglednije sučelje i povratak na ETF istraživanje nisu implementirani; crypto ostaje za kasnije |

Dokazi se oslanjaju na aktualni kod, [status](IMPLEMENTATION_STATUS.md), [FX plan](EURUSD_READINESS.md), [FX-10](FX10_RESEARCH.md), [FX-11](FX11_ONBOARDING.md), [paper protokol](FX12_PAPER_PROTOCOL.md) i spremljene rezultate. Ovo nije ponovljeni potpuni sigurnosni pregled svakog retka koda. Povijesni nalazi R01–R16 ne predstavljaju automatski današnje greške: npr. `MarketDataValidator` sada uvažava trajanje 1m/5m/15m bara, a IBKR DLL je obvezan i pohranjen u repozitoriju.

### Aktualna provjera i mjerljivi rezultati

- Ponovljeno `dotnet test TradingBot.sln -c Release --no-restore`: **741/741 prolazi**, bez neizvršenih testova. Lokalni TRX: `TestResults/plan-review-20261120/plan-review-20261120.trx`. To nije novi CI rezultat ni stvarni brokerski test.
- Dnevna sesija 20.11. 10–12 Zagreb: **111.943 potpuna bid/ask para**, 1.433/1.436 finalnih 5s barova u četiri odvojena prozora, 1.283 framea s potrebnim warmupom. Raniji problem gotovo potpunog nedostatka 26 uzastopnih barova ne opisuje ovaj uzorak. Nije dokazana neprekidna višednevna pouzdanost.
- Scenarij **100 USD nije proizveo fill** uz pregledane veličine i konfigurirani risk. Virtualnih **50.000 USD služi tehničkom testu**, nije preporuka uplate niti dokaz izvedivosti sa 100 USD.
- U većem scenariju ima 25 simuliranih ulaza, **21 zatvoreni trade, svih 21 neto negativnih**, ukupno **−133,03 USD zatvorenog podskupa** nakon provizija i safety buffera. Četiri pozicije imaju nepoznat završetak. Četiri batcha resetiraju stanje; zbroj nije potpuni kontinuirani portfeljni prinos. Svi potvrđeni izlazi su vremenski: ovaj uzorak nije empirijska potvrda SL/TP izvršenja.
- Četiri nepotpuna izlaza nastaju nakon 3,10–4,41 s bez nove kotacije. To nije samo po sebi dokaz izgubljene veze. Zasebno postoji potvrđen TWS prekid; događaje ne smijemo poistovjetiti.
- Lokalni pipeline ima vrh **1,88 s**. Uzrok nije dokazan; ranija SQL optimizacija ne znači da su svi zastoji riješeni.
- Povijesni nalaz prije N2: evaluacija odbija prekratak raspon. `DeterministicReplayService.Forex.cs` tada učitava input u memoriju, ograničava ga na **200.000 događaja i sedam dana**, a `ResearchEvaluationService.cs` za FX zahtijeva jedan dovršeni `ReplayRunId`. Ova dva sata već sadrže 223.886 događaja. Samo dulje prikupljanje ne rješava taj put evaluacije.
- Pretraga `StoreCompletedTradeAnalysisAsync` nalazi ugovor, implementaciju i test, **bez produkcijskog pozivatelja**. Postojanje servisa nije automatska analiza svakog zatvorenog tradea. To ne znači da nema drugih trade/execution evidencija.
- Stari `CandleHistoryService` i `MarketSnapshotService` put još nema eksplicitnu `asOf` granicu u čitanju posljednjih barova. FX paritet na provjerenom uzorku ne dokazuje isti rezultat za sve druge putove.

Detalji i ograničenja rezultata: [dnevni izvještaj](FOREX_COLLECTION.md#analiza-dnevne-sesije--20112026) i [strojno čitljiv dokaz](evidence/fx12-day-session-analysis-2026-11-20.json). U ovom pregledu nisu slani nalozi, pokretan novi kolektor ni mijenjana operativna konfiguracija.

## Predloženi redoslijed

### N1 — Razjasniti valjanost kotacije i nedostajuće podatke

Status: **implementirano i lokalno provjereno**; [dokaz i granice](N1_QUOTE_COVERAGE.md). Prihvat N1 dopušta zadržavanje neizvjesnog ishoda kada nedostaje dokaz; ne zahtijeva izmišljanje izlaza. Sljedeća razvojna točka je N2, nakon potrebne CI potvrde i odobrenja nastavka.

Razdvojiti vrijeme tržišnog događaja, lokalnog primitka, zadnje valjane kotacije i stvarnog prekida veze. Utvrditi kada posljednja poznata kotacija smije ostati dostupna za odluku, a kada nema dokaza za fill/exit. Ne povećavati prag od tri sekunde samo da bi nestali nepotpuni rezultati; ne izmišljati tickove ili volumen. Ako nema dovoljno podataka za pouzdanu razliku, zadržati neizvjestan ishod i prikazati njegov utjecaj na rezultat.

**Završetak:** dokumentirana pravila i testovi mirnog tržišta, stale kotacije, prekida/reconnecta i granica dataseta; ponovljena analiza postojeća četiri nepotpuna tradea uz nove verzije, bez prepisivanja starih dokaza. N1 dovršen; sljedeća razvojna točka nakon N2 je N3.

### N2 — Omogućiti višednevni, ponovljiv research dataset i replay

Status: **tehnička implementacija i offline provjere dovršene**; [N2 dokaz i ograničenja](N2_STREAMING_RESEARCH.md). Višednevni kontinuitet/evaluacija provjereni su sintetski; stvarni dokaz pokriva dva sata i zaseban prelazak ponoći. Reprezentativne pune tržišne dane još treba prikupiti i validirati u N5. Sljedeći tekst čuva izvorne kriterije.

Odabrati ograničeno streaming procesiranje s nastavkom stanja ili verzionirani skup kompatibilnih replay segmenata. Sačuvati warmup, otvorene pozicije i rokove gdje postoji kontinuitet; eksplicitno označiti reset i stvarni gap. Ukloniti duplikate i spriječiti skriveno resetiranje kapitala. Povezati manifest/hashove i verzije s evaluacijom, treningom i odobrenjem modela. Ne zaobići ograničenje ručnim spajanjem SQLite redaka ili pukim povećanjem limita u memoriji.

**Završetak:** reprezentativan višednevni ulaz radi uz ograničenu memoriju; prekid/nastavak i različite veličine segmenta daju isti rezultat; nekompatibilni i preklopljeni inputi ne prolaze neopaženo. Moguće su odvojene train/validation/holdout cjeline bez leakagea. Ovo uklanja glavnu tehničku blokadu FX-10/25.

### N3 — Izmjeriti i ukloniti preostale zastoje prikupljanja

Status: **završeno za AnalysisOnly EUR/USD prikupljanje**, 23.11.2026. [Prihvatna sesija i paritet](N3_COLLECTION_LATENCY.md#prihvatna-sesija-i-paritet--23112026) prolaze kriterije N3. Fizički SQL zastoji ostaju opaženi u pozadini; paper/live per-event put i puni signal/model/order paritet provjeravaju se u N4 i prije brokerskog testa, bez tvrdnje da je SSD popravljen.

Dodati trajanja pojedinih faza obrade: queue, quality provjera/baza, pohrana i agregati. Mjeriti raspodjelu p50/p95/p99 i maksimum te ih povezati s TWS prekidima i opterećenjem procesa. Popravljati potvrđeni uzrok. Provjeriti reconnect, restart, sinkronizaciju sata i nadzor diska/logova; zadržati granice kvalitete i postojeće podatke.

**Završetak:** ponovljiva dnevna sesija s unaprijed zadanim operativnim pragovima, objašnjenim outlierima i dokazom da promjena ne mijenja featuree/signale. Prikupljanje može teći dok radimo N1/N2, uz zasebne verzije i metapodatke.

### N4 — Zatvoriti praznine između signala, simulacije i stvarnog tradea

**Status 23.11.: kriterij implementacije i fake-broker provjere završen; [dokazi i granice](N4_TRADE_LIFECYCLE.md). Stvarni brokerski kriteriji ostaju zasebni.**

Za EUR/USD povezati zatvoreni trade s točno jednim automatskim post-trade zapisom; kasna provizija mora ažurirati rezultat bez dupliciranja. Provjeriti isti feature i `asOf` kontekst u live/replay putu, risk i izlaze, uključujući djelomične fillove, zaštitu, restart i reconciliation. Sačuvati stock regresije; preostale R09/R13/R14 nalaze provjeravati u aktualnom kodu, ne prenositi stare tvrdnje kao činjenice.

**Završetak za prvi FX paper test:** trag input → signal → risk → order → execution → zatvoreni trade/post-trade za sve podržane izlaze, dokaz na fake brokeru i jasne preostale brokerske provjere. Potpuni zajednički portfolio replay ostaje zaseban N10 kriterij; sama ova točka ne zatvara cijelu točku 25.

### N5 — Prikupiti i zamrznuti reprezentativne podatke

**Status 23.11.: priprema i offline alati provjereni; buduće sesije i završni prihvat još nedostaju. [N5 dokazi i redoslijed](N5_DATASET_READINESS.md).**

Prikupiti više dnevnih sesija i različitih uvjeta spreada/volatilnosti. Sačuvati brokerova pravila važeća za promatrano razdoblje; ne produljivati stari snapshot promjenom vremena. Razriješiti ili jasno izuzeti označene povijesne gapove uz pošteno stanje onboardinga. Unaprijed rezervirati podatke za završnu provjeru. Razdvajanje mora uvažiti trajanje labela i preklapanje tradeova.

**Završetak:** nepromjenjiv verificiran dataset s manifestom, pravilima, izvještajem kvalitete i vremenskim podjelama. Konfigurirani minimalni broj dana/tradeova samo je donja granica, ne dokaz statističke pouzdanosti. Ne obećavati dovoljno podataka nakon jednog dana.

### N6 — Provjeriti ekonomsku izvedivost postojećih strategija

Prvo usporediti postojeći baseline s unaprijed ograničenim varijantama horizonta i izlaza, uz stvarne instrumentne minimume, realistične troškove, spread, kašnjenje i slippage. Rezultate za korisnikov budžet 100 USD odvojiti od virtualnog tehničkog kapitala. Prikazati broj odbijenih/nepotpunih slučajeva, neto očekivanje po tradeu, drawdown, izloženost, odnos prosječnog dobitka/gubitka i osjetljivost na troškove. Ne optimizirati samo postotak pobjeda.

**Završetak:** reproducibilan zaključak prihvati/odbaci za svaki unaprijed zapisan kandidat, s procjenom nesigurnosti i rezultatima na drugim vremenskim razdobljima. Ako konfiguracija nije izvediva sa 100 USD ili nema neto prednosti, to je rezultat istraživanja, ne razlog za popuštanje riska ili obećanje profita.

### N7 — Dovršiti stvarni EUR/USD model workflow

Tek na prikladnim podacima pokrenuti postojeću vremensku evaluaciju i trening; usporediti model s jednostavnim pravilima na istom ulazu. Provjeriti kalibraciju procjene, dostupnost featurea u trenutku odluke, troškove i sve pokušane varijante. Odobriti samo točnu verziju koja zadovoljava kriterije izvan treninga. Ako model ne pomaže, ne forsirati odobrenje ili zaobići gateove; dogovoriti daljnje istraživanje.

**Završetak:** reproduktivan izvještaj i hashovi podataka/modela/evaluacije; pozitivan prihvatni rezultat ili jasno odbijanje. Odobrenje nije automatski ishod ove točke. Završni holdout nakon korištenja za odabir više nije neviđen test.

### N8 — Dovršiti model shadow i readiness na novim kotacijama

Bez naloga prikupljati stvarne odluke odobrene verzije, razloge blokada, kašnjenja, troškove i naknadne simulirane ishode. Ponovno provjeriti onboarding i postojeće FX-11/30 pragove. Samo aktivna veza i mnogo tickova nisu model shadow. Pragovi broja odluka/trajanja nisu dokaz profitabilnosti.

**Završetak:** neovisna svježa sesija s potpunim dokazom odluka i prolazom kriterija; jasno navesti da se time zatvara FX opseg točke 30, a ne nedostajući SPY/QQQ feed i stock shadow.

### N9 — Ograničeni stvarni EUR/USD paper test

Pripremiti konkretan pregledljiv test po postojećem [FX-12 protokolu](FX12_PAPER_PROTOCOL.md): važeća pravila/margina, dopušten instrument/model, iznos i maksimalni rizik, početno stanje bez neželjenih naloga. Stvarno slanje naloga ide tek nakon potrebnog odobrenja tog konkretnog testa. Krenuti jednim ograničenim ciklusom; postupno potvrditi fill, zaštitu, SL/TP/timer, otkazivanje ostatka, obnovu veze i završno usklađenje. Kontrolirane scenarije koji se ne dogode u jednoj sesiji ne označavati testiranima.

**Završetak:** brokerski order/execution/commission trag, usklađena završna pozicija, nema zaostalih naloga, evidentirani svi traženi scenariji i odstupanja simulacije. FX-12 se zatvara samo prema svojim kriterijima; točka 31 jasno navodi koji su instrumenti/profili stvarno potvrđeni. Paper provjera potvrđuje integraciju, ne live likvidnost ni budući profit. Izmjerenim troškovima ponovno provjeriti backtest i eventualno povući raniju prednost.

### N10 — Dovršiti cijeli opseg točke 25 i proširiti istraživanje nakon FX-12

Dodavati jednu provjerljivu nadogradnju: zajednički portfolio replay s istim risk/arbitration/reservation i order/fill/exit pravilima, konkurentnim strategijama/instrumentima, odbijanjima i obnovom stanja. Dokazati rezultate pri različitom redoslijedu callbackova i ukupnoj zauzetosti kapitala. Zatim provesti već zapisani [odgođeni istraživački backlog](FX12_RESEARCH_PREPARATION.md#dogovoreni-prijedlozi-nakon-fx-12--20112026): registar svih eksperimenata, jednostavan trend/breakout kandidat, zasebne provjere doprinosa filtara, stres troškova/parametara, praćenje slabljenja strategije i povezanost strategija.

**Završetak:** točku 25 zatvoriti tek kada je dokumentiran i testiran puni dogovoreni simulacijski opseg; svaki novi kandidat ima rezultat izvan optimizacije i svjež forward/shadow dokaz. Više testiranih kombinacija zahtijeva strožu kontrolu slučajnog odabira pobjednika. Novi kandidati nisu unaprijed odobrene strategije.

### N11 — Doraditi sučelje i preostalu operativnu pouzdanost

Nakon trenutačnog plana s korisnikom razraditi Blazor navigaciju i razumljive prikaze: prikupljanje, istraživanje, model, shadow/paper, blokada i sljedeći korak; bruto/neto rezultat i nepotpuni slučajevi moraju biti vidljivi. Sačuvati postojeće mogućnosti. Prije šireg ili nenadziranog korištenja provjeriti backup/restore, kalendar, nadzor i pristup operativnim kontrolama. Ako se uključuje udaljeni LLM put, zatvoriti preostalu evidenciju svakog API pokušaja, retry i atomsko ograničavanje budžeta (R15); nije preduvjet lokalne FX LightGBM inferencije.

**Završetak:** dogovoreni korisnički zadaci prolaze kroz jasne ekrane, obnovljena baza stvarno radi, pauza ne uklanja zaštitu otvorenih pozicija, a operativne granice su provjerene. Detaljan UI plan tek treba dogovoriti, nije već izvršen.

### N12 — Vratiti se SPY/ETF-ovima i odluci o daljnjem radu

Sačuvati prethodni zaključak: bez tada potrebnih IBKR podatkovnih prava raditi na legalno dostupnim povijesnim podacima i odvojenom offline istraživanju; prije stvarnog ETF feeda ponovno potvrditi tada aktualne brokerske uvjete. FX rezultat ne prenosi se automatski na SPY. Za svaki instrument zasebno provjeriti podatke, troškove, model, shadow i paper; strategijski kod može se dijeliti uz instrumentne profile i zasebnu validaciju. Crypto ostaje odgođen.

**Završetak:** po instrumentu dokumentirana odluka nastaviti/izmijeniti/odbaciti. Eventualni live rad zahtijevao bi zaseban budući plan i odobrenje; nije uključen u ovaj prijedlog.

## Što bi bio dokaz napretka prema profitabilnosti

Tražimo pozitivno neto očekivanje nakon svih troškova, održivo na podacima koji nisu korišteni za odabir, uz prihvatljiv drawdown i izvediv kapital. Nesigurnost treba računati uz vremensku povezanost i preklapanje tradeova, ne tretirati sve labele kao neovisne zarade. Rezultat treba preživjeti umjereno nepovoljnije troškove i razdoblja te nove shadow/paper podatke. Paper izvršenje je dodatni dokaz integracije i modela troškova, ne jamstvo live rezultata.

Trenutačni rezultat to **ne zadovoljava**. Ne možemo obećati da će razvoj dovesti do profitabilne strategije, niti pouzdano odrediti datum ili postotak dovršenosti tog cilja. Možemo završiti pouzdan postupak koji razlikuje stvarnu prednost od pogreške simulacije ili prilagodbe prošlosti, te prestati ulagati u kandidate koji ga ne prođu.
