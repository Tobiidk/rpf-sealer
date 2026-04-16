// RpfSealer — NG-seals GTA V RPF archives, derives keys from a running game.
//
// Two primary commands:
//   seal <file.rpf>     Toggle encryption flag to NG on an unencrypted RPF7,
//                       bind it to its filename, write it back.
//   keys                Extract the AES key from a running GTA V process,
//                       unlock the bundled magic blob, derive and save the
//                       six .dat files the RageLib runtime expects.
//
// Works with Legacy GTA V, GTA V Enhanced, and FiveM-family launchers.
// See README.md for the why, NOTICE.txt for attribution.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using RageLib.GTA5.ArchiveWrappers;
using RageLib.GTA5.Archives;
using RageLib.GTA5.Cryptography;
using RageLib.GTA5.Cryptography.Helpers;
using RageLib.Helpers;

namespace RpfSealer
{
    internal static class Program
    {
        // ---- constants ---------------------------------------------------

        private const string ToolName = "RpfSealer";

        private static readonly string BaseDir =
            Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);

        private static readonly string[] RequiredKeyFiles =
        {
            "gtav_aes_key.dat",
            "gtav_ng_key.dat",
            "gtav_ng_decrypt_tables.dat",
            "gtav_ng_encrypt_tables.dat",
            "gtav_ng_encrypt_luts.dat",
            "gtav_hash_lut.dat",
        };

        // Process-name hints for auto-detection. Extend freely.
        private static readonly string[] KnownGameProcesses =
        {
            "GTA5", "GTA5_Enhanced", "PlayGTAV",
            "***",
            "FiveM", "FiveM_b2944", "FiveReborn",
        };

        // Standard PE layout offsets used by the process memory scan.
        private const int PE_LFANEW_OFFSET     = 0x3C;
        private const int PE_SIZE_OF_IMAGE_OFF = 0x50; // within optional header
        private const int KEY_BLOB_SIZE        = 272;

        // ---- entry -------------------------------------------------------

        [System.STAThread]
        private static int Main(string[] args)
        {
            // Bare double-click (no args and no attached console): show the GUI
            // launcher instead of the "press any key to exit" stub. The CLI
            // remains the primary interface for every other invocation.
            if (args.Length == 0 && !InvokedFromConsole())
            {
                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
                System.Windows.Forms.Application.Run(new Gui.MainForm());
                return 0;
            }

            int exitCode = 1;
            try
            {
                Console.OutputEncoding = Encoding.UTF8;

                // Drag-drop path: first arg is an existing file and not a verb.
                if (args.Length > 0 && File.Exists(args[0]) && !IsKnownVerb(args[0]))
                {
                    exitCode = Seal(args[0]);
                    return exitCode;
                }

                string cmd = (args.Length > 0 ? args[0] : "").ToLowerInvariant();
                switch (cmd)
                {
                    case "seal":
                    case "fix": // alias for ArchiveFix migrants
                        if (args.Length < 2)
                        {
                            Console.Error.WriteLine($"Usage: {ToolName} seal <path-to-rpf>");
                            exitCode = 2;
                        }
                        else exitCode = Seal(args[1]);
                        break;

                    case "keys":
                    case "fetch": // alias for ArchiveFix migrants
                        exitCode = Keys(args.Skip(1).ToArray());
                        break;

                    case "processes":
                    case "list-processes":
                    case "list":
                        ListProcesses();
                        exitCode = 0;
                        break;

                    case "self-test":
                        exitCode = SelfTest(args.Skip(1).ToArray());
                        break;

                    case "":
                    case "help":
                    case "--help":
                    case "-h":
                    case "/?":
                        PrintUsage();
                        exitCode = 0;
                        break;

                    default:
                        Console.Error.WriteLine($"Unknown command: {cmd}");
                        Console.Error.WriteLine();
                        PrintUsage();
                        exitCode = 2;
                        break;
                }
                return exitCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Unhandled error: {ex.Message}");
                return 99;
            }
            finally
            {
                // If launched by double-click or drag-drop, pause so output is visible.
                if (!InvokedFromConsole())
                {
                    Console.WriteLine();
                    Console.WriteLine("Press any key to exit.");
                    Console.ReadKey(true);
                }
            }
        }

        private static bool IsKnownVerb(string s)
        {
            string[] verbs = { "seal", "fix", "keys", "fetch", "processes", "list",
                               "list-processes", "self-test", "help", "--help", "-h", "/?" };
            return verbs.Any(v => string.Equals(v, s, StringComparison.OrdinalIgnoreCase));
        }

        private static bool InvokedFromConsole()
            => GetConsoleProcessList(new uint[2], 2u) > 1;

        private static void PrintUsage()
        {
            Console.WriteLine($"{ToolName} - GTA V RPF NG-encryption tool");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine($"  {ToolName} seal <file.rpf>              Encrypt an RPF with platform NG keys.");
            Console.WriteLine($"  {ToolName} keys [--process <name>]      Derive keys from a running GTA V.");
            Console.WriteLine( "                                          Default: scans exe for AES key,");
            Console.WriteLine( "                                          unlocks bundled magic blob. Works");
            Console.WriteLine( "                                          on Legacy and Enhanced builds.");
            Console.WriteLine($"  {ToolName} keys --pid <id>              Target a specific PID.");
            Console.WriteLine($"  {ToolName} keys --legacy                Original pipeline: memory-scan + exe-scan.");
            Console.WriteLine( "                                          Hangs on Enhanced builds; avoid.");
            Console.WriteLine($"  {ToolName} processes                    Show candidate GTA V processes.");
            Console.WriteLine($"  {ToolName} self-test [dir]              Verify magic unwrap vs reference .dat files.");
            Console.WriteLine($"  {ToolName} <file.rpf>                   Drag-and-drop an RPF here to seal it.");
            Console.WriteLine();
            Console.WriteLine("Key files (written by `keys`, loaded by `seal`):");
            foreach (var f in RequiredKeyFiles) Console.WriteLine($"  {f}");
        }

        // ---- seal --------------------------------------------------------

        internal static int Seal(string path)
        {
            var missing = RequiredKeyFiles
                .Where(f => !File.Exists(Path.Combine(BaseDir, f)))
                .ToList();
            if (missing.Count > 0)
            {
                Console.Error.WriteLine("Missing key files:");
                foreach (var m in missing) Console.Error.WriteLine($"  {m}");
                Console.Error.WriteLine($"Run '{ToolName} keys' first, or copy the .dat files next to this executable.");
                return 3;
            }

            try { GTA5Constants.LoadFromPath(BaseDir); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to load keys: {ex.Message}");
                return 3;
            }

            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"File not found: {path}");
                return 4;
            }

            try
            {
                using (var wrapper = RageArchiveWrapper7.Open(path))
                {
                    if ((int)wrapper.archive_.Encryption != 0)
                    {
                        Console.Error.WriteLine("This packfile is already encrypted - nothing to do.");
                        return 5;
                    }
                    wrapper.archive_.Encryption = (RageArchiveEncryption7)2; // NG
                    wrapper.Flush();
                }
                Console.WriteLine($"OK: {path} is now NG-encrypted.");
                Console.WriteLine($"Note: encryption is bound to the filename '{Path.GetFileName(path)}' - do not rename it.");
                return 0;
            }
            catch (FileNotFoundException ex)
            {
                Console.Error.WriteLine($"File not found: {ex.FileName}");
                return 4;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to seal archive: {ex.Message}");
                return 6;
            }
        }

        // ---- keys (magic + legacy) ---------------------------------------

        private static int Keys(string[] args)
        {
            string processName = null;
            int pid = -1;
            bool useLegacy = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--process" when i + 1 < args.Length: processName = args[++i]; break;
                    case "--pid"     when i + 1 < args.Length: int.TryParse(args[++i], out pid); break;
                    case "--legacy": useLegacy = true; break;
                }
            }

            Process target = SelectTarget(pid, processName);
            if (target == null) return 10;

            Console.WriteLine($"Target process: {target.ProcessName} (PID {target.Id})");
            string exePath = ResolveGameExe(target);
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                Console.Error.WriteLine($"Could not resolve game executable path (got: '{exePath}').");
                Console.Error.WriteLine("Tip: run as Administrator if the target is elevated.");
                return 12;
            }
            Console.WriteLine($"Game executable: {exePath}");
            Console.WriteLine();

            return useLegacy
                ? KeysLegacy(target, exePath)
                : KeysMagic(exePath);
        }

        internal static int KeysMagic(string exePath)
        {
            byte[] magic = MagicLoader.LoadBlob();
            if (magic == null)
            {
                Console.Error.WriteLine("Magic blob not found (embedded resource missing and no Resources\\magic.dat on disk).");
                Console.Error.WriteLine($"Re-install {ToolName} or use --legacy.");
                return 14;
            }

            Console.WriteLine("Reading executable and searching for AES key...");
            byte[] exeBytes = File.ReadAllBytes(exePath);
            byte[] aesKey;
            using (var ms = new MemoryStream(exeBytes))
                aesKey = HashSearch.SearchHash(ms, GTA5HashConstants.PC_AES_KEY_HASH, 32);
            if (aesKey == null)
            {
                Console.Error.WriteLine("AES key not found. This build is not supported by the tool's hash table.");
                return 15;
            }
            Console.WriteLine("AES key found.");
            Console.WriteLine();

            try
            {
                Console.WriteLine("Unlocking magic blob and deriving encrypt tables (a few minutes on older CPUs)...");
                var bar = Progress.Create("Deriving");
                MagicLoader.Load(magic, aesKey, (cur, total, detail) => bar.Report(cur, total, detail));
                bar.Finish();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Key derivation failed: {ex.Message}");
                return 16;
            }

            Console.WriteLine();
            Console.WriteLine("Writing key files...");
            MagicLoader.SaveDatFiles(BaseDir);
            Console.WriteLine($"Done. Key files written to: {BaseDir}");
            return 0;
        }

        private static int KeysLegacy(Process target, string exePath)
        {
            string ngKeyPath = Path.Combine(BaseDir, "gtav_ng_key.dat");
            if (!File.Exists(ngKeyPath))
            {
                Console.WriteLine("Scanning process memory for NG keys (101 keys to find)...");
                var keys = ScanProcessForKeyBlobs(target, GTA5HashConstants.PC_NG_KEY_HASHES, KEY_BLOB_SIZE);
                if (keys == null)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("NG keys not found. Build's key layout is unknown to this tool.");
                    return 13;
                }
                GTA5Constants.PC_NG_KEYS = keys;
                CryptoIO.WriteNgKeys(ngKeyPath, keys);
                Console.WriteLine("NG keys extracted.");
            }
            else
            {
                Console.WriteLine("NG keys already present - skipping scan.");
            }

            Console.WriteLine();
            Console.WriteLine($"Generating lookup tables from {Path.GetFileName(exePath)} (this takes a while)...");
            Console.WriteLine("Note: on Enhanced this step hangs - use the default (omit --legacy).");
            GTA5Constants.Generate(File.ReadAllBytes(exePath));
            Console.WriteLine();
            Console.WriteLine($"Done. Key files written to: {BaseDir}");
            return 0;
        }

        // ---- process discovery -------------------------------------------

        private static Process SelectTarget(int pid, string processName)
        {
            if (pid > 0)
            {
                try { return Process.GetProcessById(pid); }
                catch (ArgumentException)
                {
                    Console.Error.WriteLine($"No process with PID {pid}.");
                    return null;
                }
            }

            var candidates = FindCandidateProcesses(processName).ToList();
            if (candidates.Count == 0)
            {
                Console.Error.WriteLine(processName != null
                    ? $"No running process named '{processName}' found."
                    : "No GTA V-like process detected. Start the game, or use --process <name> / --pid <id>.");
                return null;
            }
            if (candidates.Count == 1) return candidates[0];

            Console.WriteLine("Multiple candidate processes found:");
            for (int i = 0; i < candidates.Count; i++)
            {
                var p = candidates[i];
                Console.WriteLine($"  [{i}] {p.ProcessName} (PID {p.Id})  {p.MainWindowTitle}");
            }
            Console.Write("Choose (number, Enter to cancel): ");
            string input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) { Console.WriteLine("Cancelled."); return null; }
            if (!int.TryParse(input, out int sel) || sel < 0 || sel >= candidates.Count)
            {
                Console.Error.WriteLine("Invalid selection.");
                return null;
            }
            return candidates[sel];
        }

        internal static IEnumerable<Process> FindCandidateProcesses(string explicitName = null)
        {
            var all = Process.GetProcesses();

            if (!string.IsNullOrWhiteSpace(explicitName))
            {
                string want = explicitName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? explicitName.Substring(0, explicitName.Length - 4)
                    : explicitName;
                return all.Where(p => string.Equals(p.ProcessName, want, StringComparison.OrdinalIgnoreCase));
            }

            return all.Where(p =>
            {
                try
                {
                    if (KnownGameProcesses.Any(n => string.Equals(p.ProcessName, n, StringComparison.OrdinalIgnoreCase)))
                        return true;
                    // Tight window-title check: R*'s own games use "Grand Theft Auto".
                    return !string.IsNullOrWhiteSpace(p.MainWindowTitle)
                        && p.MainWindowTitle.IndexOf("Grand Theft Auto", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                catch { return false; }
            }).ToList();
        }

        private static void ListProcesses()
        {
            var cands = FindCandidateProcesses().ToList();
            if (cands.Count == 0)
            {
                Console.WriteLine("No candidate processes running.");
                return;
            }
            Console.WriteLine("PID\tProcess\tWindow title");
            foreach (var p in cands)
            {
                string title = "";
                try { title = p.MainWindowTitle; } catch { }
                Console.WriteLine($"{p.Id}\t{p.ProcessName}\t{title}");
            }
        }

        internal static string ResolveGameExe(Process target)
        {
            string path;
            try { path = target.MainModule.FileName; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Cannot read main module ({ex.Message}).");
                return null;
            }

            bool isFiveM = target.ProcessName.StartsWith("FiveM", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(target.ProcessName, "FiveReborn", StringComparison.OrdinalIgnoreCase);
            if (!isFiveM) return path;

            // FiveM-family launchers store the real GTA install path in CitizenFX.ini's IVPath.
            string dir = Path.GetDirectoryName(path);
            string ini = Path.Combine(dir, "CitizenFX.ini");
            if (!File.Exists(ini)) return path;

            string ivLine = File.ReadAllLines(ini).FirstOrDefault(l => l.StartsWith("IVPath=", StringComparison.OrdinalIgnoreCase));
            if (ivLine == null) return path;

            string gameDir = ivLine.Split(new[] { '=' }, 2)[1].Trim();
            foreach (string name in new[] { "GTA5.exe", "GTA5_Enhanced.exe", "PlayGTAV.exe", "***.exe" })
            {
                string full = Path.Combine(gameDir, name);
                if (File.Exists(full)) return full;
            }
            return path;
        }

        // ---- process memory scan (clean-room) ----------------------------
        //
        // Layout:
        //   [0, 0x400000)     — too low; contains PE header and game code, never keys.
        //   [0x400000, imageSize) — scan window; keys live in .rdata/.data.
        //
        // Strategy: read overlapping 4 MiB chunks, slide a 272-byte window on
        // 8-byte boundaries, SHA-1 each window, match against a dictionary
        // keyed by the first 8 bytes of each target hash (cheap prefilter).
        private static byte[][] ScanProcessForKeyBlobs(
            Process proc, IList<byte[]> expectedHashes, int blobSize)
        {
            IntPtr baseAddr = proc.MainModule.BaseAddress;

            int peLfanew = ReadU32(proc.Handle, baseAddr + PE_LFANEW_OFFSET);
            int imageSize = ReadU32(proc.Handle, baseAddr + peLfanew + PE_SIZE_OF_IMAGE_OFF);

            // Prefix lookup: first 8 bytes of SHA-1 → list of hash indices sharing that prefix.
            var prefixIndex = new Dictionary<ulong, List<int>>();
            for (int i = 0; i < expectedHashes.Count; i++)
            {
                ulong prefix = BitConverter.ToUInt64(expectedHashes[i], 0);
                if (!prefixIndex.TryGetValue(prefix, out var list))
                    prefixIndex[prefix] = list = new List<int>();
                list.Add(i);
            }

            var found = new byte[expectedHashes.Count][];
            int foundCount = 0;

            const int ChunkSize    = 4 * 1024 * 1024;
            const int ScanFromAddr = 20 * 1024 * 1024; // skip the low code region
            const int Stride       = 8;

            var chunk = new byte[ChunkSize];
            using (var sha1 = SHA1.Create())
            {
                // Overlap each read by blobSize so blobs straddling chunk edges aren't missed.
                for (int cursor = ScanFromAddr;
                     cursor < imageSize && foundCount < expectedHashes.Count;
                     cursor += ChunkSize - blobSize)
                {
                    int toRead = Math.Min(ChunkSize, imageSize - cursor);
                    if (!ReadProcessMemory(proc.Handle, baseAddr + cursor, chunk, toRead, out _))
                        continue;

                    Console.Write('.');

                    int scanEnd = toRead - blobSize;
                    for (int off = 0; off <= scanEnd; off += Stride)
                    {
                        byte[] hash = sha1.ComputeHash(chunk, off, blobSize);
                        ulong prefix = BitConverter.ToUInt64(hash, 0);
                        if (!prefixIndex.TryGetValue(prefix, out var cands)) continue;

                        foreach (int idx in cands)
                        {
                            if (found[idx] != null) continue;
                            if (!BytesEqual(hash, expectedHashes[idx])) continue;

                            var copy = new byte[blobSize];
                            Buffer.BlockCopy(chunk, off, copy, 0, blobSize);
                            found[idx] = copy;
                            foundCount++;
                            Console.Write($" [{foundCount}/{expectedHashes.Count}]");
                        }
                    }
                }
            }
            Console.WriteLine();

            return foundCount == expectedHashes.Count ? found : null;
        }

        private static int ReadU32(IntPtr hProc, IntPtr addr)
        {
            var buf = new byte[4];
            ReadProcessMemory(hProc, addr, buf, 4, out _);
            return BitConverter.ToInt32(buf, 0);
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ---- self-test ---------------------------------------------------

        private static int SelfTest(string[] args)
        {
            string dir = args.Length > 0 ? args[0] : BaseDir;
            string aesPath    = Path.Combine(dir, "gtav_aes_key.dat");
            string ngKeyPath  = Path.Combine(dir, "gtav_ng_key.dat");
            string tablesPath = Path.Combine(dir, "gtav_ng_decrypt_tables.dat");
            string lutPath    = Path.Combine(dir, "gtav_hash_lut.dat");

            foreach (var f in new[] { aesPath, ngKeyPath, tablesPath, lutPath })
                if (!File.Exists(f))
                {
                    Console.Error.WriteLine($"Missing: {f}");
                    return 20;
                }

            byte[] magic = MagicLoader.LoadBlob();
            if (magic == null)
            {
                Console.Error.WriteLine("Magic blob not found (embedded or on disk).");
                return 20;
            }

            byte[] aesKey = File.ReadAllBytes(aesPath);
            Console.WriteLine($"AES key: {aesKey.Length} bytes");
            Console.WriteLine($"Magic:   {magic.Length} bytes");
            Console.WriteLine("Unwrapping magic blob...");
            var bar = Progress.Create("Deriving");
            MagicLoader.Load(magic, aesKey, (cur, total, detail) => bar.Report(cur, total, detail));
            bar.Finish();

            var expectedKeys = CryptoIO.ReadNgKeys(ngKeyPath);
            int keyMismatches = 0;
            for (int i = 0; i < 101; i++)
                if (!BytesEqual(expectedKeys[i], GTA5Constants.PC_NG_KEYS[i])) keyMismatches++;
            Console.WriteLine($"NG keys:        {(keyMismatches == 0 ? "MATCH" : keyMismatches + " mismatches")}");

            var expectedTables = CryptoIO.ReadNgTables(tablesPath);
            int tableMismatches = 0;
            for (int i = 0; i < 17; i++)
                for (int j = 0; j < 16; j++)
                    for (int k = 0; k < 256; k++)
                        if (expectedTables[i][j][k] != GTA5Constants.PC_NG_DECRYPT_TABLES[i][j][k]) tableMismatches++;
            Console.WriteLine($"Decrypt tables: {(tableMismatches == 0 ? "MATCH" : tableMismatches + " mismatches")}");

            byte[] expectedLut = File.ReadAllBytes(lutPath);
            bool lutOk = BytesEqual(expectedLut, GTA5Constants.PC_LUT);
            Console.WriteLine($"PC_LUT:         {(lutOk ? "MATCH" : "MISMATCH")}");

            bool allOk = keyMismatches == 0 && tableMismatches == 0 && lutOk;
            Console.WriteLine();
            Console.WriteLine(allOk
                ? "PASS - magic blob unwraps to the expected key material."
                : "FAIL - magic unwrap does not match reference .dat files.");
            return allOk ? 0 : 21;
        }

        // ---- Win32 P/Invoke ---------------------------------------------

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
            [Out] byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetConsoleProcessList(uint[] ProcessList, uint ProcessCount);
    }
}
