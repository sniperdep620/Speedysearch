using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Speedysearch.Windows
{
    public sealed class SettingsForm : Form
    {
        private readonly AppConfig _config;
        private readonly TextBox _paths;
        private readonly TextBox _excludes;
        private readonly TextBox _hotkey;
        private readonly NumericUpDown _candidates;
        private readonly NumericUpDown _opacity;
        private readonly CheckBox _model;
        private readonly CheckBox _clickstream;
        private readonly CheckBox _topmost;
        private readonly CheckBox _startup;

        public SettingsForm(AppConfig config)
        {
            _config = config;
            Text = "Speedysearch preferences";
            Width = 620;
            Height = 590;
            MinimumSize = new Size(560, 520);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = LauncherTheme.Surface;
            ForeColor = LauncherTheme.Foreground;
            Font = new Font("Segoe UI", 9.5f);

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(18),
                ColumnCount = 2,
                RowCount = 9
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 105));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
            for (int row = 2; row < 8; row++)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            }
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            _paths = CreateTextBox(true);
            _paths.Text = String.Join(Environment.NewLine, config.WatchPaths);
            AddRow(layout, 0, "Folders to index", _paths);

            _excludes = CreateTextBox(true);
            _excludes.Text = String.Join(", ", config.ExcludePatterns);
            AddRow(layout, 1, "Excluded names", _excludes);

            _hotkey = CreateTextBox(false);
            _hotkey.Text = config.GlobalHotkey;
            AddRow(layout, 2, "Global shortcut", _hotkey);

            _candidates = new NumericUpDown
            {
                Minimum = 10,
                Maximum = 1000,
                Value = config.MaxStage1Candidates,
                Width = 120,
                BackColor = LauncherTheme.Card,
                ForeColor = ForeColor
            };
            AddRow(layout, 3, "Candidate limit", _candidates);

            _opacity = new NumericUpDown
            {
                Minimum = 55,
                Maximum = 100,
                Value = (decimal)Math.Round(config.Opacity * 100),
                Width = 120,
                BackColor = LauncherTheme.Card,
                ForeColor = ForeColor
            };
            AddRow(layout, 4, "Window opacity (%)", _opacity);

            FlowLayoutPanel ranking = new FlowLayoutPanel { Dock = DockStyle.Fill };
            _model = CreateCheckBox("Use personalized model", config.ModelEnabled);
            _clickstream = CreateCheckBox("Record local selections", config.EnableClickstream);
            ranking.Controls.Add(_model);
            ranking.Controls.Add(_clickstream);
            AddRow(layout, 5, "Ranking", ranking);

            FlowLayoutPanel behavior = new FlowLayoutPanel { Dock = DockStyle.Fill };
            _topmost = CreateCheckBox("Always on top", config.AlwaysOnTop);
            _startup = CreateCheckBox("Run at sign-in", config.RunAtStartup);
            behavior.Controls.Add(_topmost);
            behavior.Controls.Add(_startup);
            AddRow(layout, 6, "Behavior", behavior);

            Label hint = new Label
            {
                Text = "A shortcut must include Ctrl, Alt, or Win plus a non-modifier key. "
                    + "Changing indexed folders triggers a background rebuild.",
                Dock = DockStyle.Fill,
                ForeColor = LauncherTheme.Muted
            };
            layout.Controls.Add(hint, 1, 7);

            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                FlowDirection = FlowDirection.RightToLeft
            };
            Button save = CreateButton("Save", 95);
            save.DialogResult = DialogResult.None;
            Button cancel = CreateButton("Cancel", 95);
            cancel.DialogResult = DialogResult.Cancel;
            save.Click += SaveClicked;
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            layout.Controls.Add(buttons, 0, 8);
            layout.SetColumnSpan(buttons, 2);
            AcceptButton = save;
            CancelButton = cancel;
        }

        private void SaveClicked(object sender, EventArgs e)
        {
            HotkeyDefinition definition;
            if (!GlobalHotkey.TryParse(_hotkey.Text, out definition))
            {
                MessageBox.Show(
                    this,
                    "Enter a shortcut such as Ctrl+Space or Ctrl+Alt+K.",
                    "Invalid shortcut",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                _hotkey.Focus();
                return;
            }
            string[] paths = _paths.Lines
                .Select(delegate(string value) { return value.Trim(); })
                .Where(delegate(string value) { return value.Length > 0; })
                .ToArray();
            if (paths.Length == 0)
            {
                MessageBox.Show(this, "Add at least one folder to index.", "Missing folder");
                return;
            }
            _config.WatchPaths = paths.ToList();
            _config.ExcludePatterns = _excludes.Text
                .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(delegate(string value) { return value.Trim(); })
                .Where(delegate(string value) { return value.Length > 0; })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _config.GlobalHotkey = definition.Normalized;
            _config.MaxStage1Candidates = (int)_candidates.Value;
            _config.Opacity = (double)_opacity.Value / 100.0;
            _config.ModelEnabled = _model.Checked;
            _config.EnableClickstream = _clickstream.Checked;
            _config.AlwaysOnTop = _topmost.Checked;
            _config.RunAtStartup = _startup.Checked;
            DialogResult = DialogResult.OK;
            Close();
        }

        private TextBox CreateTextBox(bool multiline)
        {
            return new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = multiline,
                ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
                BackColor = LauncherTheme.Card,
                ForeColor = ForeColor,
                BorderStyle = BorderStyle.FixedSingle
            };
        }

        private CheckBox CreateCheckBox(string text, bool value)
        {
            return new CheckBox
            {
                Text = text,
                Checked = value,
                AutoSize = true,
                ForeColor = ForeColor,
                Margin = new Padding(0, 6, 16, 0)
            };
        }

        private Button CreateButton(string text, int width)
        {
            Button button = new Button
            {
                Text = text,
                Width = width,
                Height = 32,
                FlatStyle = FlatStyle.Flat,
                BackColor = LauncherTheme.Card,
                ForeColor = LauncherTheme.Foreground,
                Margin = new Padding(4, 0, 4, 0)
            };
            button.FlatAppearance.BorderColor = LauncherTheme.Border;
            return button;
        }

        private static void AddRow(TableLayoutPanel layout, int row, string label, Control control)
        {
            Label heading = new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft,
                Padding = new Padding(0, 6, 8, 0)
            };
            layout.Controls.Add(heading, 0, row);
            layout.Controls.Add(control, 1, row);
        }
    }
}
