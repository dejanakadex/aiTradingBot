# FX-10 — replay, izvršne labele i lokalni model

Implementacija nadograđuje postojeći replay, SQLite research/evaluation/model registar i ML.NET workflow. Ne pokreće TWS niti šalje naloge tijekom offline obrade. SPY ostaje na postojećim stock ugovorima. Automatski FX paper put postoji iza dodatnog opt-ina; nije aktiviran u zajedničkoj konfiguraciji.

## Ulazi i granice dokaza

- Potreban je provjeren Parquet s kanonskim parovima `IBKR.TickByTick.Forex.BidAsk.v2`. MIDPOINT minutne svijeće, stock trade tickovi i legacy nevezani bid/ask nisu zamjena. Dvije strane imaju isti callback ID, event i receive vrijeme. Konfliktni duplikati, nedostajuća strana, crossed quote ili regresija event vremena prekidaju obradu.
- `ForexResearch:Profiles` veže instrument/strategiju, 1/5/15s agregat, sekundni holding, entry timeout, latenciju, dopušteni gap, pip stop/target, bazne jedinice, početni USD kapital i eksplicitnu hipotetsku fill količinu po quoteu. Veličina kotacije ne koristi se kao dokaz izvršive likvidnosti.
- `HistoricalRules` je niz `ForexBrokerRulesSnapshot`: `Contract` (potvrđeni CASH ugovor i metadata), `ObservedUtc` te `Rules` (contract ID, `VerifiedAtUtc`, izvor, minimum/maksimum/korak baznih jedinica, market rule ID i price bands). Zadržati izvorne UTC datume i dokaz; ne osvježavati stare zapise datumom replaya. Za svaki signal koriste se samo tada dostupni, još važeći podaci. Bez njih nema potpune labele. Pravila su povijesna pretpostavka simulacije, a ne današnje dopuštenje naloga.
- [Primjer konfiguracije](../TradingBot.Web/forex-research.example.json) nije automatski učitan. Nule i prazan niz pravila namjerno traže pregled prije uporabe. Spojiti ga u zasebnu kompletnu lokalnu konfiguraciju s odgovarajućim `TradingSettings`, `RiskSettings`, `ForexRisk`, `ForexPatterns`, `ForexSignals`, `ShortAggregates`, `CanonicalFeatures`, `ResearchEvaluation` i `NumericalModel` postavkama. Provjeriti stvarni naziv feature sekcije u aktivnoj konfiguraciji (`CanonicalFeatures`).
- Jedan poziv obrađuje najviše sedam dana i 200.000 raw događaja. To je ograničen lokalni istraživački batch, bez pause/resume, prijenosa otvorene pozicije među batchovima ili dokaza produkcijske propusnosti. Za evaluaciju unutar tog raspona potrebni su izričito postavljeni kraći vremenski prozori; zadani višemjesečni prozori nisu primjenjivi. Manji minimumi u testovima nisu preporuka za odobrenje stvarnog modela.
- Učitavanje pravila strogo prijavljuje neispravnu konfiguraciju umjesto tihog preskakanja zapisa. Opcionalna IBKR metadata smije biti null; izvršni risk i dalje odbija nedostajuće obvezne podatke. ISO datumi s offsetom normaliziraju se u isti UTC trenutak. Eksplicitni aggregate/evaluation/model nizovi zamjenjuju defaultne nizove u Webu i ModelOpsu, a FX geometry nizovi zadržavaju isti fingerprint nakon bindanja.

## Simulacija i značenje labele

Isti `LatestMarketDataService`, FX agregator, feature engine, pattern detector i `ForexSignalTriggerService` obrađuju input u virtualnom receive vremenu. Bar se zatvara na granici plus dopuštena lateness; budući quote ne ulazi u raniju odluku. Replay research zapisi imaju zaseban `ReplayRunId` i ne mijenjaju live cooldown.

Simulacija ponovno koristi FX-8 risk i sizing. Podržava long, USD-kotirane parove i USD scenarij kapitala. Kupnja koristi ask plus slippage unutar početnog limita, prodaja bid minus slippage. Target zahtijeva prolaz iznad limita, zatim dostupnu cijenu nakon latencije; dodir nije dokaz fill-a. Djelomični fillovi imaju izričit kapacitet. Timer počinje prvim fillom i otkazuje ostatak ulaza; izvršenje izlaza zahtijeva sljedeći pokriveni quote nakon latencije. FixedBracket ima stop/target; AtrTrailing koristi samo tada dostupan ATR i odgođenu izmjenu stopa. Nepokriveni gap, reconnect, zastarjeli quote, nepoznata pravila, otvoreni ostatak i short nemaju valjanu trening labelu.

`GrossReturnBps` je rezultat stvarno simuliranih entry/exit cijena: **spread i slippage već su u njemu**. `EstimatedCostBps` sadrži dodatne provizije i safety buffer; `NetReturnBps = GrossReturnBps - EstimatedCostBps`. Provizija se konzervativno naplaćuje po simuliranom fillu uz minimum, što nije tvrdnja o IBKR grupiranju stvarnih provizija. MFE/MAE su nenegativne povoljne/nepovoljne ekskurzije do izlaza. Prinos na kraju holding horizonta je zasebna, opcionalna dijagnostika i ne zamjenjuje izvršnu labelu. Cost-stress u postojećoj evaluaciji skalira dodatni trošak; za drugi spread/slippage/latency/fill scenarij ponoviti replay s drugim ugovorom.

Counterfactual labele računaju se za detektirane, uključujući blokirane kandidate, s istim početnim kapitalom i unaprijed zadanim stop/target pravilom. Target udaljenost tu je scenarij, ne prognoza. Zaseban baseline backtest obrađuje samo prihvaćene signale, dopušta jednu otvorenu poziciju tog para/strategije, prati kapital i dnevni gubitak i preskače preklapanja. Zbroj labela nije rezultat portfelja. Ne simulira zajednički više parova, margin call, broker disconnect protokol, cancel-race ili tržišni red čekanja; puni produkcijski paritet točke 25 ostaje otvoren.

Model uči samo iz potpuno pokrivenih zatvorenih simulacija. Ne predviđa vjerojatnost fill-a; no-fill, otvoreni i nepokriveni scenariji ostaju izvan treninga i moraju se zasebno pregledati u replay rezultatu. To ograničava tumačenje modelskih metrika.

## Verzije, evaluacija i model

`labels-fx-execution-v1+config-SHA256` obuhvaća execution profil, povijesna pravila, FX trošak, risk limite, identitet instrumenta i feature/pattern/signal verzije. Operativni `TradingEnabled` nije dio tog hasha. Spremaju se input/output hashevi replaya te ulaz, risk procjena i rezultat svake simulacije u label proofu. Neuspješan replay označen je `Faulted`; njegove djelomične rezultate evaluacija odbija.

Postojeća vremenska evaluacija koristi purging preko konzervativnog kraja label prozora (entry timeout + holding + pokriće/latencija). Treniranje traži dovršen replay s nepromijenjenim output hashom te točne feature/pattern/label verzije, par, strategiju i horizont. Trenira se `numerical-model-fx-v1`: LightGBM net-win klasifikator i regresor izvršnog gross prinosa. Ulazi su pattern confidence/tip/smjer/timeframe, ATR/price i UTC sat; stock normalized liquidity je izostavljen, a volumen nije izmišljen.

Prag klasifikatora bira se na walk-forward predikcijama. Završni holdout ne bira prag i ne ulazi u fit. Za odobrenje treba pozitivan neto expectancy i usporedba s confidence baselineom, plus manji forecast MAE od konstante iz prethodnog trening razdoblja na walk-forward i holdoutu. Forecast MAE vidljiv je u postojećim metrics JSON objektima. Klasifikatorske metrike prikazuju odabir po vjerojatnosti; dodatni runtime forecast/cost/risk gate može odbiti više signala. To nisu dokazi profitabilnosti.

Postojeći `Draft → approve/reject`, pregled output SHA, audit, supersede, rollback i cache predikcija vrijede zasebno po paru/strategiji. Sintetski test odobrava samo testnu SQLite bazu. Nije treniran niti odobren produkcijski EUR/USD model.

## Operativni redoslijed

Koristiti kopiju operativne baze za istraživanje i zasebnu lokalnu JSON konfiguraciju. ModelOps traži postojeću apsolutnu putanju baze; primijeniti migracije prije obrade. Niti jedna naredba ne pokreće botov Web host, broker ili trading worker.

`ModelOps migrate` priprema EF research/model shemu. Puna operativna shema ima i postojeće startup compatibility dopune iz `MigrationHostedService` (npr. legacy `OrderRecords.ClientOrderKey`); prazna baza migrirana samo kroz CLI nije potvrda readinessa za trgovanje. FX-11 treba provjeriti stvarni startup/onboarding tok.

```powershell
dotnet run --project TradingBot.ModelOps -c Release -- fx-contract --db C:\Research\fx.db --config C:\Research\fx.json --instrument-id FX-CASH-EUR-USD-IDEALPRO --strategy-id eurusd-patterns
dotnet run --project TradingBot.ModelOps -c Release -- fx-replay --db C:\Research\fx.db --config C:\Research\fx.json --dataset C:\Research\parquet --instrument-id FX-CASH-EUR-USD-IDEALPRO --strategy-id eurusd-patterns --from 2026-10-19T00:00:00Z --to 2026-10-24T00:00:00Z
```

`fx-contract` prikazuje očekivane verzije, bez njihovog odobravanja. Pregledati FX signal profil i eksplicitno postaviti njegove approval verzije za željeno istraživanje. Nakon replaya koristiti vraćeni `replayRunId` u postojećem `evaluate --replay-run-id ... --instrument-id ... --strategy-id ... --horizon-seconds ... --from ... --to ...`, zatim `train --evaluation-id ...`, `show`, pregled metrika i tek onda `approve` uz review hash. Sve naredbe traže iste `--db` i `--config` argumente. Promjena execution ugovora traži novi replay/evaluaciju/model. Stock `backtest` parametri ne mogu se primijeniti na FX run.

Automatski put `FX signal → lokalni odobren model → ForexPaperPreparationService → zajednički kanal → završni FX gate` dodatno traži `ForexResearch:AutomaticPaperSignalsEnabled=true`, postojeći `ForexPaperExecutionEnabled`, uključen instrument, PaperReady, verified-paper reconciliation i sve FX-9 limite. Default je false. Sekundni horizont i exit pravila moraju odgovarati modelu. Model daje eksplicitnu prognozu, bez pretvaranja confidencea u pomak cijene; prognoza izvršnog prinosa prenosi se konzervativno bez dodavanja spread/slippagea nazad. FX-9 ponovno provjerava kompletan trošak i stvarnu količinu. Identitet modela i label verzija prate plan/strategy context. Odbijanje reda za izvršavanje oslobađa rezervaciju. Prikupljanje i shadow rade i kada su svi trading gateovi zatvoreni.

## Dokazi i preostalo

Lokalni testovi koriste sintetske quoteove, Parquet/SQLite, stvarni LightGBM te deterministički fake broker. Pokrivaju ponovljivost replay hasha, izvršne labele, djelomične fillove, sve podržane izlaze, rupe/pogrešne parove, buduća pravila/ATR, vremenski ML workflow, promjenu holdouta bez promjene fit-a/praga, approval/rollback, zasebne FX ugovore, model → reservation → fake order i odbijanje reda. Puni solution i obvezni vendored IBKR adapter provjeravaju se prije commita.

FX-11 ostaje onboarding i operativna shadow provjera; FX-12 ostaje zasebno odobren stvarni paper end-to-end test. Nema novog stvarnog IBKR filla, provizije, margine ili dokaza profitabilnosti. Točke 25, 30 i 31 time nisu zatvorene.
