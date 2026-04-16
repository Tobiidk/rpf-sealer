# RpfSealer

A small, single-file Windows command-line tool that NG-encrypts GTA V RPF
archives so the game will load them, and derives the key material needed to
do so from a running game executable.

Works with:

- GTA V Legacy (`GTA5.exe`)
- GTA V Enhanced (`GTA5_Enhanced.exe`)
- FiveM / FiveM_b2944 / FiveReborn (detected via `CitizenFX.ini`)

No `.NET` runtime install required beyond `.NET Framework 4.8` (preinstalled
on Windows 10/11).

## Why this exists

GTA V's RPF7 archives use a content encryption scheme called "NG" that's keyed
by filename. Tools that produce or edit RPFs (for mod development, asset
swaps, server builds, etc.) typically write unencrypted RPFs first, then need
to toggle the NG encryption flag and re-seal the header before the game will
accept them. That's the `seal` operation.

The original community tool [*Affix* (aka ArchiveFix)][affix], released by
GTAForums user **crypter** on October 17, 2016, did this on Legacy GTA V by
scanning `GTA5.exe` in memory and on disk for the 272 NG decrypt tables by
SHA-1. On GTA V Enhanced the exe layout changed enough that the hash-window
scan hangs indefinitely looking for matches it can never find.

[affix]: https://gtaforums.com/topic/871168-affix-fix-your-rpf-archives-and-work-without-openiv/

RpfSealer replaces that pipeline with CodeWalker's [`UseMagicData`][magic]
approach: a precomputed encrypted blob of all key material is bundled in the
executable; only the AES key needs to be extracted from the live game exe
(which still works on all current builds). The tool is rebuild- and
distribution-verified byte-identical to the 2018 reference `.dat` set.

[magic]: https://github.com/dexyfex/CodeWalker/blob/master/CodeWalker.Core/GameFiles/Utils/GTAKeys.cs

## Usage

```
RpfSealer seal <file.rpf>              Encrypt an RPF with platform NG keys.
RpfSealer keys [--process <name>]      Derive keys from a running GTA V.
RpfSealer keys --pid <id>              Target a specific PID.
RpfSealer keys --legacy                Old pipeline (hangs on Enhanced).
RpfSealer processes                    Show candidate GTA V processes.
RpfSealer self-test [dir]              Verify magic unwrap vs reference set.
RpfSealer <file.rpf>                   Drag-and-drop an RPF to seal it.
```

Typical first-time flow:

```powershell
# Start GTA V (any build), then:
.\RpfSealer.exe keys
# …AES key found… derives 6 .dat files next to RpfSealer.exe (~1 minute)

# Now seal your unencrypted RPF:
.\RpfSealer.exe seal .\mymod.rpf
# OK: mymod.rpf is now NG-encrypted.
```

The six `.dat` files produced by `keys` are cached next to the executable;
subsequent `seal` invocations read from the cache and take milliseconds. If
you want to refresh the keys, delete the `.dat` files and rerun `keys`.

### Drag-and-drop

Drop an unencrypted `.rpf` on `RpfSealer.exe`. Equivalent to `seal <that
file>`. The console pauses before exit so you can read the output.

## Exit codes

| Code | Meaning                                                      |
|-----:|--------------------------------------------------------------|
|    0 | Success                                                      |
|    2 | Usage error (missing argument, unknown command)              |
|    3 | Key files missing, or load failed                            |
|    4 | File not found                                               |
|    5 | RPF is already encrypted                                     |
|    6 | Seal operation failed (see error text)                       |
|   10 | No target process selected                                   |
|   11 | Invalid selection during interactive process pick            |
|   12 | Could not resolve game executable path                       |
|   13 | NG keys not found in process memory (legacy path)            |
|   14 | Magic blob missing (reinstall)                               |
|   15 | AES key not found in target exe (build not supported)        |
|   16 | Key derivation failed                                        |
|   20 | Self-test: reference .dat files missing                      |
|   21 | Self-test: magic unwrap does not match reference             |
|   99 | Unhandled exception                                          |

## Building from source

See [BUILD.md](BUILD.md). Short version:

```powershell
cd src\RpfSealer
dotnet build -c Release
# Output: src\RpfSealer\bin\Release\RpfSealer.exe  (~850 KB, self-contained)
```

`Costura.Fody` is wired in to produce a single-file `RpfSealer.exe` with all
dependencies embedded. To inspect the embedded resources, open the `.exe` in
dnSpyEx or ILSpy.

## Attribution

### Prior art

**Affix** (aka **ArchiveFix**) by GTAForums user **crypter**, released on
[October 17, 2016][affix]. RpfSealer is a clean-room modernisation of
that workflow for current GTA V builds; no Affix source or binary content
is redistributed. Thanks to crypter for the original tool and to
**Neodymium** for the research it stood on.

### Third-party components

All bundled components are MIT-licensed. RpfSealer itself is MIT (see
[LICENSE](LICENSE)). The standard MIT permission text below applies to
every component in this section.

| Component | Author | Source | Role |
|---|---|---|---|
| **RageLib** / **RageLib.GTA5** | Neodymium | [Neodymium146/gta-toolkit](https://github.com/Neodymium146/gta-toolkit) | RPF archive format + crypto primitives. DLLs shipped in `libs/` are derivative works of upstream gta-toolkit with encrypt-path additions from the CodeWalker lineage (also MIT). See [BUILD.md](BUILD.md). |
| **magic.dat** + `UseMagicData` algorithm | dexyfex / CodeWalker | [dexyfex/CodeWalker](https://github.com/dexyfex/CodeWalker) | Pre-baked encrypted key blob + AES-unwrap routine, replacing Affix's hash-window scan. Verbatim copy of `CodeWalker.Core/Resources/magic.dat` (SHA-1 `0768e698862cce8ad3ca1683e6388f76dff26758`). `src/RpfSealer/MagicLoader.cs` is a clean-room reimplementation of `CodeWalker.Core/GameFiles/Utils/GTAKeys.cs`'s `UseMagicData`. |
| **Spectre.Console** | Patrik Svensson et al. | [spectreconsole/spectre.console](https://github.com/spectreconsole/spectre.console) | Terminal UI rendering. |
| **HtmlAgilityPack** | ZZZ Projects | [zzzprojects/html-agility-pack](https://github.com/zzzprojects/html-agility-pack) | Transitively referenced by RageLib.GTA5. |
| **DirectXTex** (managed wrapper) | via gta-toolkit `Libraries/` | [Microsoft/DirectXTex](https://github.com/Microsoft/DirectXTex) | Transitively referenced by RageLib texture helpers. |
| **Costura.Fody** + **Fody** | Simon Cropp / Fody team | [Fody/Costura](https://github.com/Fody/Costura) | Build-time single-file packer. |

### MIT License (applies to every third-party component above)

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to
deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.
```

### Rockstar Games

RpfSealer does not redistribute any Rockstar Games intellectual property.
The magic blob contains only derived cryptographic constants; it is inert
until unlocked by an AES key taken at runtime from the user's own installed
copy of GTA V.

## License

MIT — see [LICENSE](LICENSE).
