# Building RpfSealer

## Quick build (ships-today path)

Uses the prebuilt MIT-licensed DLLs under `libs/`. Fastest, and what the
CI / release workflow will consume.

```powershell
# Requires .NET SDK 8 or later. Windows host (net48 target).
cd src\RpfSealer
dotnet build -c Release
# Output: src\RpfSealer\bin\Release\RpfSealer.exe
```

Single-file build via Costura.Fody is automatic — `RpfSealer.exe` is
self-contained (~850 KB), no loose DLLs required at runtime.

## Pure-upstream rebuild (provenance audit path)

The `libs/ragelib.dll` and `libs/ragelib.gta5.dll` shipped in this repo are
a superset of Neodymium146/gta-toolkit: pure-upstream classes plus a set
of encrypt-path additions that live in a downstream MIT fork (see the
Attribution section of [README.md](README.md)).

`.upstream/build/` contains SDK-style wrappers that build **only** the
pure-upstream portion. Reproducing a decrypt-only build is useful for
provenance audits; it is **not** sufficient to run RpfSealer's `seal`
command (which needs the encrypt tables/LUTs upstream does not produce).

```powershell
# 1. Clone gta-toolkit into .upstream/gta-toolkit/
mkdir .upstream
cd .upstream
git clone --depth 1 https://github.com/Neodymium146/gta-toolkit.git
cd ..

# 2. Build decrypt-only RageLib + RageLib.GTA5:
cd .upstream\build\RageLib         ; dotnet build -c Release ; cd ..\..\..
cd .upstream\build\RageLib.GTA5    ; dotnet build -c Release ; cd ..\..\..
# Outputs:
#   .upstream\build\RageLib\bin\Release\RageLib.dll
#   .upstream\build\RageLib.GTA5\bin\Release\RageLib.GTA5.dll
```

A future `/patches` directory (see [CONTRIBUTING.md](CONTRIBUTING.md) —
not yet written) will stage the encrypt-path additions as individual `.cs`
files so the full DLLs can be reproduced from source.

## Verifying the tool

After building:

```powershell
.\src\RpfSealer\bin\Release\RpfSealer.exe help
.\src\RpfSealer\bin\Release\RpfSealer.exe self-test <dir-with-reference-dat-files>
```

`self-test` unwraps the bundled magic blob using a reference AES key and
compares the output to reference `.dat` files byte-for-byte. If all three
checks (NG keys, decrypt tables, PC_LUT) report MATCH, the tool is
behaviorally correct.
