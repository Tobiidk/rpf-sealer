// ProcessPickerForm — small dialog to pick one process when multiple
// candidates are running at once.

using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace RpfSealer.Gui
{
    internal sealed class ProcessPickerForm : Form
    {
        private readonly ListBox _list;
        private readonly Button _okBtn;
        private readonly Button _cancelBtn;
        private readonly IList<Process> _candidates;

        public Process Selected { get; private set; }

        public ProcessPickerForm(IList<Process> candidates)
        {
            _candidates = candidates;

            Text = "Pick GTA V process";
            ClientSize = new Size(420, 260);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            _list = new ListBox
            {
                Location = new Point(12, 12),
                Size = new Size(396, 196),
                IntegralHeight = false,
            };
            foreach (var p in candidates)
            {
                string title = "";
                try { title = p.MainWindowTitle; } catch { }
                _list.Items.Add($"{p.ProcessName}  (PID {p.Id})  {title}");
            }
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            _list.DoubleClick += (s, e) => Confirm();

            _okBtn = new Button
            {
                Text = "OK",
                Location = new Point(246, 220),
                Size = new Size(80, 28),
                DialogResult = DialogResult.None,
            };
            _okBtn.Click += (s, e) => Confirm();

            _cancelBtn = new Button
            {
                Text = "Cancel",
                Location = new Point(332, 220),
                Size = new Size(76, 28),
                DialogResult = DialogResult.Cancel,
            };

            AcceptButton = _okBtn;
            CancelButton = _cancelBtn;

            Controls.AddRange(new Control[] { _list, _okBtn, _cancelBtn });
        }

        private void Confirm()
        {
            int idx = _list.SelectedIndex;
            if (idx < 0 || idx >= _candidates.Count) return;
            Selected = _candidates[idx];
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
