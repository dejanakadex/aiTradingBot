# N1 — valjanost kotacije i pokrivenost simuliranog tradea

20.11.2026.: **implementirano i lokalno provjereno**, uključujući ponovljenu analizu četiri nepotpuna izlaza. CI status provjerava se nakon pusha, na [GitHub Actions](https://github.com/dejanakadex/aiTradingBot/actions/workflows/ci.yml?query=branch%3Atrading-bot-v2) za commit ove promjene; lokalni prolaz nije zamjena za CI. N1 ne zatvara FX-12, točku 25 niti potvrđuje paper izvršenje ili profitabilnost.

## Pravila

`ForexReplayQuote` sada zadržava tri odvojena vremena:

- `EventUtc`: izvorno brokerovo vrijeme;
- `ReceivedUtc`: izvorno lokalno vrijeme primitka;
- `AvailableUtc = max(EventUtc, ReceivedUtc)`: najraniji trenutak dostupnosti u simulaciji, uključujući postojeću toleranciju malog pomaka sata.

Tolerancija sata ne prepisuje izvorna vremena i ne dopušta korištenje quotea prije njegove konzervativne dostupnosti. Posljednji quote smije poslužiti kao cijena **odluke** samo dok prolazi postojeću granicu starosti i nema zabilježenog prekida od njegova primitka. **Fill zahtijeva novu opaženu kotaciju**, postojeću simuliranu latenciju, cjenovne i risk uvjete. Samo protok vremena ne daje novu likvidnost, fill ili izlaz.

`ForexReplayCoveragePolicy` (`fx-quote-coverage-v1`) koristi postojeće granice `MaximumQuoteGapSeconds` i `MaximumSnapshotAgeSeconds`; dnevni scenarij ostaje na 3 s / 2 s. Jednakost s granicom prolazi, prvi tick iznad nje ne prolazi. Kontinuitet unutar dopuštenog razmaka je pretpostavka research scenarija, ne dokaz da nijedna tržišna promjena nije izgubljena. Nema interpolacije, izmišljenih svijeća, volumena ili popuštanja pragova.

| Razlog | Značenje |
| --- | --- |
| `NoDecisionQuote` | U trenutku odluke nema dostupne kotacije |
| `StaleDecisionQuote` | Posljednja kotacija prestara je za odluku |
| `RecordedInterruption` | Eksplicitni zamrznuti operativni dokaz bilježi prekid koji presijeca korištenje quotea/put tradea; vrijedi i za prekid kraći od tri sekunde |
| `CallbackSessionChanged` | Promijenjen identitet callback sesije; ne zaključujemo automatski uzrok promjene |
| `StaleReceivedQuote` | Novo primljena kotacija ima prestar događaj |
| `MarketSessionClosed` | Sesija prema postojećem FX kalendaru nije otvorena |
| `UnobservedQuoteInterval` | Nema dovoljno opaženih quoteova unutar granice razmaka; nije dokaz kvara veze niti potvrda mirnog tržišta |
| `QuoteDatasetBoundaryBeforeEntryExpiry` | Podaci završavaju prije isteka vremena za ulaz; nema dokaza za fill |
| `QuoteDatasetBoundaryWithOpenPosition` | Simulacija završava s nepotpuno zatvorenom pozicijom; nema dokazanog konačnog P/L |
| `NoFillWithinEntryWindow` | Dostupan ulazni prozor nije dao fill prema limitu; to se razlikuje od odsječenog uzorka |
| `UnresolvedResidual` | Preostala količina ne može se razriješiti prema simuliranim pravilima veličine |

Razlozi imaju prvenstvo: zabilježeni prekid, promjena callback sesije, starost novog quotea, zatvorena tržišna sesija, pa neopaženi razmak. Dijagnostika sadrži prethodnu i novu opaženu kotaciju, sva tri vremena, granice i pripadajući zapis prekida. `CheckedAtUtc` za završetak uzorka označava traženi kraj simulacijskog prozora, a ne dodatno opaženi quote. Stvarni zadnji opaženi trenutak ostaje u `PreviousQuote`.

Nepotpuni trade ostaje nepotpun, bez neto labele. Ne dodajemo ga u profit kao nulu niti ga zatvaramo posljednjom poznatom cijenom. Callback sesija ili prekid usred otvorene pozicije ne popravljaju se kasnijom svježom kotacijom; nova zasebna odluka nakon oporavka može koristiti tada svjež quote. Horizontni povrat prolazi istu provjeru pokrivenosti i nije zamjena za rezultat stvarnog simuliranog izlaza.

## Zamrznuti dokaz prekida i verzije

ModelOps `fx-replay` prima opcionalni `--coverage ABSOLUTE_FROZEN_JSON`. [Stvarni ulaz ovog testa](evidence/n1-coverage-input-2026-11-20.json) sadrži instrument, UTC raspon, izvor s hashom i dva zapisa ranije zabilježenog prekida. Izvorna polja `StartUtc/EndUtc` iz spremljenog SQLite izvještaja interpretirana su kao UTC i eksplicitno označena `Z`; nisu pretvorena iz lokalnog vremena. Dnevni zapis ne sadržava podatke računa.

Primjer strukture:

```json
{
  "instrumentId": "FX-CASH-EUR-USD-IDEALPRO",
  "fromUtc": "2026-11-20T09:00:00Z",
  "toUtc": "2026-11-20T11:00:00Z",
  "source": "Frozen operational report and its SHA256",
  "interruptions": [
    { "startUtc": "2026-11-20T09:13:20.2308456Z", "endUtc": "2026-11-20T09:13:37.2725049Z", "reason": "Recorded interruption" }
  ]
}
```

Odbijaju se pogrešan instrument, nedovoljan raspon, neoznačena UTC vremena, obrnuti/neograničeni intervali, nedostajući izvor i nepoznata JSON polja. Intervali se kopiraju, dedupliciraju i stabilno sortiraju. Replay ne čita promjenjivu operativnu bazu kao implicitni dokaz; odsutnost datoteke ili prazna lista **ne dokazuju neprekinutu vezu**. Korisnik izvora odgovoran je za njegov sadržaj; sama datoteka nije neovisna brokerska potvrda.

Normalizirani dokaz i verzija pravila ulaze u input hash, a vraćeni izvještaj uključuje dokaz. Razlog i vremena trajno su u trade JSON-u i research label dijagnostici. Uvedene su `labels-fx-execution-v2`; stare labele ostaju sačuvane, ali ne prolaze zahtjev za aktualnim ugovorom nove evaluacije/treninga. Zaštite prepoznaju cijelu FX obitelj verzija, pa stari v1 replay ne može postati stock replay nakon promjene verzije. Model approval i runtime risk nisu zaobiđeni.

Ovaj ulaz ograničava **simulaciju izvršenja**. Ne rekonstruira zasebne povijesne događaje resetiranja featurea; potpuni paritet takvih događaja ostaje N4. Također ne uvodi nastavak tradea između batchova ili višednevni dataset iz N2.

## Ponovljeni dnevni test

Korišteni su isti nepromijenjeni Parquet snapshoti i byte-identične konfiguracije scenarija 100 / virtualnih 50.000 USD. Nove baze i izvještaji su u `TestResults/n1-final-20261120`; potvrđeni su hashovi **539 sačuvanih ranijih datoteka**. Stari rezultati nisu prepisani.

| Ulaz simulacije, Zagreb | Razmak bez kotacije | N1 zaključak |
| --- | --- | --- |
| 10:19:42 | 3,1018613 s | `UnobservedQuoteInterval`, izlaz nepoznat |
| 10:41:47 | 4,4147270 s | `UnobservedQuoteInterval`, izlaz nepoznat |
| 11:12:37 | 3,5988385 s | `UnobservedQuoteInterval`, izlaz nepoznat |
| 11:45:57 | 3,6407435 s | `UnobservedQuoteInterval`, izlaz nepoznat |

Nijedan od tih razmaka ne preklapa dostavljeni zapis TWS prekida. To ne isključuje neprijavljen zastoj; nema dovoljno dokaza da ih proglasimo mirnim tržištem ili popravimo ishod. Sva četiri tradea ostaju otvorena/nepotpuna u simulaciji. Time je ispunjen N1 kriterij jasne dijagnoze i poštenog neizvjesnog ishoda, a ne kriterij potpunog backtesta.

Poznati TWS prekid je oko **10:13:20–10:13:37 Zagreb**. Kandidat u **10:13:22** sada se odbija već pri provjeri dostupnosti quotea (`RecordedInterruption`). Ranije je došao do research risk veličine 20.000 EUR pa kasnije završio bez filla zbog općenite pokrivenosti. Zato se njegova tražena količina mijenja na 0, a provizija s 0 na null; to nije izmjena izvršenog tradea ili poboljšanje prinosa.

- 100 USD: i dalje **0 fillova**, zbog već opisanih veličina/risk uvjeta; jedna research labela umjesto risk odbijanja sada bilježi prekid prije risk procjene.
- Virtualnih 50.000 USD: i dalje **25 fillanih tradeova, 21 zatvoreni**, svih 21 neto negativnih; zatvoreni podskup **−133,03275 USD**. Četiri nepotpuna nisu uključena kao nulti prinos. Cijene, vremena, količine i ishodi fillanih tradeova jednaki su starom izvještaju.
- Counterfactual labele većeg scenarija: 131 dovršena / 219 nedovršenih. Među nedovršenima su 170 unsupported short, 32 neopažena razmaka, 3 zabilježena prekida, 7 odbijanja quotea u trenutku odluke, 3 bez filla i 4 završetka uzorka s otvorenom pozicijom. To nisu 131 neovisni portfolio trade.
- Ponovljeni batch 11–11:30 Zagreb daje isti input/output hash; output `78efaa56a49581b3e83ba76edbb070e9c465ead154a32bccedd99d9429983b24`.

[Strojno čitljiv izvještaj i hashovi](evidence/n1-quote-coverage-2026-11-20.json) uključuju sve četiri dijagnoze i promjene zapisa. Zbroj četiri odvojena batcha i dalje nije potpuni portfeljni rezultat. Nije uključen model, novi kolektor niti poslan nalog; operativna baza i konfiguracija nisu mijenjane.

## Provjera i nastavak

**764/764 lokalna testa prolazi** (23 nova slučaja). Pokriveni su nepromijenjene cijene s redovitim callbackovima, nedostatak novih callbackova, uključive granice gap/starosti, tolerirani budući timestamp bez ranog korištenja, kratki zabilježeni prekid, reconnect, zatvorena sesija, kraj dataseta prije/poslije ulaza, bez filla unutar opaženog prozora, validacija/fiksiranje izvora i determinističnost novog replaya uz očuvanje starog. Postojeći CLI test sada učitava coverage datoteku. Prolaze obvezni IBKR DLL, publish i negativne provjere nedostajućeg/promijenjenog DLL-a.

Sljedeća pojedinačna razvojna točka je **N2 — višednevni dataset/replay**. N1 nije promijenio negativan rezultat baselinea niti dokazao da kolektor nema preostalih kašnjenja; mjerenje i optimizacija ostaju N3.
