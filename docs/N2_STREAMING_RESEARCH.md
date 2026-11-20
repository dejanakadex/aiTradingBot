# N2 — kontinuirani višednevni FX research

20.11.2026. Tehnička implementacija i offline provjere dovršene; [strojno čitljiv dokaz](evidence/n2-streaming-research-2026-11-20.json). N2 uklanja ograničenje od 200.000 događaja / sedam dana u **FX trade replayu i snapshot izvozu**, bez povećavanja starog memorijskog limita. Zasebni feature replay i collection audit nisu ovim postali neograničeni. N3 nije započet; FX-12 i puni opseg 25/30/31 ostaju otvoreni.

## Obrada i kontinuitet

Parquet se čita po row grupama i predaje u blokovima od 1–16.384 događaja (zadano 4.096). Novi zapis koristi row grupe od 32.768 redaka; reader odbija postojeće grupe veće od 65.536 prije deserializacije. Takav vanjski input treba ponovno izvesti u kompatibilne grupe, uz novi manifest i očuvanje izvornika. Potreban je slobodan prostor na lokalnom privremenom disku.

Privremeni SQLite indeks sortira događaje po receive vremenu/ID-u i povezuje bid/ask parove. Identični duplikati se broje i obrađuju jednom; konfliktni ID, nepotpun par, regresija event vremena, nesklad retka s manifestom ili promjena izvora prekidaju obradu. Manifest se provjerava prije i nakon učitavanja. Izvozni `fx-snapshot` kopira hashom provjerene datoteke u nov direktorij; ne spaja proizvoljne nekompatibilne izvore.

Jedan agregator, warmup, signalni cooldown i red rokova vrijede kroz sve blokove. Zasebni portfolio prolaz čuva kapital, dnevni gubitak i zauzetost pozicijom kroz sve stranice kandidata. Dnevni risk brojač resetira se po postojećem UTC pravilu, kapital ne. Stvarna praznina u kotacijama ostaje N1 coverage problem. Ne nadopunjavamo izvršne kotacije niti zatvaramo poziciju izmišljenom cijenom: nepoznata otvorena pozicija zauzima portfelj do kraja zadanog raspona.

ATR i quote prozori čitaju se iz indeksa prema trenutku odluke. Kandidati se obrađuju po 512 redaka, a izlazni hash računa se redak po redak. Izvještaj ima zasebnu granicu od 250.000 neodbijenih kandidata; postojeći evaluator također ostaje ograničen. Manifest metapodaci i konačni ograničeni izvještaj ostaju u memoriji. Ovo nije tvrdnja o neograničenoj količini datoteka ili konstantnoj ukupnoj memoriji.

## Verzije i oporavak

Verzije su `fx-stream-replay-v1`, `fx-trade-stream-v1` i `labels-fx-execution-v3+config-SHA256`. Ulazni hash uključuje instrument/raspon, manifest, kanonski sadržaj događaja i zamrznuti N1 dokaz prekida. Veličina bloka nije dio semantičkog hasha. Ugovor labela uključuje konfiguraciju i verziju simulatora. Stari v1/v2 rezultati ostaju čitljivi povijesni dokazi; novi trening zahtijeva aktualni replay.

Svaki pokušaj dobiva neprepisivi zapis `<research.db>.fx-manifests/<run-id>.json`: raspon, izvorni manifest, hashove, ugovorne verzije, coverage i vezu s prethodnim pokušajem. Čuvati ga zajedno s bazom, konfiguracijom i Parquet snapshotom. ReplayRunId i hashovi labela prenose taj identitet kroz postojeću evaluaciju/trening/odobravanje. Nije potrebna migracija baze.

`fx-replay-status` prikazuje posljednjih 20 FX pokušaja. Tijekom offline obrade status je `Paused` kako ga generički background replay worker ne bi preuzeo; napredak pokazuje obrađene događaje. Nakon urednog završetka je `Completed`, a uhvaćena pogreška daje `Faulted`. Neočekivano ugašen proces može ostaviti `Paused` pokušaj. Sam status nije dokaz da je proces prestao — prije oporavka provjeriti da stari CLI više ne radi.

`--resume-run-id ID` **rekonstruira cijeli pokušaj od početka u novi RunId**, uz identične zamrznute podatke, raspon i istraživački ugovor. Stari djelomični zapisi ostaju nepromijenjeni i ne smiju u trening. To nije nastavak sa serijaliziranog položaja otvorene pozicije; ponavlja se CPU rad. Provjereno je da rezultat odgovara neprekinutom pokušaju, i s drugom veličinom bloka. Pokušaj s promijenjenim inputom/rasponom/ugovorom odbija se. Privremeni indeks briše se pri urednom izlasku; nakon nasilnog gašenja može ostati točno imenovana `aitradingbot-fx-*.db` datoteka u sistemskom Temp direktoriju, koju ukloniti tek nakon provjere da pripada ugašenom pokušaju.

Primjer nakon zamrzavanja dataseta i pripreme zasebne konfiguracije/baze:

```powershell
dotnet run --project TradingBot.ModelOps -- fx-replay --db C:/research/fx.db --config C:/research/fx.json --dataset C:/research/quotes --instrument-id FX-CASH-EUR-USD-IDEALPRO --strategy-id eurusd-patterns --from 2026-11-19T22:58:10Z --to 2026-11-20T11:00:00Z --coverage C:/research/coverage.json --batch-size 4096
dotnet run --project TradingBot.ModelOps -- fx-replay-status --db C:/research/fx.db --config C:/research/fx.json
```

Za oporavak ponoviti prvu naredbu s istim ulazima i dodatkom `--resume-run-id <nepotpuni-ID>`. Ne pokretati nad operativnom bazom niti miješati različite pokušaje ručnim kopiranjem SQLite redaka.

## Rezultati provjere

- **773/773 testa**, bez neizvršenih; obvezni IBKR publish i missing/changed-DLL provjere prolaze. Nije otvorena brokerska veza.
- Blokovi 1, 17 i 4.096 u testovima daju isti rezultat; prekid nakon djelomičnih kandidata pa novi pokušaj s blokom 7 daje isti hash kao neprekinuti replay i zadržava stari pokušaj. Duplicirani i konfliktni događaji te nekompatibilan oporavak imaju testove.
- Kapacitet: 200.002 sintetska događaja, kompatibilne row grupe i osmodnevni traženi raspon prolaze. Događaji su unutar deset sekundi: ovo je test uklanjanja limita, **ne osam dana reprezentativnog tržišta**.
- Četiri sintetske dnevne sesije u jednom replayu daju vremensku evaluaciju s train/validation/holdout podjelom i postojećim purging pravilima. Minima od jednog uzorka služe isključivo testu; nisu stvarno odobrenje modela.
- Stvarni 20.11. 09–11 UTC: **223.922 događaja / 111.961 par**, jedan kontinuirani replay. 50.000 virtualnih USD: 94 prihvaćena signala, 8 fillova, 7 zatvorenih timer tradeova, **−40,6763 USD zatvorenog podskupa**, jedna fillana pozicija bez poznatog završetka. Ostalih 84 signala preskočeno je zbog zauzetosti; postoje i dva odbijena/nepotpuna pokušaja bez fill-a. Nije potpuni portfeljni prinos.
- Blokovi 4.096 i 257 na istom stvarnom snapshotu imaju jednake input/output hashove i rezultate. Izmjereno približno 72–73 s; opaženi vrh working seta oko 180 / 175 MiB. To je mjerenje ovog uzorka/procesa, ne jamstvo za svaki dataset ili produkcijsku propusnost.
- 100 USD na istom rasponu: **0 fillova**. Nisu ublaženi minimumi, risk ni troškovi.
- Dodatni stvarni raspon 19.11. 22:58:10 → 20.11. 11:00 UTC: **237.540 događaja**, kontinuirani prelazak UTC ponoći s četiri zamrznuta zapisa prekida. Jedna fillana noćna pozicija ima nepoznat izlaz; nema zatvorenih tradeova. To su približno 12 sati preko dva datuma, **ne više punih reprezentativnih dana**.
- Ponovljeni stari N1 polusatni snapshot daje **identične sve trade rezultate**. Cijela dva sata imaju 36 događaja više nego zbroj starih prozora jer više ne odbacuju događaje primljene odmah nakon unutarnje polusatne granice. Promjena ukupnog portfeljnog rezultata dolazi iz uklanjanja resetiranja, ne poboljšanja strategije.
- Stvarna evaluacija dva sata i dalje odbija: `Evaluation range is too short for the configured training, validation and untouched holdout windows.` Nema stvarnog treninga, approvala ni naloga.

Lokalni artefakti: `TestResults/n2-20261120`, testovi `TestResults/n2-validation/n2-full.trx`. Izvorni N1 i FX-12 artefakti, operativna baza i konfiguracija ostaju sačuvani.

## Što ostaje

N2 tehnički omogućuje višednevnu evaluaciju jednog kontinuiranog replaya. Reprezentativne stvarne sesije i važeća povijesna pravila tek treba osigurati kroz N5; njihova statistička prihvatljivost nije dokazana ovim testovima. Nepotpune pozicije ne smiju se automatski resetirati kako bi istraživanje izgledalo uspješnije. N4/N10 i dalje nose potpuni produkcijski order/fill/exit i zajednički portfolio paritet. N3 je sljedeći planirani zahvat, uz zasebno odobrenje korisnika. Nema potvrđene profitabilnosti niti zatvaranja FX-12.
