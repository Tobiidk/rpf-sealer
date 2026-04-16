// MainForm — minimal double-click launcher UI.
//
// Shown only when RpfSealer is launched with no arguments and no attached
// console (typical double-click / drag-onto-exe-shortcut case). The CLI
// remains the primary interface; this form just gives non-technical users
// a discoverable way to trigger the two main commands.

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RpfSealer.Gui
{
    internal sealed class MainForm : Form
    {
        private readonly Button _sealBtn;
        private readonly Button _keysBtn;
        private readonly Button _helpBtn;
        private readonly Button _closeBtn;
        private readonly TextBox _log;
        private readonly ProgressBar _progress;
        private readonly Label _status;

        public MainForm()
        {
            Text = "RpfSealer";
            ClientSize = new Size(540, 440);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            _sealBtn  = MakeButton("Seal an RPF...",   12,  12, 165, 40, OnSealClick);
            _keysBtn  = MakeButton("Derive Keys...",  187,  12, 165, 40, OnKeysClick);
            _helpBtn  = MakeButton("Help",            362,  12,  78, 40, OnHelpClick);
            _closeBtn = MakeButton("Close",           450,  12,  78, 40, (s, e) => Close());

            _log = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                ReadOnly = true,
                Location = new Point(12, 64),
                Size = new Size(516, 312),
                Font = new Font(FontFamily.GenericMonospace, 9f),
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(220, 220, 220),
            };

            _progress = new ProgressBar
            {
                Location = new Point(12, 384),
                Size = new Size(516, 18),
                Style = ProgressBarStyle.Continuous,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
            };

            _status = new Label
            {
                Location = new Point(12, 410),
                Size = new Size(516, 20),
                Text = "Ready. Run `keys` once per game install, then `seal` individual RPFs.",
                ForeColor = SystemColors.GrayText,
            };

            Controls.AddRange(new Control[] { _sealBtn, _keysBtn, _helpBtn, _closeBtn,
                                              _log, _progress, _status });

            // Redirect Console writes to the log box so the existing CLI helpers
            // (which print status via Console.Write*) produce visible output.
            var writer = new TextBoxWriter(_log);
            Console.SetOut(writer);
            Console.SetError(writer);
        }

        // -----------------------------------------------------------------
        // button handlers
        // -----------------------------------------------------------------

        private async void OnSealClick(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog
            {
                Filter = "RPF archives (*.rpf)|*.rpf|All files (*.*)|*.*",
                Title = "Select an unencrypted RPF to seal",
            })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                string path = ofd.FileName;

                SetBusy(true, $"Sealing {Path.GetFileName(path)}...");
                int rc = await Task.Run(() => Program.Seal(path));
                SetBusy(false, rc == 0 ? "Sealed OK." : $"Seal failed (exit code {rc}).");

                if (rc == 0)
                    MessageBox.Show(this, "Archive sealed successfully.", "RpfSealer",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    MessageBox.Show(this, "Seal failed — see the log for details.", "RpfSealer",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void OnKeysClick(object sender, EventArgs e)
        {
            var candidates = Program.FindCandidateProcesses().ToList();
            Process target;
            if (candidates.Count == 0)
            {
                MessageBox.Show(this,
                    "No GTA V-like process detected.\n\nStart the game first, then try again.",
                    "RpfSealer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            else if (candidates.Count == 1)
            {
                target = candidates[0];
            }
            else
            {
                using (var picker = new ProcessPickerForm(candidates))
                {
                    if (picker.ShowDialog(this) != DialogResult.OK) return;
                    target = picker.Selected;
                }
            }

            Console.WriteLine($"Target: {target.ProcessName} (PID {target.Id})");
            string exePath = Program.ResolveGameExe(target);
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                MessageBox.Show(this,
                    "Could not resolve the game executable for that process.\n\nTry running as Administrator.",
                    "RpfSealer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            SetBusy(true, "Deriving keys...");
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 0;

            int rc = await Task.Run(() =>
            {
                // Hook progress reports into the progress bar while the derivation runs.
                // KeysMagic uses Progress.Create internally and writes to Console;
                // we additionally install a lightweight progress hook by wrapping
                // MagicLoader.Load, but KeysMagic composes both. To keep this simple,
                // just let the console log carry step-by-step text, and use an
                // indeterminate marquee while we wait.
                return Program.KeysMagic(exePath);
            });

            SetBusy(false, rc == 0 ? "Keys derived successfully." : $"Key derivation failed (exit code {rc}).");
            MessageBox.Show(this,
                rc == 0
                    ? "Keys derived. You can now seal RPFs."
                    : "Key derivation failed — see the log.",
                "RpfSealer",
                MessageBoxButtons.OK,
                rc == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        }

        private void OnHelpClick(object sender, EventArgs e)
        {
            _log.Clear();
            Console.WriteLine("RpfSealer — GTA V RPF NG-encryption tool");
            Console.WriteLine();
            Console.WriteLine("Two commands do all the work:");
            Console.WriteLine();
            Console.WriteLine("  Derive Keys    Start GTA V, click this button, pick the process.");
            Console.WriteLine("                 Takes ~30-60 seconds. Only needs to be done once");
            Console.WriteLine("                 per install; keys are cached next to RpfSealer.exe.");
            Console.WriteLine();
            Console.WriteLine("  Seal an RPF    Pick an unencrypted .rpf file; it gets re-encrypted");
            Console.WriteLine("                 with NG so GTA V will load it. Binds encryption to");
            Console.WriteLine("                 the filename — do not rename after sealing.");
            Console.WriteLine();
            Console.WriteLine("This window appears on double-click. For scripting, run RpfSealer.exe");
            Console.WriteLine("from a console and use `RpfSealer help` for the CLI reference.");
            Console.WriteLine();
            Console.WriteLine("Attribution: see NOTICE.txt next to the executable.");
        }

        // -----------------------------------------------------------------
        // helpers
        // -----------------------------------------------------------------

        private static Button MakeButton(string text, int x, int y, int w, int h,
                                          EventHandler onClick)
        {
            var b = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                FlatStyle = FlatStyle.System,
            };
            b.Click += onClick;
            return b;
        }

        private void SetBusy(bool busy, string statusText)
        {
            _sealBtn.Enabled = !busy;
            _keysBtn.Enabled = !busy;
            _helpBtn.Enabled = !busy;
            _status.Text = statusText ?? "";
            _progress.Value = 0;
            _progress.Style = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            _progress.MarqueeAnimationSpeed = busy ? 30 : 0;
            if (!busy) _progress.Value = 0;
        }
    }
}
