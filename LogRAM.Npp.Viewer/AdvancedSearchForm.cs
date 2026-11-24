namespace LogRAM.Npp;

internal sealed class AdvancedSearchForm : Form
{
    private readonly bool _dark;
    private readonly FlowLayoutPanel _includePanel = new();
    private readonly FlowLayoutPanel _excludePanel = new();
    private readonly Button _addInclude = new();
    private readonly Button _addExclude = new();
    private readonly Button _close = new();
    private readonly Button _search = new();

    internal string? QueryText { get; private set; }

    internal AdvancedSearchForm(string initialQuery, bool dark)
    {
        _dark = dark;
        var s = Loc.S;
        Text = s.AdvancedTitle;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(390, 360);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 8
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var hint = new Label
        {
            Text = s.AdvancedHint,
            AutoSize = true,
            MaximumSize = new Size(360, 0),
            Margin = new Padding(0, 0, 0, 8),
            ForeColor = dark ? Color.FromArgb(157, 167, 176) : Color.FromArgb(93, 104, 115)
        };
        var includeLabel = new Label { Text = s.IncludeLabel, AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        var excludeLabel = new Label { Text = s.ExcludeLabel, AutoSize = true, Margin = new Padding(0, 8, 0, 4) };

        ConfigureListPanel(_includePanel);
        ConfigureListPanel(_excludePanel);

        _addInclude.Text = s.AddIncludeButton;
        _addInclude.AutoSize = true;
        _addInclude.Margin = new Padding(0, 4, 0, 4);
        _addInclude.Click += (_, _) => AddRow(_includePanel, "");

        _addExclude.Text = s.AddExcludeButton;
        _addExclude.AutoSize = true;
        _addExclude.Margin = new Padding(0, 4, 0, 8);
        _addExclude.Click += (_, _) => AddRow(_excludePanel, "");

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 4, 0, 0)
        };
        _search.Text = s.SearchButton;
        _search.Width = 76;
        _search.Height = 28;
        _search.Click += (_, _) => Submit();

        _close.Text = s.CloseButton;
        _close.Width = 76;
        _close.Height = 28;
        _close.DialogResult = DialogResult.Cancel;

        footer.Controls.Add(_search);
        footer.Controls.Add(_close);

        root.Controls.Add(hint, 0, 0);
        root.Controls.Add(includeLabel, 0, 1);
        root.Controls.Add(_includePanel, 0, 2);
        root.Controls.Add(_addInclude, 0, 3);
        root.Controls.Add(excludeLabel, 0, 4);
        root.Controls.Add(_excludePanel, 0, 5);
        root.Controls.Add(_addExclude, 0, 6);
        root.Controls.Add(footer, 0, 7);
        Controls.Add(root);

        AcceptButton = _search;
        CancelButton = _close;

        ApplyTheme();
        Populate(initialQuery);
    }

    private static void ConfigureListPanel(FlowLayoutPanel panel)
    {
        panel.Dock = DockStyle.Fill;
        panel.FlowDirection = FlowDirection.TopDown;
        panel.WrapContents = false;
        panel.AutoScroll = true;
        panel.Margin = new Padding(0);
    }

    private void Populate(string initialQuery)
    {
        if (AdvancedSearchQuery.TryParse(initialQuery, out var parsed))
        {
            foreach (var term in parsed.Includes) AddRow(_includePanel, term, focus: false);
            foreach (var term in parsed.Excludes) AddRow(_excludePanel, term, focus: false);
        }

        if (_includePanel.Controls.Count == 0) AddRow(_includePanel, "", focus: false);
        if (_excludePanel.Controls.Count == 0) AddRow(_excludePanel, "", focus: false);

        Shown += (_, _) =>
        {
            var firstBox = _includePanel.Controls.OfType<Panel>().FirstOrDefault()?.Controls.OfType<TextBox>().FirstOrDefault();
            firstBox?.Focus();
        };
    }

    private void AddRow(FlowLayoutPanel host, string text, bool focus = true)
    {
        var rowWidth = Math.Max(300, host.ClientSize.Width > 20 ? host.ClientSize.Width - 20 : 340);
        var row = new Panel { Width = rowWidth, Height = 28, Margin = new Padding(0, 2, 0, 2) };
        var box = new TextBox
        {
            Text = text,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(0, 2),
            Width = rowWidth - 32,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
        };
        var remove = new Button
        {
            Text = "×",
            Width = 26,
            Height = 23,
            Location = new Point(rowWidth - 28, 2),
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
            FlatStyle = _dark ? FlatStyle.Flat : FlatStyle.Standard
        };
        remove.Click += (_, _) => host.Controls.Remove(row);
        StyleInput(box);
        StyleButton(remove);
        row.Controls.Add(box);
        row.Controls.Add(remove);
        host.Controls.Add(row);
        if (focus) box.Focus();
    }

    private void Submit()
    {
        var includes = Collect(_includePanel);
        var excludes = Collect(_excludePanel);
        if (includes.Count == 0 && excludes.Count == 0)
        {
            MessageBox.Show(this, Loc.S.CannotSearchNoKeyword, Loc.S.CannotSearchTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (includes.Concat(excludes).Any(term => term.Any(ch => ch > '\x7F')))
        {
            MessageBox.Show(this, Loc.S.CannotSearchAscii, Loc.S.CannotSearchTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        QueryText = AdvancedSearchQuery.Format(includes, excludes);
        DialogResult = DialogResult.OK;
        Close();
    }

    private static List<string> Collect(FlowLayoutPanel host)
    {
        var list = new List<string>();
        foreach (var row in host.Controls.OfType<Panel>())
        {
            var box = row.Controls.OfType<TextBox>().FirstOrDefault();
            var value = box?.Text.Trim();
            if (!string.IsNullOrEmpty(value)) list.Add(value);
        }
        return list;
    }

    private void ApplyTheme()
    {
        var back = _dark ? Color.FromArgb(32, 37, 42) : SystemColors.Control;
        var fore = _dark ? Color.Gainsboro : SystemColors.ControlText;
        BackColor = back;
        ForeColor = fore;
        _includePanel.BackColor = back;
        _excludePanel.BackColor = back;
        StyleButton(_addInclude);
        StyleButton(_addExclude);
        StyleButton(_close);
        StyleButton(_search);
    }

    private void StyleInput(TextBox box)
    {
        box.BackColor = _dark ? Color.FromArgb(21, 24, 27) : SystemColors.Window;
        box.ForeColor = _dark ? Color.Gainsboro : SystemColors.WindowText;
    }

    private void StyleButton(Button button)
    {
        if (_dark)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Color.FromArgb(42, 48, 55);
            button.ForeColor = Color.Gainsboro;
            button.FlatAppearance.BorderColor = Color.FromArgb(68, 76, 86);
        }
        else
        {
            button.FlatStyle = FlatStyle.Standard;
            button.UseVisualStyleBackColor = true;
            button.ForeColor = SystemColors.ControlText;
        }
    }
}
