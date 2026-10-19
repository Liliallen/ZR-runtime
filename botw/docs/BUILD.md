# Build

Nothing is installed system-wide. Tools live in `<BOTW>/.tools/` (sibling of this repo), builds in `<BOTW>/build-*`.

## Prerequisites
- The official Ryubing 1.3.3 Flatpak installed for the user (`io.github.ryubing.Ryujinx`) — its binary provides the
  two NuGet packages that only exist on the unreachable `git.ryujinx.app` feed.
- `curl`, `python3`, `flatpak`, `strings`; for the DLSS bridge `g++` and `libvulkan.so`.

## .NET SDK (user-local)
```bash
mkdir -p ../.tools && curl -sSfL https://dot.net/v1/dotnet-install.sh -o ../.tools/dotnet-install.sh
bash ../.tools/dotnet-install.sh --channel 9.0 --install-dir ../.tools/dotnet
```

## Emulator
```bash
botw/scripts/build.sh                  # → ../build-botwpc (Release). First run creates the offline feed:
                                       #   botw/scripts/make-offline-feed.sh extracts Ryujinx.LibHac 0.21.0-alpha.116 and
                                       #   Ryujinx.UpdateClient/Common 1.0.44 from the Flatpak binary (versions checked),
                                       #   puts the original DLLs in lib/ (runtime) and a CoreLib-retargeted copy in ref/
                                       #   (compile only), and restores with botw/nuget.offline.config.
export DOTNET_ROOT=$PWD/../.tools/dotnet
../build-botwpc/Ryujinx -r ../devdata "<game.nsp>"     # GUI, isolated data dir
```
Why the retarget: the Flatpak is a trimmed single-file publish whose assemblies reference
`System.Private.CoreLib` directly, which the C# compiler cannot use; at runtime the original bytes are used
(`build-botwpc/LibHac.dll` is byte-identical to the official one).

## Tests
```bash
dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj -c Release --no-restore            # 30 573 pass, 133 skipped (incl. 28 BotW-PC tests)
dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~BotwPc
dotnet test src/Ryujinx.Tests.Memory/Ryujinx.Tests.Memory.csproj -c Release --no-restore
python3 botw/scripts/frametime_stats.py --selftest; python3 botw/scripts/threadstat.py --selftest
python3 botw/scripts/trace_top.py --selftest
```
(`dotnet restore ... --configfile botw/nuget.offline.config` first.)

## DLSS bridge and test
```bash
botw/native/ngx_bridge/build.sh        # downloads the DLSS SDK + Vulkan headers into ../.tools, builds
cd ../.tools/ngx-bridge && ./test_ngx_bridge "$PWD" "$PWD/data" /tmp/out   # expects PASS
```

## Profiling
`BOTWPC_TRACE=20 botw/scripts/run.sh ...` records a `dotnet-trace` sampling profile (install once:
`dotnet tool install dotnet-trace --tool-path ../.tools/bin`); analyse with `botw/scripts/trace_top.py`.
