# Pregled dovršenosti i redoslijed nastavka

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
- Evaluacija odbija prekratak raspon. `DeterministicReplayService.Forex.cs` učitava input u memoriju, ograničava ga na **200.000 događaja i sedam dana**, a `ResearchEvaluationService.cs` za FX zahtijeva jedan dovršeni `ReplayRunId`. Ova dva sata već sadrže 223.886 događaja. Samo dulje prikupljanje ne rješava taj put evaluacije.
- Pretraga `StoreCompletedTradeAnalysisAsync` nalazi ugovor, implementaciju i test, **bez produkcijskog pozivatelja**. Postojanje servisa nije automatska analiza svakog zatvorenog tradea. To ne znači da nema drugih trade/execution evidencija.
- Stari `CandleHistoryService` i `MarketSnapshotService` put još nema eksplicitnu `asOf` granicu u čitanju posljednjih barova. FX paritet na provjerenom uzorku ne dokazuje isti rezultat za sve druge putove.

Detalji i ograničenja rezultata: [dnevni izvještaj](FOREX_COLLECTION.md#analiza-dnevne-sesije--20112026) i [strojno čitljiv dokaz](evidence/fx12-day-session-analysis-2026-11-20.json). U ovom pregledu nisu slani nalozi, pokretan novi kolektor ni mijenjana operativna konfiguracija.

## Predloženi redoslijed

### N1 — Razjasniti valjanost kotacije i nedostajuće podatke

Razdvojiti vrijeme tržišnog događaja, lokalnog primitka, zadnje valjane kotacije i stvarnog prekida veze. Utvrditi kada posljednja poznata kotacija smije ostati dostupna za odluku, a kada nema dokaza za fill/exit. Ne povećavati prag od tri sekunde samo da bi nestali nepotpuni rezultati; ne izmišljati tickove ili volumen. Ako nema dovoljno podataka za pouzdanu razliku, zadržati neizvjestan ishod i prikazati njegov utjecaj na rezultat.

**Završetak:** dokumentirana pravila i testovi mirnog tržišta, stale kotacije, prekida/reconnecta i granica dataseta; ponovljena analiza postojeća četiri nepotpuna tradea uz nove verzije, bez prepisivanja starih dokaza. Sljedeća preporučena razvojna točka.

### N2 — Omogućiti višednevni, ponovljiv research dataset i replay

Odabrati ograničeno streaming procesiranje s nastavkom stanja ili verzionirani skup kompatibilnih replay segmenata. Sačuvati warmup, otvorene pozicije i rokove gdje postoji kontinuitet; eksplicitno označiti reset i stvarni gap. Ukloniti duplikate i spriječiti skriveno resetiranje kapitala. Povezati manifest/hashove i verzije s evaluacijom, treningom i odobrenjem modela. Ne zaobići ograničenje ručnim spajanjem SQLite redaka ili pukim povećanjem limita u memoriji.

**Završetak:** reprezentativan višednevni ulaz radi uz ograničenu memoriju; prekid/nastavak i različite veličine segmenta daju isti rezultat; nekompatibilni i preklopljeni inputi ne prolaze neopaženo. Moguće su odvojene train/validation/holdout cjeline bez leakagea. Ovo uklanja glavnu tehničku blokadu FX-10/25.

### N3 — Izmjeriti i ukloniti preostale zastoje prikupljanja

Dodati trajanja pojedinih faza obrade: queue, quality provjera/baza, pohrana i agregati. Mjeriti raspodjelu p50/p95/p99 i maksimum te ih povezati s TWS prekidima i opterećenjem procesa. Popravljati potvrđeni uzrok. Provjeriti reconnect, restart, sinkronizaciju sata i nadzor diska/logova; zadržati granice kvalitete i postojeće podatke.

**Završetak:** ponovljiva dnevna sesija s unaprijed zadanim operativnim pragovima, objašnjenim outlierima i dokazom da promjena ne mijenja featuree/signale. Prikupljanje može teći dok radimo N1/N2, uz zasebne verzije i metapodatke.

### N4 — Zatvoriti praznine između signala, simulacije i stvarnog tradea

Za EUR/USD povezati zatvoreni trade s točno jednim automatskim post-trade zapisom; kasna provizija mora ažurirati rezultat bez dupliciranja. Provjeriti isti feature i `asOf` kontekst u live/replay putu, risk i izlaze, uključujući djelomične fillove, zaštitu, restart i reconciliation. Sačuvati stock regresije; preostale R09/R13/R14 nalaze provjeravati u aktualnom kodu, ne prenositi stare tvrdnje kao činjenice.

**Završetak za prvi FX paper test:** trag input → signal → risk → order → execution → zatvoreni trade/post-trade za sve podržane izlaze, dokaz na fake brokeru i jasne preostale brokerske provjere. Potpuni zajednički portfolio replay ostaje zaseban N10 kriterij; sama ova točka ne zatvara cijelu točku 25.

### N5 — Prikupiti i zamrznuti reprezentativne podatke

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
