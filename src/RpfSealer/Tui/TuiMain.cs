// TuiMain — Spectre.Console-powered terminal UI.
//
// Entered from Program.Main() when RpfSealer is launched with no arguments,
// or explicitly via `RpfSealer tui`. Provides the same operations as the
// CLI (seal, keys, help) behind an arrow-key-navigable menu.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using RageLib.GTA5.Cryptography;
using RageLib.Helpers;
using Spectre.Console;

namespace RpfSealer.Tui
{
    internal static class TuiMain
    {
        private static readonly string BaseDir =
            Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);

        private static readonly string[] KeyFiles =
        {
            "gtav_aes_key.dat", "gtav_ng_key.dat",
            "gtav_ng_decrypt_tables.dat", "gtav_ng_encrypt_tables.dat",
            "gtav_ng_encrypt_luts.dat", "gtav_hash_lut.dat",
        };

        public static int Run()
        {
            while (true)
            {
                AnsiConsole.Clear();
                RenderHeader();
                RenderStatus();

                var choice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[bold]What would you like to do?[/]")
                        .PageSize(8)
                        .HighlightStyle(new Style(foreground: Color.Red1, decoration: Decoration.Bold))
                        .AddChoices(new[]
                        {
                            "Seal an RPF archive",
                            "Derive keys from a running game",
                            "Advanced",
                            "About / attribution",
                            "Exit",
                        }));

                switch (choice)
                {
                    case "Seal an RPF archive":             DoSeal(); break;
                    case "Derive keys from a running game": DoKeys(); break;
                    case "Advanced":                        DoAdvanced(); break;
                    case "About / attribution":             DoAbout(); break;
                    case "Exit":                            return 0;
                }
            }
        }

        // -----------------------------------------------------------------
        // screens
        // -----------------------------------------------------------------

        private static void DoSeal()
        {
            AnsiConsole.Clear();
            RenderHeader();
            AnsiConsole.Write(new Rule("[red1]Seal an RPF[/]").LeftJustified());
            AnsiConsole.WriteLine();

            var missing = KeyFiles.Where(f => !File.Exists(Path.Combine(BaseDir, f))).ToList();
            if (missing.Count > 0)
            {
                AnsiConsole.MarkupLine("[yellow]No keys on disk yet.[/] Run [bold]Derive keys[/] first.");
                AnsiConsole.MarkupLine("Missing:");
                foreach (var m in missing) AnsiConsole.MarkupLine($"  [grey]{m.EscapeMarkup()}[/]");
                PressAnyKey();
                return;
            }

            string path = AnsiConsole.Prompt(
                new TextPrompt<string>("Path to unencrypted [bold].rpf[/] (or drag onto this window):")
                    .PromptStyle("cyan")
                    .Validate(p =>
                    {
                        if (string.IsNullOrWhiteSpace(p)) return ValidationResult.Error("empty");
                        p = p.Trim().Trim('"');
                        return File.Exists(p)
                            ? ValidationResult.Success()
                            : ValidationResult.Error($"not a file: {p}");
                    }));

            path = path.Trim().Trim('"');

            int rc = 0;
            AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(new Style(foreground: Color.Red1))
                .Start($"Sealing {Path.GetFileName(path)}...", _ => { rc = Program.Seal(path); });

            AnsiConsole.WriteLine();
            if (rc == 0)
                AnsiConsole.MarkupLine("[green]OK — archive is NG-sealed.[/]");
            else
                AnsiConsole.MarkupLine($"[red]Seal failed[/] [grey](exit {rc})[/]");
            PressAnyKey();
        }

        private static void DoKeys()
        {
            AnsiConsole.Clear();
            RenderHeader();
            AnsiConsole.Write(new Rule("[red1]Derive keys[/]").LeftJustified());
            AnsiConsole.WriteLine();

            var candidates = Program.FindCandidateProcesses().ToList();
            if (candidates.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No GTA V-like process detected.[/]");
                AnsiConsole.MarkupLine("Start the game first, then try again.");
                PressAnyKey();
                return;
            }

            Process target;
            if (candidates.Count == 1)
            {
                target = candidates[0];
                AnsiConsole.MarkupLine($"Auto-selected [cyan]{target.ProcessName.EscapeMarkup()}[/] [grey](PID {target.Id})[/]");
            }
            else
            {
                target = AnsiConsole.Prompt(
                    new SelectionPrompt<Process>()
                        .Title("Multiple candidates running - pick one:")
                        .PageSize(10)
                        .UseConverter(p =>
                        {
                            string title = "";
                            try { title = p.MainWindowTitle; } catch { }
                            return $"{p.ProcessName,-24}  PID {p.Id,-6}  {title}";
                        })
                        .AddChoices(candidates));
            }

            string exePath = Program.ResolveGameExe(target);
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                AnsiConsole.MarkupLine("[red]Could not resolve game executable.[/] Try running as Administrator.");
                PressAnyKey();
                return;
            }

            AnsiConsole.MarkupLine($"Game exe: [grey]{exePath.EscapeMarkup()}[/]");
            AnsiConsole.WriteLine();

            int rc = 0;
            AnsiConsole.Progress()
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(new ProgressColumn[]
                {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn { CompletedStyle = new Style(Color.Red1) },
                    new PercentageColumn(),
                    new RemainingTimeColumn(),
                    new SpinnerColumn(Spinner.Known.Dots) { Style = new Style(Color.Grey) },
                })
                .Start(ctx =>
                {
                    var scan = ctx.AddTask("Scanning exe for AES key", maxValue: 1);
                    byte[] magic = MagicLoader.LoadBlob();
                    if (magic == null)
                    {
                        AnsiConsole.MarkupLine("[red]Magic blob missing — reinstall.[/]");
                        rc = 14;
                        return;
                    }

                    byte[] exeBytes = File.ReadAllBytes(exePath);
                    byte[] aesKey;
                    using (var ms = new MemoryStream(exeBytes))
                        aesKey = HashSearch.SearchHash(ms, GTA5HashConstants.PC_AES_KEY_HASH, 32);
                    scan.Value = 1;
                    scan.StopTask();

                    if (aesKey == null)
                    {
                        AnsiConsole.MarkupLine("[red]AES key not found — build not supported.[/]");
                        rc = 15;
                        return;
                    }

                    var derive = ctx.AddTask("Deriving encrypt tables + LUTs", maxValue: 17);
                    try
                    {
                        MagicLoader.Load(magic, aesKey, (cur, total, detail) =>
                        {
                            derive.Value = cur;
                            derive.Description = $"Deriving [grey]— {detail.EscapeMarkup()}[/]";
                        });
                        derive.StopTask();
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]Derivation failed:[/] {ex.Message.EscapeMarkup()}");
                        rc = 16;
                        return;
                    }

                    var save = ctx.AddTask("Writing key files", maxValue: 1);
                    MagicLoader.SaveDatFiles(BaseDir);
                    save.Value = 1;
                    save.StopTask();
                });

            AnsiConsole.WriteLine();
            if (rc == 0)
            {
                AnsiConsole.MarkupLine($"[green]Keys derived.[/] Written to [grey]{BaseDir.EscapeMarkup()}[/]");
                AnsiConsole.MarkupLine("You can now seal RPFs.");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Keys failed[/] [grey](exit {rc})[/]");
            }
            PressAnyKey();
        }

        // -----------------------------------------------------------------
        // Advanced submenu — CLI-only operations surfaced for TUI users.
        // -----------------------------------------------------------------
        private static void DoAdvanced()
        {
            while (true)
            {
                AnsiConsole.Clear();
                RenderHeader();
                AnsiConsole.Write(new Rule("[red1]Advanced[/]").LeftJustified());
                AnsiConsole.WriteLine();

                var choice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Pick an operation:")
                        .PageSize(8)
                        .HighlightStyle(new Style(foreground: Color.Red1, decoration: Decoration.Bold))
                        .AddChoices(new[]
                        {
                            "Diagnostics (keys, magic, paths)",
                            "List candidate GTA V processes",
                            "Run self-test (verify magic vs reference .dat)",
                            "Derive keys via legacy pipeline (--legacy)",
                            "View raw CLI reference",
                            "Back to main menu",
                        }));

                if (choice.StartsWith("Diagnostics"))         DoDiagnostics();
                else if (choice.StartsWith("List candidate")) DoProcessList();
                else if (choice.StartsWith("Run self-test"))  DoSelfTestScreen();
                else if (choice.StartsWith("Derive keys via")) DoKeysLegacyScreen();
                else if (choice.StartsWith("View raw"))       DoCliReference();
                else if (choice.StartsWith("Back"))           return;
            }
        }

        private static void DoDiagnostics()
        {
            AnsiConsole.Clear();
            RenderHeader();
            AnsiConsole.Write(new Rule("[red1]Diagnostics[/]").LeftJustified());
            AnsiConsole.WriteLine();

            var asm = Assembly.GetExecutingAssembly();
            var version = asm.GetName().Version?.ToString() ?? "?";

            AnsiConsole.MarkupLine($"[grey]Version:[/]  {version}");
            AnsiConsole.MarkupLine($"[grey]Base dir:[/] {BaseDir.EscapeMarkup()}");
            AnsiConsole.WriteLine();

            // Key files
            var ktbl = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
            ktbl.AddColumn("[bold]Key file[/]");
            ktbl.AddColumn("[bold]Status[/]");
            ktbl.AddColumn("[bold]Size[/]");
            ktbl.AddColumn("[bold]Modified[/]");
            foreach (var f in KeyFiles)
            {
                string p = Path.Combine(BaseDir, f);
                if (File.Exists(p))
                {
                    var fi = new FileInfo(p);
                    ktbl.AddRow(f, "[green]OK[/]", FormatBytes(fi.Length), fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
                }
                else
                {
                    ktbl.AddRow(f, "[red]missing[/]", "-", "-");
                }
            }
            AnsiConsole.Write(ktbl);
            AnsiConsole.WriteLine();

            // Magic blob
            byte[] magic = MagicLoader.LoadBlob();
            AnsiConsole.MarkupLine("[bold]Magic blob[/]");
            if (magic == null)
            {
                AnsiConsole.MarkupLine("  [red]not found[/] (neither embedded nor on disk)");
            }
            else
            {
                string sha1;
                using (var h = System.Security.Cryptography.SHA1.Create())
                    sha1 = BitConverter.ToString(h.ComputeHash(magic)).Replace("-", "").ToLowerInvariant();
                bool embedded;
                using (var s = asm.GetManifestResourceStream("magic.dat")) embedded = s != null;
                AnsiConsole.MarkupLine($"  [grey]source:[/] {(embedded ? "embedded resource" : "Resources/magic.dat on disk")}");
                AnsiConsole.MarkupLine($"  [grey]size:[/]   {FormatBytes(magic.Length)}");
                AnsiConsole.MarkupLine($"  [grey]sha-1:[/]  [dim]{sha1}[/]");
            }
            AnsiConsole.WriteLine();
            PressAnyKey();
        }

        private static void DoProcessList()
        {
            AnsiConsole.Clear();
            RenderHeader();
            AnsiConsole.Write(new Rule("[red1]Candidate GTA V processes[/]").LeftJustified());
            AnsiConsole.WriteLine();

            var cands = Program.FindCandidateProcesses().ToList();
            if (cands.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]None detected.[/] Start the game first if you want to derive keys.");
                AnsiConsole.WriteLine();
                PressAnyKey();
                return;
            }

            var tbl = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
            tbl.AddColumn("[bold]PID[/]");
            tbl.AddColumn("[bold]Process[/]");
            tbl.AddColumn("[bold]Window title[/]");
            tbl.AddColumn("[bold]Exe path[/]");
            foreach (var p in cands)
            {
                string title = "", path = "";
                try { title = p.MainWindowTitle; } catch { }
                try { path = p.MainModule.FileName; } catch { path = "[grey](needs admin)[/]"; }
                tbl.AddRow(p.Id.ToString(), p.ProcessName, title.EscapeMarkup(), path.EscapeMarkup());
            }
            AnsiConsole.Write(tbl);
            AnsiConsole.WriteLine();
            PressAnyKey();
        }

        private static void DoSelfTestScreen()
        {
            AnsiConsole.Clear();
            RenderHeader();
            AnsiConsole.Write(new Rule("[red1]Self-test[/]").LeftJustified());
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine("Checks that the bundled magic blob unwraps to a known-good key set.");
            AnsiConsole.MarkupLine("You need a directory containing [bold]gtav_aes_key.dat[/], [bold]gtav_ng_key.dat[/],");
            AnsiConsole.MarkupLine("[bold]gtav_ng_decrypt_tables.dat[/], and [bold]gtav_hash_lut.dat[/] to compare against.");
            AnsiConsole.WriteLine();

            string dir = AnsiConsole.Prompt(
                new TextPrompt<string>("Reference directory:")
                    .DefaultValue(BaseDir)
                    .PromptStyle("cyan")
                    .Validate(d =>
                    {
                        d = d?.Trim().Trim('"');
                        return Directory.Exists(d)
                            ? ValidationResult.Success()
                            : ValidationResult.Error($"not a directory: {d}");
                    }));
            dir = dir.Trim().Trim('"');

            AnsiConsole.WriteLine();
            int rc = Program.SelfTest(new[] { dir });
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(rc == 0
                ? "[green]Self-test passed.[/]"
                : $"[red]Self-test failed[/] [grey](exit {rc})[/]");
            PressAnyKey();
        }

        private static void DoKeysLegacyScreen()
        {
            AnsiConsole.Clear();
            RenderHeader();
            AnsiConsole.Write(new Rule("[red1]Derive keys — legacy pipeline[/]").LeftJustified());
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine("[yellow]The legacy pipeline scans process memory for 101 NG keys and then[/]");
            AnsiConsole.MarkupLine("[yellow]scans the exe for 272 decrypt tables.[/] It is much slower than the");
            AnsiConsole.MarkupLine("default path and [bold]hangs indefinitely on GTA V Enhanced builds[/]");
            AnsiConsole.MarkupLine("because their table layout doesn't match the 2018-era hashes.");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Use this only as a diagnostic, or on pre-Enhanced Legacy GTA V.");
            AnsiConsole.WriteLine();

            if (!AnsiConsole.Confirm("Continue?", defaultValue: false))
            {
                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                PressAnyKey();
                return;
            }

            var cands = Program.FindCandidateProcesses().ToList();
            if (cands.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No GTA V-like process detected.[/]");
                PressAnyKey();
                return;
            }

            Process target = cands.Count == 1
                ? cands[0]
                : AnsiConsole.Prompt(
                    new SelectionPrompt<Process>()
                        .Title("Pick a process:")
                        .UseConverter(p =>
                        {
                            string t = ""; try { t = p.MainWindowTitle; } catch { }
                            return $"{p.ProcessName}  (PID {p.Id})  {t}";
                        })
                        .AddChoices(cands));

            string exePath = Program.ResolveGameExe(target);
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                AnsiConsole.MarkupLine("[red]Could not resolve game executable.[/]");
                PressAnyKey();
                return;
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[grey]Target:[/] {target.ProcessName} [grey](PID {target.Id})[/]");
            AnsiConsole.MarkupLine($"[grey]Exe:[/]    {exePath.EscapeMarkup()}");
            AnsiConsole.MarkupLine("[grey]Running legacy pipeline (output below; Ctrl+C to abort):[/]");
            AnsiConsole.WriteLine();

            int rc = Program.KeysLegacy(target, exePath);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(rc == 0
                ? "[green]Legacy derivation succeeded.[/]"
                : $"[red]Legacy derivation failed[/] [grey](exit {rc})[/]");
            PressAnyKey();
        }

        private static void DoCliReference()
        {
            AnsiConsole.Clear();
            RenderHeader();
            AnsiConsole.Write(new Rule("[red1]CLI reference[/]").LeftJustified());
            AnsiConsole.WriteLine();

            var tbl = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
            tbl.AddColumn(new TableColumn("[bold]Command[/]").NoWrap());
            tbl.AddColumn("[bold]Description[/]");

            tbl.AddRow("RpfSealer seal <file.rpf>",         "Encrypt an unencrypted RPF with platform NG keys.");
            tbl.AddRow("RpfSealer keys",                    "Derive keys from a running GTA V (magic-blob path).");
            tbl.AddRow("RpfSealer keys --pid <id>",         "Target a specific PID.");
            tbl.AddRow("RpfSealer keys --legacy",           "Original memory-scan path. Hangs on Enhanced.");
            tbl.AddRow("RpfSealer processes",               "List candidate GTA V processes.");
            tbl.AddRow("RpfSealer self-test [dir]",         "Verify magic unwrap against reference .dat files.");
            tbl.AddRow("RpfSealer tui",                     "Launch this terminal UI explicitly.");
            tbl.AddRow("RpfSealer <file.rpf>",              "Drag-drop or positional: shortcut for 'seal'.");

            AnsiConsole.Write(tbl);
            AnsiConsole.WriteLine();
            PressAnyKey();
        }

        private static string FormatBytes(long n)
        {
            if (n < 1024) return $"{n} B";
            if (n < 1024 * 1024) return $"{n / 1024.0:F1} KB";
            return $"{n / 1024.0 / 1024.0:F1} MB";
        }

        private static void DoAbout()
        {
            AnsiConsole.Clear();
            RenderHeader();
            AnsiConsole.Write(new Rule("[red1]About[/]").LeftJustified());
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine("[bold]RpfSealer[/]  —  GTA V RPF NG-encryption tool");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]Built on[/]:");
            AnsiConsole.MarkupLine("  • RageLib / RageLib.GTA5  (Neodymium, MIT)");
            AnsiConsole.MarkupLine("  • magic.dat + unwrap algorithm  (dexyfex / CodeWalker, MIT)");
            AnsiConsole.MarkupLine("  • Spectre.Console  (Patrik Svensson, MIT)");
            AnsiConsole.MarkupLine("  • Costura.Fody + Fody  (MIT)");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]License[/]: MIT. See [bold]NOTICE.txt[/] next to the exe for the full chain.");
            AnsiConsole.MarkupLine("[grey]No Rockstar Games IP is bundled.[/]");
            AnsiConsole.WriteLine();
            PressAnyKey();
        }

        // -----------------------------------------------------------------
        // layout helpers
        // -----------------------------------------------------------------

        private static void RenderHeader()
        {
            var panel = new Panel("[bold red1]RpfSealer[/] [grey]·[/] [grey]GTA V RPF NG-encryption tool[/]")
                .Border(BoxBorder.Double)
                .BorderStyle(new Style(Color.Red1))
                .Padding(1, 0);
            AnsiConsole.Write(panel);
        }

        private static void RenderStatus()
        {
            int present = KeyFiles.Count(f => File.Exists(Path.Combine(BaseDir, f)));
            string keysLine = present == KeyFiles.Length
                ? $"[green]Keys:[/] {present}/{KeyFiles.Length} loaded"
                : $"[yellow]Keys:[/] {present}/{KeyFiles.Length} present [grey](run 'Derive keys' to complete)[/]";
            AnsiConsole.MarkupLine(keysLine);
            AnsiConsole.MarkupLine($"[grey]Base dir:[/] {BaseDir.EscapeMarkup()}");
            AnsiConsole.WriteLine();
        }

        private static void PressAnyKey()
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey](press any key to return to menu)[/]");
            Console.ReadKey(true);
        }
    }
}
