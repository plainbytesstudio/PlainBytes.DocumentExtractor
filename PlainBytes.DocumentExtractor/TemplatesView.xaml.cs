// SPDX-FileCopyrightText: 2026 PlainBytes Studio
// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PlainBytes.Core.Controls;
using PlainBytes.DocumentExtractor.Resources;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace PlainBytes.DocumentExtractor;

/// <summary>Templates management — list, search, import/export against local TemplateStore.</summary>
public partial class TemplatesView
{
    private readonly ObservableCollection<TemplateListItem> _templates = [];
    private readonly ICollectionView _view;
    private readonly DispatcherTimer _statusTimer;
    private bool _suppressHeaderCheck;
    private bool _suppressStoreReload;
    private bool _suppressRenameValidate;
    private string? _renameOriginal;

    public TemplatesView()
    {
        InitializeComponent();

        _view = CollectionViewSource.GetDefaultView(_templates);
        _view.Filter = FilterTemplate;
        TemplateListItems.ItemsSource = _view;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            HideStatus();
        };

        Loaded += (_, _) => ReloadFromStore();
        TemplateStore.Changed += OnTemplateStoreChanged;
        ReloadFromStore();
    }

    private void OnTemplateStoreChanged()
    {
        if (_suppressStoreReload)
            return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(OnTemplateStoreChanged);
            return;
        }

        ReloadFromStore();
    }

    private void ReloadFromStore()
    {
        var selectedIds = _templates.Where(t => t.IsSelected).Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in _templates)
            item.PropertyChanged -= TemplateItem_PropertyChanged;

        _templates.Clear();
        foreach (var doc in TemplateStore.GetAll())
        {
            var item = new TemplateListItem(
                doc.Id,
                doc.Name,
                fieldCount: doc.Fields.Count,
                anchorCount: doc.Anchors.Count,
                lastModified: doc.LastModified.ToLocalTime());
            item.IsSelected = selectedIds.Contains(doc.Id);
            item.PropertyChanged += TemplateItem_PropertyChanged;
            _templates.Add(item);
        }

        _view.Refresh();
        RefreshSelectionUi();
        RefreshEmptyState();
    }

    private bool FilterTemplate(object obj)
    {
        if (obj is not TemplateListItem item)
            return false;

        var q = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(q))
            return true;

        return item.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        _view.Refresh();
        RefreshSelectionUi();
        RefreshEmptyState();
    }

    private void TemplateItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TemplateListItem.IsSelected))
            RefreshSelectionUi();
    }

    private IEnumerable<TemplateListItem> VisibleTemplates()
    {
        foreach (var obj in _view)
        {
            if (obj is TemplateListItem item)
                yield return item;
        }
    }

    private void RefreshSelectionUi()
    {
        var visible = VisibleTemplates().ToList();
        var selectedCount = visible.Count(t => t.IsSelected);
        var anySelected = _templates.Any(t => t.IsSelected);

        _suppressHeaderCheck = true;
        if (visible.Count == 0 || selectedCount == 0)
            HeaderCheckBox.IsChecked = false;
        else if (selectedCount == visible.Count)
            HeaderCheckBox.IsChecked = true;
        else
            HeaderCheckBox.IsChecked = null;
        _suppressHeaderCheck = false;

        ExportButton.IsEnabled = anySelected;
        DeleteSelectedButton.IsEnabled = anySelected;
    }

    private void RefreshEmptyState()
    {
        if (!_view.IsEmpty)
        {
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyStatePanel.Visibility = Visibility.Visible;
        if (_templates.Count == 0)
        {
            EmptyStateTitle.Text = Strings.Templates_EmptyTitle;
            EmptyStateDetail.Text = Strings.Templates_EmptyDetail;
        }
        else
        {
            EmptyStateTitle.Text = Strings.Templates_NoMatchTitle;
            EmptyStateDetail.Text = Strings.Templates_NoMatchDetail;
        }
    }

    private void HeaderCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressHeaderCheck)
            return;

        var check = HeaderCheckBox.IsChecked == true;
        foreach (var item in VisibleTemplates())
            item.IsSelected = check;

        RefreshSelectionUi();
    }

    private void RowCheckBox_Click(object sender, RoutedEventArgs e) =>
        RefreshSelectionUi();

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Strings.Templates_ImportDialog,
            Filter = $"{Strings.Dialog_FilterTemplates}|{Strings.Dialog_AllFiles}|*.*",
            Multiselect = true,
        };

        if (dialog.ShowDialog() != true)
            return;

        var ok = 0;
        var failFiles = 0;
        _suppressStoreReload = true;
        foreach (var path in dialog.FileNames)
        {
            var n = TemplateStore.ImportFromFile(path);
            if (n > 0)
                ok += n;
            else
                failFiles++;
        }

        _suppressStoreReload = false;
        ReloadFromStore();
        TemplateStore.NotifyChanged();

        if (ok > 0 && failFiles == 0)
            ShowStatus(ok == 1 ? Strings.Templates_ImportedOne : Strings.Format(Strings.Templates_ImportedMany, ok), StatusKind.Success);
        else if (ok > 0)
            ShowStatus(Strings.Format(Strings.Templates_ImportedPartial, ok, failFiles), StatusKind.Warning);
        else
            ShowStatus(Strings.Templates_ImportFailed, StatusKind.Error);
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e) =>
        ExportSelectedTemplates();

    private void DeleteSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _templates.Where(t => t.IsSelected).ToList();
        if (selected.Count == 0)
            return;

        if (!ConfirmDelete(selected))
            return;

        DeleteTemplates(selected);
    }

    private void ExportSelectedTemplates()
    {
        var selected = _templates.Where(t => t.IsSelected).ToList();
        if (selected.Count == 0)
            return;

        var docs = new List<ExtractionTemplate>();
        foreach (var item in selected)
        {
            var doc = TemplateStore.Get(item.Id);
            if (doc is not null)
                docs.Add(doc);
        }

        if (docs.Count == 0)
        {
            ShowStatus(Strings.Templates_ExportMissing, StatusKind.Error);
            return;
        }

        var defaultName = docs.Count == 1
            ? SanitizeFileName(docs[0].Name) + ".json"
            : "templates-export.json";

        var dialog = new SaveFileDialog
        {
            Title = docs.Count == 1 ? Strings.Templates_ExportDialogOne : Strings.Templates_ExportDialogMany,
            Filter = Strings.Dialog_FilterTemplates,
            FileName = defaultName,
        };
        if (dialog.ShowDialog() != true)
            return;

        TemplateStore.ExportToFile(docs, dialog.FileName);
        ShowStatus(
            docs.Count == 1
                ? Strings.Format(Strings.Templates_ExportedOne, docs[0].Name)
                : Strings.Format(Strings.Templates_ExportedMany, docs.Count),
            StatusKind.Success);
    }

    private void RowMore_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: TemplateListItem item } fe)
            return;

        var menu = new ContextMenu();

        var rename = new MenuItem { Header = Strings.Templates_Rename };
        rename.Click += (_, _) => BeginRename(item);
        menu.Items.Add(rename);

        var export = new MenuItem { Header = Strings.Templates_Export };
        export.Click += (_, _) =>
        {
            foreach (var t in _templates)
                t.IsSelected = ReferenceEquals(t, item);
            RefreshSelectionUi();
            ExportSelectedTemplates();
        };
        menu.Items.Add(export);

        menu.Items.Add(new Separator());

        var delete = new MenuItem { Header = Strings.Templates_Delete };
        delete.Click += (_, _) =>
        {
            if (!ConfirmDelete([item]))
                return;
            DeleteTemplates([item]);
        };
        menu.Items.Add(delete);

        menu.PlacementTarget = fe;
        menu.Placement = PlacementMode.Bottom;
        fe.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private static bool ConfirmDelete(IReadOnlyList<TemplateListItem> items)
    {
        if (items.Count == 1)
        {
            return MessageBox.Show(
                Strings.Format(Strings.Templates_DeleteConfirmOne, items[0].Name),
                Strings.Templates_DeleteConfirmTitleOne,
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) == MessageBoxResult.OK;
        }

        return MessageBox.Show(
            Strings.Format(Strings.Templates_DeleteConfirmMany, items.Count),
            Strings.Templates_DeleteConfirmTitleMany,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;
    }

    private void DeleteTemplates(IReadOnlyList<TemplateListItem> items)
    {
        if (items.Count == 0)
            return;

        _suppressStoreReload = true;
        foreach (var item in items)
        {
            item.PropertyChanged -= TemplateItem_PropertyChanged;
            item.IsEditing = false;
            TemplateStore.Delete(item.Id);
        }

        _suppressStoreReload = false;
        ReloadFromStore();
        TemplateStore.NotifyChanged();
    }

    private void BeginRename(TemplateListItem item)
    {
        foreach (var t in _templates)
            t.IsEditing = false;

        _renameOriginal = item.Name;
        item.EditError = null;
        item.IsEditing = true;
    }

    private void RenameBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && box.DataContext is TemplateListItem { IsEditing: true })
        {
            _suppressRenameValidate = true;
            box.Focus();
            box.SelectAll();
            _suppressRenameValidate = false;
        }
    }

    private void RenameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressRenameValidate)
            return;
        if (sender is not TextBox { DataContext: TemplateListItem item } box || !item.IsEditing)
            return;

        ValidateRename(item, box.Text);
    }

    private void RenameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: TemplateListItem item } box)
            return;

        if (e.Key == Key.Enter)
        {
            if (!ValidateRename(item, box.Text))
            {
                e.Handled = true;
                return;
            }

            CommitRename(item, box.Text, cancel: false);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CommitRename(item, box.Text, cancel: true);
            e.Handled = true;
        }
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: TemplateListItem item } box || !item.IsEditing)
            return;

        if (!ValidateRename(item, box.Text))
        {
            // Keep editing so the user can fix the name.
            Dispatcher.BeginInvoke(() =>
            {
                if (item.IsEditing)
                    box.Focus();
            }, DispatcherPriority.Input);
            return;
        }

        CommitRename(item, box.Text, cancel: false);
    }

    private bool ValidateRename(TemplateListItem item, string text)
    {
        var next = text.Trim();
        if (string.IsNullOrWhiteSpace(next))
        {
            item.EditError = Strings.Templates_NameEmpty;
            return false;
        }

        if (TemplateStore.NameExists(next, item.Id))
        {
            item.EditError = Strings.Templates_NameConflict;
            return false;
        }

        item.EditError = null;
        return true;
    }

    private void CommitRename(TemplateListItem item, string text, bool cancel)
    {
        if (!item.IsEditing)
            return;

        if (cancel)
        {
            item.Name = _renameOriginal ?? item.Name;
            item.IsEditing = false;
            _renameOriginal = null;
            return;
        }

        var next = text.Trim();
        if (!ValidateRename(item, next))
            return;

        if (string.Equals(item.Name, next, StringComparison.Ordinal))
        {
            item.IsEditing = false;
            _renameOriginal = null;
            return;
        }

        var doc = TemplateStore.Get(item.Id);
        if (doc is null)
        {
            ShowStatus(Strings.Templates_RenameMissing, StatusKind.Error);
            item.IsEditing = false;
            _renameOriginal = null;
            return;
        }

        doc.Name = next;
        _suppressStoreReload = true;
        TemplateStore.Save(doc);
        _suppressStoreReload = false;

        item.Name = doc.Name;
        item.LastModified = doc.LastModified.ToLocalTime();
        item.IsEditing = false;
        _renameOriginal = null;
        _view.Refresh();
        RefreshEmptyState();
    }

    private void StatusBannerClose_Click(object sender, RoutedEventArgs e) => HideStatus();

    private enum StatusKind
    {
        Success,
        Warning,
        Error,
    }

    private void ShowStatus(string message, StatusKind kind)
    {
        StatusBannerText.Text = message;
        StatusBanner.Visibility = Visibility.Visible;
        Panel.SetZIndex(StatusBanner, 200);

        switch (kind)
        {
            case StatusKind.Success:
                StatusBanner.Background = new SolidColorBrush(Color.FromRgb(0xEC, 0xFD, 0xF5));
                StatusBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(0xA7, 0xF3, 0xD0));
                StatusBanner.BorderThickness = new Thickness(1);
                StatusBannerIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24;
                StatusBannerIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0x65, 0x34));
                StatusBannerText.Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0x65, 0x34));
                break;
            case StatusKind.Warning:
                StatusBanner.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFB, 0xEB));
                StatusBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFD, 0xE6, 0x8A));
                StatusBanner.BorderThickness = new Thickness(1);
                StatusBannerIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Warning24;
                StatusBannerIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
                StatusBannerText.Foreground = new SolidColorBrush(Color.FromRgb(0x92, 0x40, 0x0E));
                break;
            default:
                StatusBanner.Background = new SolidColorBrush(Color.FromRgb(0xFE, 0xF2, 0xF2));
                StatusBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFE, 0xCA, 0xCA));
                StatusBanner.BorderThickness = new Thickness(1);
                StatusBannerIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.ErrorCircle24;
                StatusBannerIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
                StatusBannerText.Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0x12, 0x39));
                break;
        }

        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void HideStatus()
    {
        _statusTimer.Stop();
        StatusBanner.Visibility = Visibility.Collapsed;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "template" : name;
    }
}
