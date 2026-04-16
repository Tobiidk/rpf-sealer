// TextBoxWriter — redirects Console.Write*/WriteLine* output into a WinForms
// TextBox, marshalling cross-thread writes to the UI thread.

using System.IO;
using System.Text;
using System.Windows.Forms;

namespace RpfSealer.Gui
{
    internal sealed class TextBoxWriter : TextWriter
    {
        private readonly TextBox _target;

        public TextBoxWriter(TextBox target) { _target = target; }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => Append(value.ToString());
        public override void Write(string value) { if (value != null) Append(value); }
        public override void WriteLine(string value) => Append((value ?? "") + "\r\n");
        public override void WriteLine() => Append("\r\n");

        private void Append(string text)
        {
            if (_target.IsDisposed) return;
            if (_target.InvokeRequired)
            {
                try { _target.BeginInvoke((MethodInvoker)(() => AppendUnsafe(text))); }
                catch { /* form closing mid-write — silently drop */ }
            }
            else
            {
                AppendUnsafe(text);
            }
        }

        private void AppendUnsafe(string text)
        {
            if (_target.IsDisposed) return;

            // Handle \r as "rewrite current line" (used by the console progress bar).
            // Without this, progress updates accumulate as visual noise.
            int carriage = text.IndexOf('\r');
            if (carriage >= 0 && !text.Contains("\r\n"))
            {
                // Strip any trailing content on the current line and replace with the new tail.
                string existing = _target.Text;
                int lastNewline = existing.LastIndexOf('\n');
                if (lastNewline < 0)
                {
                    _target.Text = text.Substring(carriage + 1);
                }
                else
                {
                    _target.Text = existing.Substring(0, lastNewline + 1) + text.Substring(carriage + 1);
                }
            }
            else
            {
                _target.AppendText(text);
            }
            _target.SelectionStart = _target.TextLength;
            _target.ScrollToCaret();
        }
    }
}
