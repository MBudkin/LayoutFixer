using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using LayoutFixer.Diagnostics;
using LayoutFixer.Input;
using LayoutFixer.Localization;

namespace LayoutFixer.UI;

/// <summary>
/// Editor of the user replacements ("пт" → "привет"). Edits are saved a moment
/// after they are made, so a new pair works while the window is still open.
/// The options (on/off, punctuation) live in Settings → Dictionaries.
/// </summary>
internal sealed class UserReplacementsForm : Form
{
    private const string TriggerColumn = "Trigger";
    private const string ReplacementColumn = "Replacement";
    private const string CaseColumn = "CaseSensitive";
    private const string EnabledColumn = "Enabled";

    private readonly UserReplacements _replacements;
    private readonly Func<bool> _isAutoReplaceEnabled;
    private readonly Action<bool> _setAutoReplaceEnabled;
    private readonly ToolTip _toolTip = new() { AutoPopDelay = 20000 };
    private readonly Label _hintLabel = new();
    private readonly LinkLabel _disabledLabel = new();
    private readonly Label _searchLabel = new();
    private readonly TextBox _searchBox = new();
    private readonly DataGridView _grid = new();
    private readonly Button _addButton = new();
    private readonly Button _removeButton = new();
    private readonly Button _clearButton = new();
    private readonly Label _countLabel = new();
    private readonly Button _exportButton = new();
    private readonly Button _importButton = new();
    private readonly Button _closeButton = new();
    private readonly Timer _saveTimer = new() { Interval = 700 };
    private bool _saving;
    private bool _loading;

    public UserReplacementsForm(
        UserReplacements replacements,
        Func<bool> isAutoReplaceEnabled,
        Action<bool> setAutoReplaceEnabled)
    {
        _replacements = replacements;
        _isAutoReplaceEnabled = isAutoReplaceEnabled;
        _setAutoReplaceEnabled = setAutoReplaceEnabled;

        Icon = AppIcons.Current;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(S(560), S(440));
        Size = new Size(S(720), S(560));

        BuildGrid();
        BuildLayout();
        LoadRows();
        ApplyLocalization();
        RefreshAutoReplaceState();

        _saveTimer.Tick += (_, _) => SaveNow();
        Localizer.LanguageChanged += HandleLanguageChanged;
        _replacements.Changed += HandleReplacementsChanged;
        FormClosing += (_, _) => SaveNow();
        FormClosed += (_, _) =>
        {
            Localizer.LanguageChanged -= HandleLanguageChanged;
            _replacements.Changed -= HandleReplacementsChanged;
            _saveTimer.Dispose();
            _toolTip.Dispose();
        };
    }

    private int S(int pixels) => LogicalToDeviceUnits(pixels);

    /// <summary>Called by the tray context when automatic replacement was switched elsewhere.</summary>
    public void RefreshAutoReplaceState()
    {
        _disabledLabel.Visible = !_isAutoReplaceEnabled();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(S(16), S(12), S(16), S(12)),
            RowCount = 6
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _hintLabel.AutoSize = true;
        _hintLabel.ForeColor = SystemColors.GrayText;
        _hintLabel.Margin = new Padding(0, 0, 0, S(6));
        root.SizeChanged += (_, _) =>
        {
            var max = new Size(Math.Max(S(200), root.ClientSize.Width - S(40)), 0);
            _hintLabel.MaximumSize = max;
            _disabledLabel.MaximumSize = max;
        };

        _disabledLabel.AutoSize = true;
        _disabledLabel.Margin = new Padding(0, 0, 0, S(6));
        _disabledLabel.LinkBehavior = LinkBehavior.HoverUnderline;
        _disabledLabel.LinkClicked += (_, _) =>
        {
            _setAutoReplaceEnabled(true);
            RefreshAutoReplaceState();
        };

        _searchLabel.AutoSize = true;
        _searchLabel.Anchor = AnchorStyles.Left;
        _searchLabel.Margin = new Padding(0, S(4), S(8), S(4));
        _searchBox.Dock = DockStyle.Fill;
        _searchBox.Margin = new Padding(0, S(3), 0, S(3));
        _searchBox.TextChanged += (_, _) => ApplyFilter();
        _searchBox.KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Escape && _searchBox.TextLength > 0)
            {
                _searchBox.Clear();
                args.SuppressKeyPress = true;
            }
        };
        var searchRow = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, S(6)) };
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchRow.Controls.Add(_searchLabel, 0, 0);
        searchRow.Controls.Add(_searchBox, 1, 0);

        ConfigureButton(_addButton, AddEntry);
        ConfigureButton(_removeButton, RemoveSelected);
        ConfigureButton(_clearButton, ClearAll);
        _countLabel.AutoSize = true;
        _countLabel.Anchor = AnchorStyles.Left;
        _countLabel.ForeColor = SystemColors.GrayText;
        _countLabel.Margin = new Padding(S(4), S(8), 0, S(4));
        var actionRow = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, S(8), 0, 0),
            WrapContents = false
        };
        actionRow.Controls.AddRange(new Control[] { _addButton, _removeButton, _clearButton, _countLabel });

        ConfigureButton(_importButton, Import);
        ConfigureButton(_exportButton, Export);
        ConfigureButton(_closeButton, Close);
        _closeButton.Margin = new Padding(0, S(3), 0, S(3));
        var footer = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, Dock = DockStyle.Fill, Margin = new Padding(0, S(10), 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(_importButton, 0, 0);
        footer.Controls.Add(_exportButton, 1, 0);
        footer.Controls.Add(new Label { AutoSize = true }, 2, 0);
        footer.Controls.Add(_closeButton, 3, 0);

        root.Controls.Add(_hintLabel, 0, 0);
        root.Controls.Add(_disabledLabel, 0, 1);
        root.Controls.Add(searchRow, 0, 2);
        root.Controls.Add(_grid, 0, 3);
        root.Controls.Add(actionRow, 0, 4);
        root.Controls.Add(footer, 0, 5);
        Controls.Add(root);
    }

    private void ConfigureButton(Button button, Action onClick)
    {
        button.AutoSize = true;
        button.MinimumSize = new Size(S(90), 0);
        button.Margin = new Padding(0, S(3), S(8), S(3));
        button.Click += (_, _) => onClick();
    }

    private void BuildGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _grid.MultiSelect = true;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
        _grid.ShowCellErrors = true;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.RowTemplate.Height = S(26);

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = TriggerColumn, FillWeight = 90, MinimumWidth = S(80) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = ReplacementColumn, FillWeight = 200, MinimumWidth = S(120) });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = CaseColumn,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = EnabledColumn,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        _grid.CellValueChanged += (_, _) =>
        {
            if (!_loading)
            {
                ValidateRows();
                ScheduleSave();
            }
        };
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            // Commit checkbox clicks immediately.
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell)
            {
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _grid.SelectionChanged += (_, _) => UpdateButtons();
        _grid.KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Delete && !_grid.IsCurrentCellInEditMode)
            {
                RemoveSelected();
                args.Handled = true;
            }
        };
    }

    private void HandleLanguageChanged(object? sender, EventArgs args)
    {
        ApplyLocalization();
        ValidateRows();
    }

    private void HandleReplacementsChanged(object? sender, EventArgs args)
    {
        // Our own saves are ignored; outside changes (backup import) reload the grid.
        if (_saving || IsDisposed || !IsHandleCreated)
        {
            return;
        }

        BeginInvoke(() =>
        {
            if (!IsDisposed && !_grid.IsCurrentCellInEditMode && !_saveTimer.Enabled)
            {
                LoadRows();
            }
        });
    }

    private void ApplyLocalization()
    {
        Text = Localizer.Get("UserReplacementsWindowTitle");
        _hintLabel.Text = Localizer.Get("UserReplacementsHint");
        string disabled = Localizer.Get("UserReplacementsDisabledNote");
        string enable = Localizer.Get("UserReplacementsEnableLink");
        _disabledLabel.Text = disabled + " " + enable;
        _disabledLabel.LinkArea = new LinkArea(disabled.Length + 1, enable.Length);
        _searchLabel.Text = Localizer.Get("UserDictionarySearch");
        _addButton.Text = Localizer.Get("UserReplacementsAddButton");
        _removeButton.Text = Localizer.Get("UserReplacementsRemoveButton");
        _clearButton.Text = Localizer.Get("UserReplacementsClearButton");
        _exportButton.Text = Localizer.Get("UserReplacementsExportButton");
        _importButton.Text = Localizer.Get("UserReplacementsImportButton");
        _closeButton.Text = Localizer.Get("SettingsCloseButton");
        _toolTip.SetToolTip(_addButton, Localizer.Get("UserReplacementsAddTooltip"));
        _toolTip.SetToolTip(_removeButton, Localizer.Get("UserReplacementsRemoveTooltip"));
        _toolTip.SetToolTip(_clearButton, Localizer.Get("UserReplacementsClearTooltip"));
        _toolTip.SetToolTip(_exportButton, Localizer.Get("UserReplacementsExportTooltip"));
        _toolTip.SetToolTip(_importButton, Localizer.Get("UserReplacementsImportTooltip"));
        _toolTip.SetToolTip(_searchBox, Localizer.Get("UserReplacementsSearchTooltip"));
        _toolTip.SetToolTip(_closeButton, Localizer.Get("SettingsCloseTooltip"));

        _grid.Columns[TriggerColumn]!.HeaderText = Localizer.Get("UserReplacementsColTrigger");
        _grid.Columns[ReplacementColumn]!.HeaderText = Localizer.Get("UserReplacementsColReplacement");
        _grid.Columns[CaseColumn]!.HeaderText = Localizer.Get("UserReplacementsColCaseSensitive");
        _grid.Columns[EnabledColumn]!.HeaderText = Localizer.Get("UserReplacementsColEnabled");
        _grid.Columns[TriggerColumn]!.ToolTipText = Localizer.Get("UserReplacementsColTriggerTooltip");
        _grid.Columns[CaseColumn]!.ToolTipText = Localizer.Get("UserReplacementsColCaseSensitiveTooltip");
        UpdateButtons();
    }

    // ── rows <-> store ───────────────────────────────────────────────────────

    private void LoadRows()
    {
        _loading = true;
        try
        {
            _grid.Rows.Clear();
            foreach (UserReplacementEntry entry in _replacements.GetAll())
            {
                _grid.Rows.Add(entry.Trigger, entry.Replacement, entry.CaseSensitive, entry.Enabled);
            }
        }
        finally
        {
            _loading = false;
        }

        ValidateRows();
        ApplyFilter();
    }

    private static UserReplacementEntry ReadRow(DataGridViewRow row)
    {
        return new UserReplacementEntry
        {
            Trigger = (row.Cells[TriggerColumn].Value as string ?? string.Empty).Trim(),
            Replacement = row.Cells[ReplacementColumn].Value as string ?? string.Empty,
            CaseSensitive = row.Cells[CaseColumn].Value is true,
            Enabled = row.Cells[EnabledColumn].Value is not false
        };
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        _saveTimer.Stop();
        if (_grid.IsCurrentCellInEditMode)
        {
            _grid.EndEdit();
        }

        // Incomplete rows stay in the grid while the window is open but are
        // not stored (UserReplacements.SetAll skips them).
        List<UserReplacementEntry> entries = _grid.Rows.Cast<DataGridViewRow>().Select(ReadRow).ToList();
        _saving = true;
        try
        {
            _replacements.SetAll(entries);
        }
        finally
        {
            _saving = false;
        }

        UpdateButtons();
    }

    // Marks problems that would stop a pair from ever firing.
    private void ValidateRows()
    {
        var seen = new List<UserReplacementEntry>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            UserReplacementEntry entry = ReadRow(row);
            string error = string.Empty;
            if (entry.Trigger.Length == 0)
            {
                error = entry.Replacement.Length > 0 ? Localizer.Get("UserReplacementsErrorEmptyTrigger") : string.Empty;
            }
            else if (entry.Trigger.Any(c => char.IsWhiteSpace(c) || c is '-' or '_'))
            {
                error = Localizer.Get("UserReplacementsErrorSeparator");
            }
            else if (seen.Any(other => SameTrigger(other, entry)))
            {
                error = Localizer.Get("UserReplacementsErrorDuplicate");
            }
            else if (entry.Replacement.Length == 0)
            {
                error = Localizer.Get("UserReplacementsErrorEmptyReplacement");
            }

            row.Cells[TriggerColumn].ErrorText = error;
            seen.Add(entry);
        }
    }

    private static bool SameTrigger(UserReplacementEntry a, UserReplacementEntry b)
    {
        // A case-insensitive pair also catches every spelling of the other one.
        bool exact = a.CaseSensitive && b.CaseSensitive;
        return string.Equals(a.Trigger, b.Trigger, exact ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyFilter()
    {
        string query = _searchBox.Text.Trim();
        _grid.CurrentCell = null;
        foreach (DataGridViewRow row in _grid.Rows)
        {
            UserReplacementEntry entry = ReadRow(row);
            row.Visible = query.Length == 0 ||
                          entry.Trigger.Length == 0 ||
                          entry.Trigger.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                          entry.Replacement.Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        int total = _grid.Rows.Count;
        int shown = _grid.Rows.Cast<DataGridViewRow>().Count(row => row.Visible);
        _removeButton.Enabled = _grid.SelectedRows.Count > 0;
        _clearButton.Enabled = total > 0;
        _countLabel.Text = shown == total
            ? Localizer.Format("UserReplacementsCount", total)
            : Localizer.Format("UserDictionaryCountFiltered", shown, total);
    }

    // ── actions ──────────────────────────────────────────────────────────────

    private void AddEntry()
    {
        if (_searchBox.TextLength > 0)
        {
            _searchBox.Clear();
        }

        int index = _grid.Rows.Add(string.Empty, string.Empty, false, true);
        _grid.ClearSelection();
        _grid.CurrentCell = _grid.Rows[index].Cells[TriggerColumn];
        _grid.Rows[index].Selected = true;
        _grid.BeginEdit(true);
        UpdateButtons();
    }

    private void RemoveSelected()
    {
        // Require an explicit selection: never fall back to the "current" row.
        List<DataGridViewRow> rows = _grid.SelectedRows.Cast<DataGridViewRow>().ToList();
        if (rows.Count == 0)
        {
            return;
        }

        if (rows.Count > 1 &&
            MessageBox.Show(
                this,
                Localizer.Format("UserReplacementsRemoveConfirm", rows.Count),
                Localizer.Get("UserReplacementsWindowTitle"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        foreach (DataGridViewRow row in rows)
        {
            _grid.Rows.Remove(row);
        }

        ValidateRows();
        SaveNow();
    }

    private void ClearAll()
    {
        if (_grid.Rows.Count == 0)
        {
            return;
        }

        if (MessageBox.Show(
                this,
                Localizer.Get("UserReplacementsClearConfirm"),
                Localizer.Get("UserReplacementsWindowTitle"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        _grid.Rows.Clear();
        SaveNow();
    }

    private void Export()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = Localizer.Get("UserReplacementsJsonFilter"),
            FileName = "LayoutFixer-user-replacements.json",
            DefaultExt = "json"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            SaveNow();
            File.WriteAllText(dialog.FileName, _replacements.ExportToJson(), new UTF8Encoding(false));
            _countLabel.Text = Localizer.Format("UserReplacementsExportDone", _replacements.Count);
        }
        catch (Exception ex)
        {
            Logger.Log($"UserReplacements export failed: {ex}");
            DialogService.ShowError(this, "UserReplacementsExportFailed");
        }
    }

    private void Import()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = Localizer.Get("UserReplacementsJsonFilter"),
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            SaveNow();
            int added = _replacements.ImportFromJson(File.ReadAllText(dialog.FileName));
            LoadRows();
            MessageBox.Show(
                this,
                Localizer.Format("UserReplacementsImportDone", added),
                Localizer.Get("UserReplacementsWindowTitle"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Logger.Log($"UserReplacements import failed: {ex}");
            DialogService.ShowError(this, "UserReplacementsImportFailed");
        }
    }
}
