# FX-11 — onboarding, prikaz i shadow

25.10.2026.: programski dio implementiran i provjeren lokalno; **stvarno mjerenje FX model shadow toka nije završeno**. Nema odobrenog produkcijskog EUR/USD modela ni potvrđenog IBKR filla. Točke 25/30/31 i FX-12 ovime se ne zatvaraju.

## Prikupljanje i prijelazi

Postojeći startup redoslijed migracija → registry sync → trajni backfill planner/worker i tick/bar kolektori ostaje zajednički za STK i CASH. `Enabled=true`, `TradingEnabled=false` i `AnalysisOnly` dopuštaju podatke i istraživanje. Restart ne duplicira registry ni backfill poslove. Test svježe operativne baze prolazi kroz stvarni `MigrationHostedService`, uključujući postojeće popravke legacy stupaca; sam ModelOps `MigrateAsync` i dalje nije zamjena za operativni startup.

`InstrumentRolloutService` sada odabire zasebna FX pravila; stock put ostaje isti:

1. **Backfilling → Collecting → WarmingUp:** svi konfigurirani povijesni timeframeovi završeni; bez gapova i s najmanje `MinimumFinalBarsPerTimeframe` zdravih, finalnih `IBKR.Midpoint` barova bez trade volumena. Minutna povijest ne popravlja nedostajuće bid/ask tickove.
2. **ResearchReady:** po svakoj konfiguriranoj strategiji eksplicitni FX-10 ugovor i najmanje `MinimumCompleteLabelsPerStrategy` različitih kandidata s izvršnim labelama istog horizonta. Priznaju se samo završeni FX replayi s provjerenim output hashom; ponovno pokretanje istih kandidata ne povećava uzorak. Deferred live labele i dioničke labele nisu dovoljne.
3. **ShadowReady:** cijela research provjera, točne verzije evaluacije/modela/featurea/patterna/labele, jedan ručno odobren kompatibilan model po strategiji, eksplicitno odobren signal profil, svježi ugrijani kratki featurei, zdravi streamovi i svježi paired bid/ask. Gapovi i incidenti blokiraju. FX-10 trenutačno podržava USD-kotirani long izvršni ugovor; ostali FX parovi mogu prikupljati podatke, ali ne preskaču tu granicu.
4. **PaperReady:** tek uz odgovarajući **AnalysisOnly model shadow uzorak nakon ulaska u ShadowReady**, zasebno za svaku strategiju. Zadano najmanje 100 odluka, 5 prošlih model prijedloga, raspon opažanja 30 minuta, p95 model obrade ≤1000 ms, starost odluke i quotea ≤1500 ms te prosječni predloženi net edge ≥3 bps. Primjenjuju se stroži minimumi iz `InstrumentRollout` i `ShadowPerformance`; FX provjera ih koristi i kada je stock timing recorder isključen. Opažanja drugih verzija/modela i PaperTrading načina ne zamjenjuju AnalysisOnly dokaz.

PaperReady je status dokaza, ne dopuštenje naloga. Prije svakog naloga FX-9 još provjerava potvrđeni paper račun, stvarni kapital, brokerova pravila, troškove, rezervacije i sve opt-in zastavice. FX-11 ne uključuje nijednu zastavicu. Nestanak readinessa ili nezdrava reconciliacija suspendira PaperReady instrument, uz nastavak prikupljanja. Oporavak suspendiranog instrumenta ostaje eksplicitan operativni postupak; ne vraća se automatski u trgovanje. Zatvorena sesija ne daje trenutačnu shadow spremnost. `PaperCriteriaPassed` i live approval za FX ne predstavljaju provedeni paper test; ovaj put ih ne odobrava.

## Shadow mjerenje i UI

`ForexSignalHostedService` bilježi prijedloge kroz `ForexShadowService` neovisno o dopuštenju trgovanja. Servis nema broker/order kanal ni risk rezervaciju. U postojećoj tablici odluka koristi zasebni policy `FX.ModelShadow.v1`: ugovor, model, AnalysisOnly/PaperTrading način, ishod, model vrijeme obrade, starost odluke od `decisionAtUtc`, starost quotea i procijenjeni edge. Prošao model nije isto što i prošao portfolio risk. Trošak prijedloga računa se za istraživački maksimum količine; konačni sizing i trošak ponovno provjerava FX-9. Odvojeni stock assessor ne računa FX model prijedloge kao stock execution odluke.

Vrijeme model obrade uključuje dohvat/verifikaciju ugovora i lokalnu inferenciju, a ne puni tick→fill put ili završni upis audita. P95 ciklusa read-only probe uključuje i prazne cikluse. Ova dva mjerenja nisu zamjenjiva niti dokazuju brokersku latenciju.

Nova stranica `/instruments` prikazuje sve registry instrumente, prikupljanje, zahtjev trgovanja, onboarding status, posljednju procjenu i razloge blokade, strategije, bid/ask i UTC vrijeme quotea. Osvježavanje samo čita postojeće stanje. EUR/USD cijena ima jedinicu USD/EUR, količina EUR; dionice imaju shares i svoju kotiranu valutu. Prikaz cijene čuva do osam decimala. FX volumen/VWAP označeni su nedostupnima. Dashboard i Trades također prikazuju jedinice; stari trade bez jednoznačnog registry identiteta ima `unknown`, bez nagađanja valute. Aggregate legacy P/L ostaje namijenjen postojećem USD izvršnom opsegu, bez nove konverzije više valuta.

## Read-only proba

Samostalni [forex-shadow.example.json](../TradingBot.Web/forex-shadow.example.json) uključuje EUR/USD prikupljanje i pattern audit u AnalysisOnly; čuva disabled SPY i EUR/GBP primjere. Aktivni `appsettings.json` nije promijenjen. Odobrenja signal profila namjerno nisu unaprijed ispunjena. Primjer ne kopirati kao djelomični override konfiguracijskog niza instrumenata.

Iz korijena repozitorija, uz paper TWS na 7497:

```powershell
dotnet run --project TradingBot.ShadowProbe -c Release --no-build -- --forex-shadow-config C:\PrivateGit\aiTradingBot\TradingBot.Web\forex-shadow.example.json --port 7497 --client-id 98 --seconds 1800 --output C:\PrivateGit\aiTradingBot\TestResults\fx11-shadow.json
```

Proba koristi order-blocked službeni adapter i novu izoliranu SQLite/Parquet mapu. Ne pokreće order lifecycle, ne uvozi/odobrava modele i ne mijenja operativnu bazu. Mjeri tok kolektor→FX feature/pattern audit uz zadane kratke agregate; drukčiji aggregate profil treba provjeriti u punom AnalysisOnly hostu. JSON razlikuje history/storage, tickove, pattern odluke, cycle p95 i model readiness. CLI vraća 3 kada povijest prođe, ali nema live quote→pattern uzorka; 1 je pogreška probe, 2 konfiguracije, 0 potvrđuje samo podatkovni opseg probe. `ModelShadowValidated=false` ostaje jer nova izolirana baza nema odobren model.

Za puno model shadow mjerenje prvo prikupiti kvalitetan bid/ask dataset tijekom otvorene sesije, provesti [FX-10 replay/evaluate/train/review](FX10_RESEARCH.md), eksplicitno odobriti točne signal verzije i model te pokrenuti puni host u AnalysisOnly uz postojeću operativnu bazu. Rollout monitor tada zapisuje i prikazuje stvarne rezultate. Ne trenirati/odobravati model na sintetskim testnim podacima radi preskakanja gateova.

## Dokazi i što ostaje

- **680/680 lokalnih testova**, od čega 16 novih FX-11 slučajeva: stvarni operativni startup/restart, mješoviti registry/backfill uz ugašeno trgovanje, quote→feature→signal→model audit sa stub modelom bez naloga/rezervacija, stvarni FX replay kao izvor readinessa, izmijenjen replay/model/evaluacija, pogrešne labele, stari/drugi model/PaperTrading shadow uzorak, quote age, spora obrada, gap i suspenzija. Blazor stranica renderirana je testom i provjerene su stock/FX jedinice. Postojeći fake-broker lifecycle i stvarni lokalni LightGBM testovi ostaju u punoj regresiji.
- Release build: 0 upozorenja/grešaka; restore i IBKR publish/negative-dependency provjera prolaze. Decimalni prag evaluacijskog hasha normaliziran je s `G29` radi stabilnog SQLite roundtripa; neusklađena stara FX evaluacija mora se ponovno generirati, ne ručno označiti valjanom.
- **Stvarna read-only proba 25.10.2026. oko 18:22 UTC:** TWS server 225/API 10.49.2.0, 60 povijesnih MIDPOINT barova u SQLite/Parquet s valjanim manifestom; 0 bid/ask tickova, 0 pattern odluka i 0 naloga. Prema konfiguriranom kalendaru FX sesija bila je zatvorena. [Anonimizirani JSON](evidence/fx11-shadow-preflight-2026-10-25.json). Time je potvrđena veza/povijest, **nije potvrđen stvarni shadow uzorak ni p95 pod opterećenjem**. Preliminarni sistemski pregled porta nije otkrio TWS; izravni API poziv potvrđuje da je veza dostupna.
- Za zatvaranje stvarnog dijela FX-11 treba otvorena sesija, živi quote uzorak i kvalitetan odobren model. FX-12 ostaje zaseban ograničeni stvarni paper entry/fill/SL/TP/timer/reconcile test. Nisu slani nalozi i live trading nije uključen.
