# IBKR C# TWS API 10.49.02

This directory contains the official C# API from the local TWS API installation, pinned for reproducible builds. It is a required dependency of TradingBot.Infrastructure. No account configuration, credentials, market data or runtime database is included.

- Assembly/file version: `10.49.02.0`.
- Target framework of the supplied binary: `net8.0`, consumed by our .NET 10 application.
- Runtime dependency: `Google.Protobuf` NuGet package `3.29.5`, explicitly referenced by TradingBot.Infrastructure.
- Original binary: `C:\TWS API\source\CSharpClient\client\bin\Release\net8.0\CSharpAPI.dll`.
- Binary SHA-256: `6CAA826F09F5C9860E1729FDAC95E7EAB4E58C9CE1F60DCDE09F4069C9BB8D3D`.
- `CSharpAPI-source.zip`: 271 unmodified `.cs`/`.csproj` files from the same installation's `source/CSharpClient/client` directory, excluding `bin` and `obj`. Includes generated protobuf C# files and `Properties/AssemblyInfo.cs`.
- Source archive SHA-256: `E59AEF1E51C91AE08E9F0DA6DA7FED173B8BFC6F90190E2AB36235F3FCC911B7`.
- Imported: 2026-10-25. Upstream download entry point: https://interactivebrokers.github.io/.

## License and source

Copyright (C) 2013-2026 Interactive Brokers LLC. The included source headers declare **GNU GPL version 3 or later**. The full GPLv3 text is in `LICENSE.txt`, obtained from the GCC mirror at https://raw.githubusercontent.com/gcc-mirror/gcc/master/COPYING3 (canonical license: https://www.gnu.org/licenses/gpl-3.0.html). This third-party component is not covered by the repository's own MIT license. Preserve its copyright, license and corresponding source when distributing this component. The web application's build/publish output includes these accompanying files under `third_party/ibkr/`.

To build the supplied source independently, extract the archive to an empty directory and run:

```powershell
dotnet build CSharpAPI.csproj -c Release -p:TargetFrameworks=net8.0
```

The prebuilt DLL is used by the bot; this independent rebuild is not required for ordinary restore/build/publish. Build environment differences may change binary bytes; the stored checksum identifies the exact reviewed artifact, not all possible rebuilds.

## Updating

Do not replace the DLL automatically or use an unofficial API NuGet package. Import the next official binary and matching source/license in a versioned directory, update the path, checksum and runtime dependency in `../IBKR.Api.targets` together with the verification script and version assertion in the DI test, then run `scripts/verify-ibkr-adapter.ps1` and the paper connection checks. Commit the entire update. Git clone restores the pinned dependency; no machine-specific API installation is required to build or publish this bot. TWS or IB Gateway is still needed to establish a broker connection.
