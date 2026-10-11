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
/// Editor of the user dictionary: two lists (Russian / English words), a
/// shared search box, one "add" box that picks the list by the word's
/// alphabet, inline editing, moving a word to the other list, and a column
/// that shows how the word looks when typed in the wrong layout.
/// </summary>
internal sealed class UserDictionaryForm : Form
{
    private readonly UserDictionary _dictionary;
    private readonly LayoutConverter _converter = new();
    private readonly ToolTip _toolTip = new() { AutoPopDelay = 20000 };
    private readonly Label _introLabel = new();
    private readonly Label _searchLabel = new();
    private readonly TextBox _searchBox = new();
    private readonly TabControl _tabs = new();
    private readonly WordList _russianList;
    private readonly WordList _englishList;
    private readonly Label _addLabel = new();
    private readonly TextBox _addBox = new();
    private readonly Button _addButton = new();
    private readonly Button _removeButton = new();
    private readonly Button _clearButton = new();
    private readonly Label _countLabel = new();
    private readonly Button _importButton = new();
    private readonly Button _exportButton = new();
    private readonly Button _closeButton = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _editItem = new();
    private readonly ToolStripMenuItem _moveItem = new();
    private readonly ToolStripMenuItem _copyItem = new();
    private readonly ToolStripMenuItem _removeItem = new();
    private bool _refreshPending;

    public UserDictionaryForm(UserDictionary dictionary)
    {
        _dictionary = dictionary;
        _russianList = new WordList(ConvertedLanguage.Russian);
        _englishList = new WordList(ConvertedLanguage.English);

        Icon = AppIcons.Current;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(S(520), S(520));
        Size = new Size(S(640), S(680));

        BuildLayout();
        BuildContextMenu();
        ApplyLocalization();
        RefreshWords();

        Localizer.LanguageChanged += HandleLanguageChanged;
        _dictionary.Changed += HandleDictionaryChanged;
        FormClosed += (_, _) =>
        {
            Localizer.LanguageChanged -= HandleLanguageChanged;
            _dictionary.Changed -= HandleDictionaryChanged;
            _toolTip.Dispose();
            _menu.Dispose();
        };
    }

    private int S(int pixels) => LogicalToDeviceUnits(pixels);

    private WordList CurrentList => _tabs.SelectedIndex == 1 ? _englishList : _russianList;

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
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _introLabel.AutoSize = true;
        _introLabel.ForeColor = SystemColors.GrayText;
        _introLabel.Margin = new Padding(0, 0, 0, S(8));
        _introLabel.MaximumSize = new Size(S(560), 0);
        root.SizeChanged += (_, _) => _introLabel.MaximumSize = new Size(Math.Max(S(200), root.ClientSize.Width - S(40)), 0);

        _searchLabel.AutoSize = true;
        _searchLabel.Anchor = AnchorStyles.Left;
        _searchLabel.Margin = new Padding(0, S(4), S(8), S(4));
        _searchBox.Dock = DockStyle.Fill;
        _searchBox.Margin = new Padding(0, S(3), 0, S(3));
        _searchBox.TextChanged += (_, _) => RefreshWords();
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

        _tabs.Dock = DockStyle.Fill;
        foreach (WordList list in new[] { _russianList, _englishList })
        {
            var page = new TabPage { Padding = new Padding(S(4)) };
            ConfigureList(list.View);
            page.Controls.Add(list.View);
            _tabs.TabPages.Add(page);
        }

        _tabs.SelectedIndexChanged += (_, _) => UpdateButtons();

        _addLabel.AutoSize = true;
        _addLabel.Anchor = AnchorStyles.Left;
        _addLabel.Margin = new Padding(0, S(4), S(8), S(4));
        _addBox.Dock = DockStyle.Fill;
        _addBox.Margin = new Padding(0, S(4), S(8), S(4));
        _addBox.KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Enter)
            {
                args.SuppressKeyPress = true;
                AddWord();
            }
        };
        ConfigureButton(_addButton, AddWord);

        var addRow = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Fill, Margin = new Padding(0, S(8), 0, 0) };
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        addRow.Controls.Add(_addLabel, 0, 0);
        addRow.Controls.Add(_addBox, 1, 0);
        addRow.Controls.Add(_addButton, 2, 0);

        ConfigureButton(_removeButton, RemoveSelected);
        ConfigureButton(_clearButton, ClearCurrentList);
        _countLabel.AutoSize = true;
        _countLabel.Anchor = AnchorStyles.Left;
        _countLabel.ForeColor = SystemColors.GrayText;
        _countLabel.Margin = new Padding(S(4), S(8), 0, S(4));
        var actionRow = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, S(2), 0, 0),
            WrapContents = false
        };
        actionRow.Controls.AddRange(new Control[] { _removeButton, _clearButton, _countLabel });

        ConfigureButton(_importButton, Import);
        ConfigureButton(_exportButton, Export);
        ConfigureButton(_closeButton, Close);
        _closeButton.Margin = new Padding(0, S(3), 0, S(3));
        CancelButton = _closeButton;
        var footer = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, Dock = DockStyle.Fill, Margin = new Padding(0, S(10), 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(_importButton, 0, 0);
        footer.Controls.Add(_exportButton, 1, 0);
        footer.Controls.Add(new Label { AutoSize = true }, 2, 0);
        footer.Controls.Add(_closeButton, 3, 0);

        root.Controls.Add(_introLabel, 0, 0);
        root.Controls.Add(searchRow, 0, 1);
        root.Controls.Add(_tabs, 0, 2);
        root.Controls.Add(addRow, 0, 3);
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

    private void ConfigureList(ListView view)
    {
        view.Dock = DockStyle.Fill;
        view.View = View.Details;
        view.FullRowSelect = true;
        view.HideSelection = false;
        view.LabelEdit = true;
        view.MultiSelect = true;
        view.BorderStyle = BorderStyle.None;
        view.HeaderStyle = ColumnHeaderStyle.Clickable;
        view.Columns.Add(string.Empty, S(260));
        view.Columns.Add(string.Empty, S(240));
        view.ContextMenuStrip = _menu;
        view.SelectedIndexChanged += (_, _) => UpdateButtons();
        view.DoubleClick += (_, _) => BeginEditSelected();
        view.Resize += (_, _) => FitColumns(view);
        view.ColumnClick += (_, args) => ToggleSort(view, args.Column);
        view.AfterLabelEdit += HandleAfterLabelEdit;
        view.KeyDown += (_, args) =>
        {
            switch (args.KeyCode)
            {
                case Keys.Delete:
                    RemoveSelected();
                    args.Handled = true;
                    break;
                case Keys.F2:
                    BeginEditSelected();
                    args.Handled = true;
                    break;
                case Keys.A when args.Control:
                    foreach (ListViewItem item in view.Items)
                    {
                        item.Selected = true;
                    }

                    args.Handled = true;
                    break;
                case Keys.C when args.Control:
                    CopySelected();
                    args.Handled = true;
                    break;
            }
        };
    }

    private static void FitColumns(ListView view)
    {
        int width = view.ClientSize.Width;
        if (width <= 0 || view.Columns.Count < 2)
        {
            return;
        }

        view.Columns[0].Width = width / 2;
        view.Columns[1].Width = width - width / 2;
    }

    private void BuildContextMenu()
    {
        _editItem.Click += (_, _) => BeginEditSelected();
        _editItem.ShortcutKeyDisplayString = "F2";
        _moveItem.Click += (_, _) => MoveSelected();
        _copyItem.Click += (_, _) => CopySelected();
        _copyItem.ShortcutKeyDisplayString = "Ctrl+C";
        _removeItem.Click += (_, _) => RemoveSelected();
        _removeItem.ShortcutKeyDisplayString = "Del";
        _menu.Items.AddRange(new ToolStripItem[] { _editItem, _moveItem, _copyItem, new ToolStripSeparator(), _removeItem });
        _menu.Opening += (_, args) =>
        {
            int count = CurrentList.View.SelectedItems.Count;
            if (count == 0)
            {
                args.Cancel = true;
                return;
            }

            _editItem.Enabled = count == 1;
            _moveItem.Text = Localizer.Format("UserDictionaryMoveTo", LanguageName(Other(CurrentList.Language)));
        };
    }

    private void HandleLanguageChanged(object? sender, EventArgs args)
    {
        ApplyLocalization();
        RefreshWords();
    }

    private void HandleDictionaryChanged(object? sender, EventArgs args)
    {
        // Coalesce bursts of changes (import) into one refresh on the UI thread.
        if (IsDisposed || !IsHandleCreated || _refreshPending)
        {
            return;
        }

        _refreshPending = true;
        BeginInvoke(() =>
        {
            _refreshPending = false;
            if (!IsDisposed)
            {
                RefreshWords();
            }
        });
    }

    private void ApplyLocalization()
    {
        Text = Localizer.Get("UserDictionaryWindowTitle");
        _introLabel.Text = Localizer.Get("UserDictionaryIntro");
        _searchLabel.Text = Localizer.Get("UserDictionarySearch");
        _addLabel.Text = Localizer.Get("UserDictionaryAddLabel");
        _addButton.Text = Localizer.Get("UserDictionaryAddButton");
        _removeButton.Text = Localizer.Get("UserDictionaryRemoveButton");
        _clearButton.Text = Localizer.Get("UserDictionaryClearButton");
        _importButton.Text = Localizer.Get("UserDictionaryImportButton");
        _exportButton.Text = Localizer.Get("UserDictionaryExportButton");
        _closeButton.Text = Localizer.Get("SettingsCloseButton");
        _editItem.Text = Localizer.Get("UserDictionaryEdit");
        _copyItem.Text = Localizer.Get("UserDictionaryCopy");
        _removeItem.Text = Localizer.Get("UserDictionaryRemoveButton");
        foreach (WordList list in new[] { _russianList, _englishList })
        {
            list.View.Columns[0].Text = Localizer.Get("UserDictionaryColWord");
            list.View.Columns[1].Text = Localizer.Get("UserDictionaryColWrongLayout");
        }

        _toolTip.SetToolTip(_searchBox, Localizer.Get("UserDictionarySearchTooltip"));
        _toolTip.SetToolTip(_addBox, Localizer.Get("UserDictionaryAddTooltip"));
        _toolTip.SetToolTip(_addButton, Localizer.Get("UserDictionaryAddTooltip"));
        _toolTip.SetToolTip(_removeButton, Localizer.Get("UserDictionaryRemoveTooltip"));
        _toolTip.SetToolTip(_clearButton, Localizer.Get("UserDictionaryClearTooltip"));
        _toolTip.SetToolTip(_closeButton, Localizer.Get("SettingsCloseTooltip"));
        _toolTip.SetToolTip(_exportButton, Localizer.Get("UserDictionaryExportTooltip"));
        _toolTip.SetToolTip(_importButton, Localizer.Get("UserDictionaryImportTooltip"));
    }

    private void RefreshWords()
    {
        string query = _searchBox.Text.Trim();
        foreach (WordList list in new[] { _russianList, _englishList })
        {
            Fill(list, query);
        }

        int ru = _dictionary.Count(ConvertedLanguage.Russian);
        int en = _dictionary.Count(ConvertedLanguage.English);
        _tabs.TabPages[0].Text = Localizer.Format("UserDictionaryTabTitle", Localizer.Get("LanguageRussian"), ru);
        _tabs.TabPages[1].Text = Localizer.Format("UserDictionaryTabTitle", Localizer.Get("LanguageEnglish"), en);
        UpdateButtons();
    }

    private void Fill(WordList list, string query)
    {
        ListView view = list.View;
        var selected = new HashSet<string>(view.SelectedItems.Cast<ListViewItem>().Select(i => i.Text), StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> words = _dictionary.GetWords(list.Language);

        view.BeginUpdate();
        try
        {
            view.Items.Clear();
            foreach (string word in words)
            {
                string wrong = _converter.Convert(word, list.Language);
                if (query.Length > 0 &&
                    word.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                    wrong.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var item = new ListViewItem(word);
                item.SubItems.Add(wrong);
                item.SubItems[1].ForeColor = SystemColors.GrayText;
                item.UseItemStyleForSubItems = false;
                item.Selected = selected.Contains(word);
                view.Items.Add(item);
            }

            view.ListViewItemSorter = new WordComparer(list.SortColumn, list.SortDescending);
            view.Sort();
        }
        finally
        {
            view.EndUpdate();
        }

        list.Shown = view.Items.Count;
    }

    private static void ToggleSort(ListView view, int column)
    {
        if (view.Tag is not WordList list)
        {
            return;
        }

        list.SortDescending = list.SortColumn == column && !list.SortDescending;
        list.SortColumn = column;
        view.ListViewItemSorter = new WordComparer(column, list.SortDescending);
        view.Sort();
    }

    private void UpdateButtons()
    {
        WordList list = CurrentList;
        int total = _dictionary.Count(list.Language);
        _removeButton.Enabled = list.View.SelectedItems.Count > 0;
        _clearButton.Enabled = total > 0;
        _countLabel.Text = list.Shown == total
            ? Localizer.Format("UserDictionaryCount", total)
            : Localizer.Format("UserDictionaryCountFiltered", list.Shown, total);
    }

    private static ConvertedLanguage Other(ConvertedLanguage language) =>
        language == ConvertedLanguage.Russian ? ConvertedLanguage.English : ConvertedLanguage.Russian;

    private static string LanguageName(ConvertedLanguage language) =>
        Localizer.Get(language == ConvertedLanguage.Russian ? "LanguageRussian" : "LanguageEnglish");

    /// <summary>Russian for Cyrillic-only words, English for Latin-only ones, otherwise Unknown.</summary>
    internal static ConvertedLanguage DetectWordLanguage(string word)
    {
        bool cyrillic = false;
        bool latin = false;
        foreach (char c in word)
        {
            if (!char.IsLetter(c))
            {
                continue;
            }

            if (c is >= 'Ѐ' and <= 'ӿ')
            {
                cyrillic = true;
            }
            else if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'))
            {
                latin = true;
            }
        }

        return cyrillic == latin
            ? ConvertedLanguage.Unknown
            : cyrillic ? ConvertedLanguage.Russian : ConvertedLanguage.English;
    }

    // ── actions ──────────────────────────────────────────────────────────────

    private void AddWord()
    {
        string word = TextNormalizer.TrimToWord(_addBox.Text.Trim());
        if (word.Length == 0 || !ConversionService.ContainsLetter(word))
        {
            if (_addBox.TextLength > 0)
            {
                ShowWarning("UserDictionaryWordWithoutLetters");
            }

            return;
        }

        ConvertedLanguage language = DetectWordLanguage(word);
        if (language == ConvertedLanguage.Unknown)
        {
            language = CurrentList.Language;
        }

        if (!ResolveConflict(word, language))
        {
            return;
        }

        if (!_dictionary.Add(language, word))
        {
            _countLabel.Text = Localizer.Format("UserDictionaryAlreadyExists", word);
        }

        _addBox.Clear();
        _searchBox.Clear();
        RefreshWords();
        SelectWord(language, word);
    }

    // The same keys typed in the other layout give the opposite decision. When
    // the other list holds exactly that spelling, offer to replace it.
    private bool ResolveConflict(string word, ConvertedLanguage language)
    {
        ConvertedLanguage other = Other(language);
        string opposite = _converter.Convert(word, language);
        bool conflict = other == ConvertedLanguage.Russian
            ? _dictionary.ContainsRussian(opposite)
            : _dictionary.ContainsEnglish(opposite);
        if (!conflict)
        {
            return true;
        }

        DialogResult answer = MessageBox.Show(
            this,
            Localizer.Format("UserDictionaryConflict", word, LanguageName(language), opposite, LanguageName(other)),
            Localizer.Get("UserDictionaryWindowTitle"),
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
        {
            return false;
        }

        _dictionary.Remove(other, opposite);
        return true;
    }

    private void SelectWord(ConvertedLanguage language, string word)
    {
        WordList list = language == ConvertedLanguage.Russian ? _russianList : _englishList;
        _tabs.SelectedIndex = language == ConvertedLanguage.Russian ? 0 : 1;
        foreach (ListViewItem item in list.View.Items)
        {
            bool match = string.Equals(item.Text, word, StringComparison.OrdinalIgnoreCase);
            item.Selected = match;
            if (match)
            {
                item.Focused = true;
                item.EnsureVisible();
            }
        }
    }

    private List<string> SelectedWords()
    {
        return CurrentList.View.SelectedItems.Cast<ListViewItem>().Select(item => item.Text).ToList();
    }

    private void RemoveSelected()
    {
        List<string> words = SelectedWords();
        if (words.Count == 0)
        {
            return;
        }

        if (words.Count > 1 &&
            MessageBox.Show(
                this,
                Localizer.Format("UserDictionaryRemoveConfirm", words.Count),
                Localizer.Get("UserDictionaryWindowTitle"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        _dictionary.RemoveRange(CurrentList.Language, words);
        RefreshWords();
    }

    private void MoveSelected()
    {
        ConvertedLanguage from = CurrentList.Language;
        ConvertedLanguage to = Other(from);
        foreach (string word in SelectedWords())
        {
            // Moving keeps the keys: "github" in English becomes "пшерги" in Russian.
            string converted = _converter.Convert(word, from);
            if (!_dictionary.Replace(from, word, to, converted))
            {
                _dictionary.Remove(from, word);
            }
        }

        RefreshWords();
    }

    private void CopySelected()
    {
        List<string> words = SelectedWords();
        if (words.Count == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, words));
        }
        catch (Exception ex)
        {
            Logger.Log($"UserDictionary copy failed: {ex.Message}");
        }
    }

    private void BeginEditSelected()
    {
        ListView view = CurrentList.View;
        if (view.SelectedItems.Count == 1)
        {
            view.SelectedItems[0].BeginEdit();
        }
    }

    private void HandleAfterLabelEdit(object? sender, LabelEditEventArgs args)
    {
        if (sender is not ListView { Tag: WordList list } view || args.Label is null)
        {
            return;
        }

        string oldWord = view.Items[args.Item].Text;
        string newWord = TextNormalizer.TrimToWord(args.Label.Trim());
        args.CancelEdit = true;
        if (newWord.Length == 0 || string.Equals(oldWord, newWord, StringComparison.Ordinal))
        {
            return;
        }

        ConvertedLanguage language = DetectWordLanguage(newWord);
        if (language == ConvertedLanguage.Unknown)
        {
            language = list.Language;
        }

        if (!ResolveConflict(newWord, language))
        {
            return;
        }

        if (!_dictionary.Replace(list.Language, oldWord, language, newWord))
        {
            ShowWarning("UserDictionaryEditDuplicate");
            return;
        }

        BeginInvoke(() =>
        {
            RefreshWords();
            SelectWord(language, newWord);
        });
    }

    private void ClearCurrentList()
    {
        ConvertedLanguage language = CurrentList.Language;
        if (_dictionary.Count(language) == 0)
        {
            return;
        }

        if (MessageBox.Show(
                this,
                Localizer.Format("UserDictionaryClearConfirm", LanguageName(language), _dictionary.Count(language)),
                Localizer.Get("UserDictionaryWindowTitle"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        _dictionary.Clear(language);
        RefreshWords();
    }

    private void ShowWarning(string key)
    {
        MessageBox.Show(this, Localizer.Get(key), Localizer.Get("UserDictionaryWindowTitle"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void Export()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = Localizer.Get("UserDictionaryExportFilter"),
            FileName = "LayoutFixer-user-dictionary.json",
            DefaultExt = "json"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            bool plainText = dialog.FilterIndex == 2 ||
                             string.Equals(Path.GetExtension(dialog.FileName), ".txt", StringComparison.OrdinalIgnoreCase);
            string content = plainText
                ? string.Join(Environment.NewLine,
                    _dictionary.GetWords(ConvertedLanguage.Russian).Concat(_dictionary.GetWords(ConvertedLanguage.English))) + Environment.NewLine
                : _dictionary.ExportToJson();
            File.WriteAllText(dialog.FileName, content, new UTF8Encoding(false));
            int total = _dictionary.Count(ConvertedLanguage.Russian) + _dictionary.Count(ConvertedLanguage.English);
            _countLabel.Text = Localizer.Format("UserDictionaryExportDone", total);
        }
        catch (Exception ex)
        {
            Logger.Log($"UserDictionary export failed: {ex}");
            DialogService.ShowError(this, "UserDictionaryExportFailed");
        }
    }

    private void Import()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = Localizer.Get("UserDictionaryImportFilter"),
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            string content = File.ReadAllText(dialog.FileName);
            int added;
            string trimmed = content.TrimStart('﻿', ' ', '\t', '\r', '\n');
            if (trimmed.StartsWith('{'))
            {
                (int ru, int en) = _dictionary.ImportFromJson(content);
                added = ru + en;
            }
            else
            {
                added = ImportPlainText(content);
            }

            RefreshWords();
            MessageBox.Show(
                this,
                Localizer.Format("UserDictionaryImportDone", added),
                Localizer.Get("UserDictionaryWindowTitle"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Logger.Log($"UserDictionary import failed: {ex}");
            DialogService.ShowError(this, "UserDictionaryImportFailed");
        }
    }

    // One word per line (or separated by spaces/commas); the list is chosen by
    // the alphabet of each word, mixed-alphabet words are skipped.
    private int ImportPlainText(string content)
    {
        var russian = new List<string>();
        var english = new List<string>();
        foreach (string token in content.Split(new[] { '\r', '\n', '\t', ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            switch (DetectWordLanguage(token))
            {
                case ConvertedLanguage.Russian:
                    russian.Add(token);
                    break;
                case ConvertedLanguage.English:
                    english.Add(token);
                    break;
            }
        }

        return _dictionary.Import(ConvertedLanguage.Russian, russian) +
               _dictionary.Import(ConvertedLanguage.English, english);
    }

    private sealed class WordList
    {
        public WordList(ConvertedLanguage language)
        {
            Language = language;
            View = new ListView { Tag = this };
        }

        public ConvertedLanguage Language { get; }

        public ListView View { get; }

        public int Shown { get; set; }

        public int SortColumn { get; set; }

        public bool SortDescending { get; set; }
    }

    private sealed class WordComparer(int column, bool descending) : System.Collections.IComparer
    {
        public int Compare(object? x, object? y)
        {
            string a = x is ListViewItem left && left.SubItems.Count > column ? left.SubItems[column].Text : string.Empty;
            string b = y is ListViewItem right && right.SubItems.Count > column ? right.SubItems[column].Text : string.Empty;
            int result = string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
            return descending ? -result : result;
        }
    }
}
