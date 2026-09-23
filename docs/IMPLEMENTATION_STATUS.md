# Implementation status

Ovaj dokument je checkpoint za nastavak rada na branchu `trading-bot-v2`. Svaka točka mora završiti testovima, zelenim CI-jem i jasnim izvještajem prije prelaska na sljedeću.

## Trenutno stanje

| Točka | Status | Sažetak |
| --- | --- | --- |
| 0 — Razvojni baseline | Završeno | .NET 10 LTS, clean repository, GitHub Actions, Dependabot i početnih 195/195 testova. Realni IBKR adapter još treba zaseban build sa službenim DLL-om. |
| 1 — Multi-instrument temelj | Završeno | Nova `Instruments` konfiguracija, legacy `Symbols` fallback, per-instrument timeframeovi/metadata, stabilni signal/correlation identiteti i verzije pipeline ugovora. |
| 2 — Registry i onboarding | Sljedeće | Persistirani statusi instrumenta i automatski, idempotentan onboarding bez automatskog live dopuštenja. |
| 3–18 | Na čekanju | Redoslijed i kriteriji nalaze se u `V2_PLAN.md`. |

## Točka 1 — izvedeno

- Dodani su `InstrumentSettings` i normalizirani `ConfiguredInstrument`.
- Instrument definira stabilni ID, symbol, exchange, currency, security type, enabled/trading zastavice, dopuštene smjerove, strategije, timeframeove i opcionalne limite.
- Startup odbija prazne, duplicirane, nepodržane ili kontradiktorne definicije.
- Pretplate koriste sve omogućene instrumente i njihove timeframeove; onemogućeni instrument se ne pretplaćuje.
- Stari `Symbols` ostaje kompatibilni fallback dok migracija pozivatelja ne završi.
- `PipelineContext` nosi `CorrelationId`, deterministički `SignalId`, `InstrumentId`, `StrategyId` i verzije market-data/feature/pattern/strategy ugovora.
- Kontekst se prenosi s pattern kandidata u strategy i risk odluku te ulazi u postojeće JSON audit zapise bez migracije baze.
- Zadani `appsettings.json` ostaje fail-closed: SPY prikupljanje je omogućeno, a instrument-level trading dopuštenje je `false`.

## Odluke i ograničenja točke 1

- Trenutni IBKR boundary još prima `symbol`, ne cijeli broker contract. Zato validator privremeno ne dopušta dva konfigurirana instrumenta s istim simbolom na različitim burzama.
- `TradingEnabled` je konfiguracijski temelj; njegovo povezivanje s persistiranim readiness statusom pripada točki 2.
- Trenutna implementirana strategija nosi ID `deterministic-patterns`. Izvršavanje više stvarnih strategija po instrumentu dolazi kroz točke 9 i 14.
- Signal ID je deterministički iz instrumenta, strategije, patterna, timeframea i vremena signala; correlation ID je jedinstven za konkretan pipeline prolaz.
- Ova točka ne popravlja nalaze R01–R16 i ne omogućuje live trading.

## Sljedeći checkpoint — točka 2

Prije koda definirati persistence ugovor i dopuštene tranzicije onboarding statusa. Implementacija treba biti idempotentna, nastavljiva nakon restarta i potpuno odvojiti collection readiness od `TradingEnabled` dozvole.
