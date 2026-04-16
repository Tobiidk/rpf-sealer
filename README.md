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

The older community tool *ArchiveFix* did this on Legacy GTA V by scanning
`GTA5.exe` in memory and on disk for the 272 NG decrypt tables by SHA-1. On
GTA V Enhanced the exe layout changed enough that the hash-window scan hangs
indefinitely looking for matches it can never find.

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

See [NOTICE.txt](NOTICE.txt) for the full chain: RageLib (Neodymium, MIT),
magic blob + algorithm (dexyfex / CodeWalker, MIT), HtmlAgilityPack (MIT),
DirectXTex managed wrapper (MIT), Costura.Fody (MIT). Prior art: the 2016
community tool *ArchiveFix* (author unknown) — no ArchiveFix content is
redistributed.

This project does not redistribute any Rockstar Games IP. The magic blob
holds only derived cryptographic constants; it is inert until unlocked by an
AES key taken at runtime from the user's own installed GTA V copy.

## License

MIT — see [LICENSE](LICENSE).
