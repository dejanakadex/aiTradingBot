# N4 — signal, izvršenje i automatski post-trade zapis

23.11.2026. N4 nadograđuje postojeće servise, uz sačuvane stock/FX risk, arbitration i order/position lifecycle. **Implementacija i dokaz na fake brokeru odvojeni su od stvarnog IBKR paper testa.** Nema promjene approvala, uključivanja naloga, short izvršavanja ili crypto podrške. Puni zajednički portfolio replay ostaje N10; FX-12 i 25/30/31 nisu zatvoreni ovom točkom.

## Automatska rekonstrukcija zatvorenog tradea

`CompletedTradeAnalysisHostedService` svakih 5 s obrađuje najviše 100 zatvorenih lifecycle zapisa i nastavlja cursorom kroz cijeli skup. Po završetku vraća cursor na početak; zato se ponovno provjeravaju kasne provizije i naknadno pristigli execution dokazi, bez oslanjanja na wall-clock `UpdatedUtc` filter. Kod malog skupa puni obilazak može uključiti dodatni prazni ciklus. Zatvaranje ostaje trajni izvor posla i nakon restarta; analiza nije dio broker callbacka ni preduvjet slanja zaštitnog naloga.

Za zapis su potrebni zatvoren lifecycle i trade, nula preostale ulazne količine te potpuni BUY/SELL executioni čiji zbrojevi odgovaraju fillanoj i izašloj količini. Nedostajući execution, višak prodanih jedinica, nepodržana quote valuta ili nepotpun lifecycle ne proizvode lažan konačni rezultat: zapis se odgađa do valjanog dokaza. Podržan je postojeći long USD-quoted opseg SPY/ostalih stockova i EUR/USD; ne uvodimo generičnu konverziju valuta ili short PnL.

Rekonstrukcija povezuje `TradeId`, `ExitManagementId`, instrument, strategiju i `SignalId` s entry/exit nalozima, arbitration vezama i pojedinačnim executionima. Koristi stvarni zbroj kupljenih/prodanih iznosa, ponderirane entry/exit cijene i originalnu ukupnu količinu, a ne `Trade.Size`, koji nakon zatvaranja iznosi nula. Stop/target/timer i miješani izlazi ostaju prepoznatljivi preko lifecycle razloga, uloga naloga i spremljene exit konfiguracije. MFE/MAE se ne izmišljaju bez tržišne putanje.

Jedinstveni nullable `ExitManagementId` i zasebni filtrirani unique `SourceTradeId` osiguravaju najviše jedan automatski rezultat po lifecycleu/tradeu. Verzionirani input hash omogućuje idempotentno ponavljanje. SQLite transakcija obuhvaća čitanje izvora i zapis projekcije, pa konkurentni worker ili kasna provizija ne mogu prepisati noviji rezultat starim snapshotom. Kod neuspjelog commita izvor ostaje za retry. Neispravan JSON/poredak execution vremena prijavljuje se i ne blokira obradu ostalih zatvorenih tradeova.

Provizije se uzimaju iz pojedinačnih execution zapisa. Kasni ispravak mijenja isti post-trade redak. Samo eksplicitna USD provizija svakog executiona daje `FeesComplete=true`; nedostajuća ili druga valuta daje `ProvisionalFees`, poznati dio troškova i privremeni neto iznos. Takav rezultat ne ulazi u konačnu pattern statistiku. Stari ručni zapisi ostaju sačuvani, ali se ne zbrajaju ponovno ako za isti trade postoji automatski rezultat. Prikaz tradeova sada koristi execution-weighted projekciju i razlikuje privremene troškove/čekanje analize od konačnog ishoda; ovo nije planirani redizajn UI-ja.

## Vremenski i odluka-kontekst

FX signal metadata sada trajno nosi cijeli feature snapshot. Model proposal dodaje točan `NumericalModelPredictionRequest` i izvršnu bid/ask kotaciju s vremenima. Vrijeme feature odluke ostaje kandidatov `decisionAtUtc`; kasnija provjera risk/execution kotacije ima vlastito vrijeme. Lifecycle pri odobrenom entryju sprema i snapshot, pipeline context i vrijeme odobrenja uz postojeći pattern, model/AI, strategy, risk i exit konfiguraciju. Post-trade zapis zadržava taj ulaz, research odluke i FX risk proof; null `SignalId` ne povezuje nepovezane research zapise.

Stari stock history/snapshot put sada ima eksplicitni `asOf`: SQLite filtrira timestamp, vrijeme primitka, finalnost i kvalitetu **prije** ograničenja broja svijeća. Sva tri timeframea koriste jednu granicu. Novija trenutna kotacija ne nadomješta nedostupnu povijesnu. Candle detector koristi vrijeme primitka kao dostupnost događaja, prenosi ga u kandidat i odluka koristi isti `asOf`. Fallback bez tog metadata podatka ostaje konzervativno vrijeme detekcije. To ne stvara arhivu revizija svijeća niti proširuje stock execution opseg.

Ponovni pregled starih nalaza:

- **R09:** validator već pribraja trajanje 1m/5m/15m bara pri freshness provjeri. Stara tvrdnja da toga nema više ne vrijedi. Stvarna brokerska timestamp konvencija i završetak sesije ostaju dio IBKR provjere.
- **R13:** nedostajući `asOf` u stock history/snapshot putu popravljen je i testiran s budućim/zakasnjelo primljenim podacima. Pipeline pri duplikatu ne objavljuje drukčiji candle nakon odbijenog spremanja; RSI ravnog niza u aktualnom kodu vraća 50. Postojeći FX feature servis i replay dijele input/feature put. Puna vremenska parity provjera timer/model/portfolio/order toka na stvarnom brokeru ostaje zasebna.
- **R14:** pattern quality ostaje heuristički score, ne kalibrirana vjerojatnost. Test razlikuje kandidatov score 0,7 od modelove vjerojatnosti 0,9, bez pretvaranja jednog u drugo. Kalibracija/prednost strategije zahtijeva N6/N7 podatke; ovom točkom nije proglašena riješenom.

## Provjera i granice dokaza

| Scenarij | Provjera |
| --- | --- |
| Povezani FX tok | Sintetičke kotacije, realni feature/pattern/signal servisi, numerički model stub, stvarni risk/arbitration/preparation/executor, fake broker, fillovi, target i post-trade uz isti SignalId |
| Izlazi | FixedBracket stop/target/timer; AtrTrailing stop/timer; stop prilagodba i decimalne base-unit djelomične pozicije |
| Djelomični izlazi | Ponderirana exit cijena, ukupna ispunjena količina i neto rezultat iz svih executiona |
| Restart/reconciliation | Očuvan timer i kapacitet, zatvaranje nakon restarta, dupli callbackovi bez drugog trade rezultata; nedostajući execution odgađa rezultat do dopune |
| Provizije i greške | Kasna USD provizija ažurira isti redak; EUR provizija ostaje privremena; rollback nakon pogreške spremanja i ponovni pokušaj |
| Konkurentnost | Dva workera nad zasebnim vezama stvarne SQLite datoteke daju jedan rezultat; unique pravila vrijede i za isti TradeId s različitim lifecycle ID-em |
| Vremenska granica | Kasnije svijeće ne potiskuju povijesni prozor prije Take; late receipt i buduća kotacija ne ulaze u raniji snapshot |
| Stock regresija | Postojeći E2E i novi post-trade spoj zadržavaju stock ponašanje |
| Migracija | Prethodna shema s duplim ručnim SourceTradeId redcima očuvana; nova unique pravila i model snapshot provjereni |

N4 offline parity ponovljen je na nepromijenjenom kratkom stvarnom snapshotu: 4.362 quote događaja, 22 zagrijana feature framea i 470 pattern evaluacija, bez razlike između per-event i checkpoint putanje uz novi feature metadata. Profil ostaje neodobren; prihvaćeni signal provjerava se odvojeno kontroliranim testom. To nije stvarni paper fill niti puna model evaluacija.

**Stvarni IBKR paper kriteriji ostaju:** potvrđeni contract/price/size uvjeti, dopuštena i svježa kotacija, stvarni entry → fill → zaštita → exit callback redoslijed, djelomični fillovi, valute/provizije i reconciliation nakon prekida. Per-event trajnost paper/live putanje ostaje uključena. Izmjereni storage repovi do oko 2 s nisu riješeni ovim audit workerom i moraju se izmjeriti prije prihvata paper execution latencije. Nisu popušteni risk, model ili readiness gateovi.

Migracija `20261123150000_AddAutomaticPostTradeAnalysis` samo dodaje nullable/default polja i indekse; ne briše postojeće tradeove, naloge, kotacije ili ručne analize. Operativni deploy radi se uz backup baze, uredan restart i iste read-only/trading-off argumente. Dokumentirani fake-broker rezultati ne opravdavaju uključivanje naloga na operativnom računu.

## Završni rezultat i operativni deploy

**810/810 Release testova prolazi**, bez neizvršenih testova; obvezni IBKR publish i negativne missing/changed-DLL provjere prolaze. Lokalni TRX, logovi, parity i SHA-256 dokazi navedeni su u [strukturiranom zapisu](evidence/n4-trade-lifecycle-2026-11-23.json).

23.11. u 14:45 Zagreb kolektor je uredno ponovno pokrenut nakon konzistentnog backupa baze. Nova migracija i oba jedinstvena indeksa potvrđeni su; SQLite quick_check je `ok`. Deployana biblioteka odgovara testiranom buildu. ReadOnly API ostaje uključen, trading/FX execution/automatski paper signali isključeni. Baza ima nula tradeova, naloga, executiona, analiza i modela. U 14:48 kotacije stižu, bez reconnecta/uzastopnih neuspjeha; quality ima nula failed/pending streamova. Najdulji pozadinski flush 1.600 ms i pending 2.507 ms potvrđuju da storage rep nije uklonjen. Startup je zabilježio IBKR 366 za završeni/nepostojeći historical query; aktivni tick tok nije blokiran. Planirani prekid deploya nije prikriven niti kratka provjera zamjenjuje N3 prihvatnu sesiju.

**N4 kriterij implementacije i simulacije je završen.** Slijedi N5, prikupljanje i zamrzavanje reprezentativnih podataka, nakon zasebnog odobrenja sljedeće točke. Stvarni paper entry/fill/exit nije proveden.
