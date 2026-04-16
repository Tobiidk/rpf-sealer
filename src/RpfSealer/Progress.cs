// Progress — lightweight single-line console progress indicator with a
// redirect-safe fallback.
//
// Usage:
//   var p = Progress.Create("Deriving keys");
//   for (int i = 0; i < total; i++)
//   {
//       p.Report(i, total, $"step {i}");
//       ...
//   }
//   p.Finish();
//
// When stdout is a TTY: rewrites a single line with \r.
// When stdout is redirected or piped: falls back to line-per-step output.

using System;

namespace RpfSealer
{
    internal abstract class Progress
    {
        public static Progress Create(string label)
        {
            return Console.IsOutputRedirected
                ? (Progress)new LineProgress(label)
                : new InlineProgress(label);
        }

        public abstract void Report(int current, int total, string detail = null);
        public abstract void Finish(string trailingMessage = null);

        // -----------------------------------------------------------------
        // inline (TTY) — rewrite a single line in place
        // -----------------------------------------------------------------
        private sealed class InlineProgress : Progress
        {
            private readonly string _label;
            private int _lastWidth;

            public InlineProgress(string label) { _label = label; }

            public override void Report(int current, int total, string detail = null)
            {
                int percent = total > 0 ? (int)(100L * current / total) : 0;
                string bar = RenderBar(percent, 20);
                string detailPart = string.IsNullOrEmpty(detail) ? "" : $" — {detail}";
                string line = $"{_label}: {bar} {current}/{total} ({percent}%){detailPart}";

                // Pad to erase any leftover text from a previous, longer line.
                int pad = Math.Max(0, _lastWidth - line.Length);
                Console.Write('\r' + line + new string(' ', pad));
                _lastWidth = line.Length;
            }

            public override void Finish(string trailingMessage = null)
            {
                Console.WriteLine();
                if (!string.IsNullOrEmpty(trailingMessage))
                    Console.WriteLine(trailingMessage);
            }

            private static string RenderBar(int percent, int width)
            {
                int filled = Math.Min(width, Math.Max(0, percent * width / 100));
                return "[" + new string('#', filled) + new string('.', width - filled) + "]";
            }
        }

        // -----------------------------------------------------------------
        // line-per-step fallback (non-TTY, e.g. redirected to a file)
        // -----------------------------------------------------------------
        private sealed class LineProgress : Progress
        {
            private readonly string _label;
            public LineProgress(string label) { _label = label; }

            public override void Report(int current, int total, string detail = null)
            {
                string detailPart = string.IsNullOrEmpty(detail) ? "" : $" — {detail}";
                Console.WriteLine($"{_label}: {current}/{total}{detailPart}");
            }

            public override void Finish(string trailingMessage = null)
            {
                if (!string.IsNullOrEmpty(trailingMessage))
                    Console.WriteLine(trailingMessage);
            }
        }
    }
}
