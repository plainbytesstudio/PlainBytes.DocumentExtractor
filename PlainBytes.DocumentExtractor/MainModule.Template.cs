// SPDX-FileCopyrightText: 2026 PlainBytes Studio
// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using PlainBytes.Core.Controls;
using PlainBytes.DocumentExtractor.Controls;
using PlainBytes.DocumentExtractor.Resources;
using UglyToad.PdfPig;

namespace PlainBytes.DocumentExtractor;

public partial class MainModule
{
    /// <summary>Templates page mutated the store; refresh the Extract template combo.</summary>
    private void OnTemplateStoreChanged()
    {
        if (_suppressStoreRefresh)
            return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(OnTemplateStoreChanged);
            return;
        }
        RefreshTemplateComboFromStore(preserveSelection: true);
    }

    private void GetSetupHelp_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (Window.GetWindow(this) is not ShellWindow shell)
            return;

        var overlay = new TemplateSetupHelpOverlay();
        overlay.Closed += () => shell.HideOverlay();
        shell.ShowOverlay(overlay);
    }

    /// <summary>Initial template combo fill (Custom + persisted templates).</summary>
    private void InitTemplateOptions()
    {
        RefreshTemplateComboFromStore(preserveSelection: false);
    }

    /// <summary>Rebuilds TemplateCombo from disk; optionally keeps the current selection.</summary>
    private void RefreshTemplateComboFromStore(bool preserveSelection)
    {
        var previousId = (TemplateCombo.SelectedItem as TemplateOption)?.TemplateId;
        var previousKind = (TemplateCombo.SelectedItem as TemplateOption)?.Kind ?? TemplateKind.Custom;
        _suppressTemplateChange = true;
        TemplateCombo.Items.Clear();
        TemplateCombo.Items.Add(new TemplateOption(Strings.Extraction_CustomConfiguration, TemplateKind.Custom));
        foreach (var doc in TemplateStore.GetAll())
            TemplateCombo.Items.Add(new TemplateOption(doc.Name, TemplateKind.Existing, doc.Id));
        TemplateOption? select = null;
        if (preserveSelection && previousKind == TemplateKind.Existing && previousId is not null)
        {
            select = TemplateCombo.Items
                .OfType<TemplateOption>()
                .FirstOrDefault(o => o.Kind == TemplateKind.Existing
                    && string.Equals(o.TemplateId, previousId, StringComparison.OrdinalIgnoreCase));
        }
        if (!preserveSelection)
        {
            TemplateCombo.SelectedItem = TemplateCombo.Items[0];
            _suppressTemplateChange = false;
            _templateDirty = false;
            ClearAnchorsAndFields();
            UpdateTemplateModifiedUi();
            UpdateExtractionActions();
            SyncTemplateComboText();
            return;
        }
        if (previousKind == TemplateKind.Custom)
        {
            TemplateCombo.SelectedItem = TemplateCombo.Items[0];
            _suppressTemplateChange = false;
            // Keep in-progress custom anchors/fields.
            UpdateTemplateModifiedUi();
            UpdateExtractionActions();
            SyncTemplateComboText();
            return;
        }
        if (select is not null)
        {
            TemplateCombo.SelectedItem = select;
            _suppressTemplateChange = false;
            if (!_templateDirty)
                ApplyTemplateDocument(select);
            UpdateTemplateModifiedUi();
            UpdateExtractionActions();
            SyncTemplateComboText();
            return;
        }
        // Previously selected template was removed.
        TemplateCombo.SelectedItem = TemplateCombo.Items[0];
        _suppressTemplateChange = false;
        _templateDirty = false;
        ClearAnchorsAndFields();
        UpdateTemplateModifiedUi();
        UpdateExtractionActions();
        SyncTemplateComboText();
    }

    /// <summary>Mirror ListBox selection into the closed-combo caption.</summary>
    private void SyncTemplateComboText()
    {
        TemplateComboText.Text =
            (TemplateCombo.SelectedItem as TemplateOption)?.DisplayName ?? string.Empty;
    }

    private bool IsTemplateComboDropOpen =>
        TemplateComboDrop.Visibility == Visibility.Visible;

    /// <summary>Toggle in-tree template list (no Popup — avoids rematerialize + spurious SelectionChanged).</summary>
    private void TemplateComboHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SetTemplateComboDropOpen(!IsTemplateComboDropOpen);
    }

    private void SetTemplateComboDropOpen(bool open)
    {
        TemplateComboDrop.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
            HookTemplateComboOutsideClose();
        else
            UnhookTemplateComboOutsideClose();
    }

    private void HookTemplateComboOutsideClose()
    {
        if (_templateComboOutsideCloseHooked)
            return;
        var window = Window.GetWindow(this);
        if (window is null)
            return;
        window.PreviewMouseLeftButtonDown += TemplateComboOutside_PreviewMouseLeftButtonDown;
        _templateComboOutsideCloseHooked = true;
    }

    private void UnhookTemplateComboOutsideClose()
    {
        if (!_templateComboOutsideCloseHooked)
            return;
        var window = Window.GetWindow(this);
        if (window is not null)
            window.PreviewMouseLeftButtonDown -= TemplateComboOutside_PreviewMouseLeftButtonDown;
        _templateComboOutsideCloseHooked = false;
    }

    private void TemplateComboOutside_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsTemplateComboDropOpen)
            return;
        if (e.OriginalSource is not DependencyObject source)
            return;
        if (IsDescendantOf(TemplateComboHost, source) || IsDescendantOf(TemplateComboDrop, source))
            return;
        SetTemplateComboDropOpen(false);
    }

    /// <summary>Apply template, or prompt if unsaved changes.</summary>
    private void TemplateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTemplateChange)
        {
            SyncTemplateComboText();
            return;
        }

        var previous = e.RemovedItems.OfType<TemplateOption>().FirstOrDefault();
        var selected = GetSelectedTemplate();
        SyncTemplateComboText();

        // ListBox first attach / rematerialize re-fires selection without a real change.
        // Applying again freezes the UI for 1–2s and leaves the drop blank until done.
        if (selected is not null
            && _appliedTemplateOption is not null
            && IsSameTemplateOption(_appliedTemplateOption, selected)
            && (previous is null || IsSameTemplateOption(previous, selected)))
        {
            Debug.WriteLine(
                $"[TemplateCombo] SelectionChanged ignored (same) at {DateTime.Now:HH:mm:ss.fff}");
            return;
        }

        SetTemplateComboDropOpen(false);

        var effectivePrevious = previous ?? _appliedTemplateOption;
        if (_templateDirty
            && effectivePrevious is not null
            && selected is not null
            && !IsSameTemplateOption(effectivePrevious, selected))
        {
            // Revert combo immediately; apply only after Save/Don't save succeeds.
            _suppressTemplateChange = true;
            TemplateCombo.SelectedItem = FindTemplateComboItem(effectivePrevious) ?? effectivePrevious;
            _suppressTemplateChange = false;
            SyncTemplateComboText();
            _ = ConfirmThenSwitchTemplateAsync(selected);
            return;
        }
        ApplyTemplateSelection(selected);
    }

    /// <summary>After leave confirm, select and apply the target template.</summary>
    private async Task ConfirmThenSwitchTemplateAsync(TemplateOption target)
    {
        if (!await TryConfirmLeaveAsync().ConfigureAwait(true))
            return;

        _suppressTemplateChange = true;
        TemplateCombo.SelectedItem = FindTemplateComboItem(target) ?? target;
        _suppressTemplateChange = false;
        ApplyTemplateSelection(GetSelectedTemplate());
    }

    /// <summary>Same Custom or same Existing template id.</summary>
    private static bool IsSameTemplateOption(TemplateOption a, TemplateOption b)
    {
        if (a.Kind != b.Kind)
            return false;
        if (a.Kind == TemplateKind.Custom)
            return true;

        return string.Equals(a.TemplateId, b.TemplateId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Finds the combo item matching a TemplateOption.</summary>
    private TemplateOption? FindTemplateComboItem(TemplateOption option)
    {
        if (option.Kind == TemplateKind.Custom)
            return TemplateCombo.Items.OfType<TemplateOption>().FirstOrDefault(o => o.Kind == TemplateKind.Custom);

        return TemplateCombo.Items
            .OfType<TemplateOption>()
            .FirstOrDefault(o => o.Kind == TemplateKind.Existing
                && string.Equals(o.TemplateId, option.TemplateId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Clears dirty and loads Custom or Existing template into UI.</summary>
    private void ApplyTemplateSelection(TemplateOption? option)
    {
        var sw = Stopwatch.StartNew();
        _templateDirty = false;

        if (option is not null)
            ApplyTemplateDocument(option);
        else
            ClearAnchorsAndFields();

        _appliedTemplateOption = option;
        UpdateTemplateModifiedUi();
        UpdateExtractionActions();
        sw.Stop();
        Debug.WriteLine(
            $"[TemplateCombo] ApplyTemplateSelection took {sw.ElapsedMilliseconds}ms at {DateTime.Now:HH:mm:ss.fff}");
    }

    /// <summary>True when extraction rules changed and are not written to a template yet.</summary>
    /// <summary>True when extraction rules changed and are not written to a template yet.</summary>
    internal bool HasUnsavedChanges => _templateDirty;

    /// <summary>
    /// Prompts to save/discard when dirty. Returns true if the caller may proceed with leave/switch.
    /// </summary>
    internal async Task<bool> TryConfirmLeaveAsync()
    {
        if (!_templateDirty)
            return true;
        if (_leavePromptOpen || _anchorKindPromptOpen)
            return false;

        _leavePromptOpen = true;
        try
        {
            CloseSaveTemplateFlyout();

            var choice = await PromptUnsavedChoiceAsync().ConfigureAwait(true);
            return choice switch
            {
                UnsavedChangesChoice.Discard => DiscardUnsavedChangesAndContinue(),
                UnsavedChangesChoice.Save => await SaveForLeaveAsync().ConfigureAwait(true),
                _ => false,
            };
        }
        finally
        {
            _leavePromptOpen = false;
        }
    }

    /// <summary>Modal: Save / Don't save / Cancel.</summary>
    private async Task<UnsavedChangesChoice> PromptUnsavedChoiceAsync()
    {
        var shell = Window.GetWindow(this) as ShellWindow;
        if (shell is null)
            return UnsavedChangesChoice.Cancel;

        var tcs = new TaskCompletionSource<UnsavedChangesChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlay = new UnsavedChangesOverlay();
        void Finish(UnsavedChangesChoice choice)
        {
            overlay.ChoiceMade -= OnChoice;
            shell.HideOverlay();
            tcs.TrySetResult(choice);
        }

        void OnChoice(UnsavedChangesChoice choice) => Finish(choice);

        overlay.ChoiceMade += OnChoice;
        shell.ShowOverlay(overlay);
        return await tcs.Task.ConfigureAwait(true);
    }

    /// <summary>Reload saved Existing or clear Custom; clear dirty.</summary>
    private bool DiscardUnsavedChangesAndContinue()
    {
        var option = GetSelectedTemplate();
        _templateDirty = false;
        _suppressTemplateChange = true;

        if (option is { Kind: TemplateKind.Existing })
            ApplyTemplateDocument(option);
        else
            ClearAnchorsAndFields();

        _suppressTemplateChange = false;

        UpdateTemplateModifiedUi();
        UpdateExtractionActions();
        return true;
    }

    /// <summary>Save Existing in place, or Save-as for Custom.</summary>
    private async Task<bool> SaveForLeaveAsync()
    {
        var option = GetSelectedTemplate();
        if (option is null)
            return false;
        if (option.Kind == TemplateKind.Existing)
        {
            SaveExistingTemplateChanges();
            return !_templateDirty;
        }
        return await PromptSaveAsAndWaitAsync().ConfigureAwait(true);
    }

    /// <summary>Opens Save-as flyout and waits for commit/cancel.</summary>
    private async Task<bool> PromptSaveAsAndWaitAsync()
    {
        if (_saveAsCompletion is not null)
            return false;

        _saveAsCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        OpenSaveTemplateFlyout();
        return await _saveAsCompletion.Task.ConfigureAwait(true);
    }

    /// <summary>Hydrate anchors/fields from a stored template, then refresh preview.</summary>
    private void ApplyTemplateDocument(TemplateOption option)
    {
        _anchors.Clear();
        if (option.Kind == TemplateKind.Custom || string.IsNullOrWhiteSpace(option.TemplateId))
        {
            UpdateExtractionHeaders();
            RefreshPageOverlays();
            _appliedTemplateOption = option;
            return;
        }

        var doc = TemplateStore.Get(option.TemplateId);
        if (doc is null)
        {
            UpdateExtractionHeaders();
            RefreshPageOverlays();
            _appliedTemplateOption = option;
            return;
        }

        var isTopLeft = string.Equals(
            doc.CoordinateOrigin,
            TemplateRegion.OriginTopLeft,
            StringComparison.OrdinalIgnoreCase);

        PdfDocument? sampleDoc = null;
        try
        {
            // Legacy templates store Y from page bottom; need page height to convert.
            if (!isTopLeft && _selected is not null && File.Exists(_selected.FilePath))
                sampleDoc = PdfDocument.Open(_selected.FilePath);
            var anchorsById = new Dictionary<string, AnchorItem>(StringComparer.Ordinal);
            var ai = 1;
            foreach (var region in doc.Anchors)
            {
                var text = region.ResolveAnchorText();
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                var id = string.IsNullOrWhiteSpace(region.Id) ? NewAnchorId() : region.Id!;
                var area = ResolveStoredRegion(region, isTopLeft, sampleDoc);
                var anchor = new AnchorItem(
                    id: id,
                    index: ai++,
                    text: text,
                    pageIndex: region.PageIndex,
                    pdfArea: area)
                {
                    Kind = ParseStoredAnchorKind(region.Kind),
                    PhrasePdfArea = region.HasPhraseBounds
                        ? ResolveStoredPhraseRegion(region, isTopLeft, sampleDoc)
                        : area,
                };

                _anchors.Add(anchor);
                anchorsById[id] = anchor;
            }
            foreach (var region in doc.Fields)
            {
                if (string.IsNullOrWhiteSpace(region.AnchorId)
                    || !anchorsById.TryGetValue(region.AnchorId, out var owner))
                    continue;
                var field = new FieldItem(
                    index: owner.Fields.Count + 1,
                    name: region.Name,
                    value: string.Empty,
                    pageIndex: region.PageIndex,
                    pdfArea: ResolveStoredRegion(region, isTopLeft, sampleDoc),
                    anchorId: owner.Id)
                {
                    IsRulerColumn = region.IsRulerColumn,
                };

                owner.Fields.Add(field);
            }
            RenumberFields();
            foreach (var anchor in _anchors)
            {
                if (anchor.Kind != AnchorKind.Table || anchor.Fields.Count == 0)
                    continue;
                // Outer header = saved PdfArea (user rubber-band). Fall back to column union for legacy.
                if (anchor.PdfArea.Width > 0.5 && anchor.PdfArea.Height > 0.5)
                    SetTableOuterBox(anchor, anchor.PdfArea);
                else
                    SetTableOuterBox(
                        anchor,
                        TableHeaderColumnService.UnionHeader(
                            anchor.Fields.Select(f => f.PdfArea).ToList()));
            }
            if (_anchors.Count > 0)
                ExpandAnchor(_anchors[0]);
        }
        finally
        {
            sampleDoc?.Dispose();
        }

        UpdateExtractionHeaders();
        RefreshPageOverlays();
        RefreshAllFieldExtractedValues();
        ScanAnchorsForAmbiguity();
        _appliedTemplateOption = option;
    }

    /// <summary>
    /// Loads a template region into memory. Legacy bottom-left files are flipped using the
    /// sample page height when available so overlays stay aligned under the new top-left store.
    /// </summary>
    private Rect ResolveStoredRegion(TemplateRegion region, bool isTopLeft, PdfDocument? sampleDoc)
    {
        var rect = region.ToRect();
        if (isTopLeft)
            return rect;

        var pageHeight = _pageHeightPt;
        if (sampleDoc is not null
            && region.PageIndex >= 0
            && region.PageIndex < sampleDoc.NumberOfPages)
        {
            pageHeight = sampleDoc.GetPage(region.PageIndex + 1).Height;
        }
        return PdfTextGeometry.FromPdfUserSpace(rect, pageHeight);
    }

    /// <summary>Same coordinate migration as <see cref="ResolveStoredRegion"/> for phrase bounds.</summary>
    private Rect ResolveStoredPhraseRegion(TemplateRegion region, bool isTopLeft, PdfDocument? sampleDoc)
    {
        var rect = region.ToPhraseRect();
        if (isTopLeft)
            return rect;

        var pageHeight = _pageHeightPt;
        if (sampleDoc is not null
            && region.PageIndex >= 0
            && region.PageIndex < sampleDoc.NumberOfPages)
        {
            pageHeight = sampleDoc.GetPage(region.PageIndex + 1).Height;
        }
        return PdfTextGeometry.FromPdfUserSpace(rect, pageHeight);
    }

    /// <summary>Empty extraction rules and overlays.</summary>
    private void ClearAnchorsAndFields()
    {
        _anchors.Clear();
        UpdateExtractionHeaders();
        RefreshPageOverlays();
    }

    /// <summary>Serialize current rules to an ExtractionTemplate.</summary>
    private ExtractionTemplate BuildTemplateDocument(string id, string name) => new()
    {
        Id = id,
        Name = name,
        CoordinateOrigin = TemplateRegion.OriginTopLeft,
        Anchors = _anchors
            .Select(a => TemplateRegion.FromAnchor(
                a.Id,
                a.Text,
                a.PageIndex,
                a.PdfArea,
                a.Kind,
                a.PhrasePdfArea.Width > 0.5 ? a.PhrasePdfArea : null))
            .ToList(),
        Fields = EnumerateFields()
            .Select(f => TemplateRegion.FromField(
                f.Name,
                f.AnchorId,
                f.PageIndex,
                f.PdfArea,
                f.IsRulerColumn))
            .ToList(),
    };

    /// <summary>JSON kind string → AnchorKind.</summary>
    private static AnchorKind ParseStoredAnchorKind(string? kind) =>
        string.Equals(kind, "table", StringComparison.OrdinalIgnoreCase)
            ? AnchorKind.Table
            : AnchorKind.Value;

    /// <summary>Save changes (Existing dirty) or open Save-as.</summary>
    private void SecondaryActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedTemplate() is not { } option)
            return;

        // Existing dirty: primary click saves immediately (no confirm / no name).
        if (option.Kind == TemplateKind.Existing && _templateDirty)
        {
            SaveExistingTemplateChanges();
            return;
        }
        OpenSaveTemplateFlyout();
    }

    /// <summary>Split-button menu: Save as new template.</summary>
    private void SecondaryActionMenuButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        var menu = new ContextMenu();
        var saveAsNew = new MenuItem { Header = Strings.Extraction_SaveAsNewTemplate };

        saveAsNew.Click += (_, _) => OpenSaveTemplateFlyout();
        menu.Items.Add(saveAsNew);
        menu.PlacementTarget = SecondaryActionHost;
        menu.Placement = PlacementMode.Top;
        SecondaryActionMenuButton.ContextMenu = menu;
        menu.IsOpen = true;
    }

    /// <summary>Show the Save-as name flyout (in-tree; not Popup — IME-safe).</summary>
    private void OpenSaveTemplateFlyout()
    {
        if (SaveTemplateFlyout.Visibility == Visibility.Visible)
            return;

        SaveTemplateNameBox.Text = SuggestTemplateName();
        ValidateSaveTemplateName();
        SaveTemplateFlyout.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() =>
        {
            if (SaveTemplateFlyout.Visibility != Visibility.Visible)
                return;
            SaveTemplateNameBox.Focus();
            SaveTemplateNameBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    /// <summary>Hide the Save-as flyout.</summary>
    private void CloseSaveTemplateFlyout()
    {
        if (SaveTemplateFlyout.Visibility != Visibility.Visible)
            return;

        SaveTemplateFlyout.Visibility = Visibility.Collapsed;
        SaveTemplateNameError.Visibility = Visibility.Collapsed;
        // Leave-flow save-as cancelled or dismissed without commit.
        CompleteSaveAsWait(success: false);
    }

    /// <summary>Click dim backdrop dismisses (same as former Popup StaysOpen=False).</summary>
    private void SaveTemplateFlyoutBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        CloseSaveTemplateFlyout();
        e.Handled = true;
    }

    /// <summary>Validate name on each keystroke.</summary>
    private void SaveTemplateNameBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ValidateSaveTemplateName();

    /// <summary>Enter confirms Save-as; Esc closes. Ignore IME composition keys.</summary>
    private void SaveTemplateNameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.ImeProcessed)
            return;

        if (e.Key == Key.Enter)
        {
            if (SaveTemplateConfirmButton.IsEnabled)
                CommitSaveAsTemplate();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseSaveTemplateFlyout();
            e.Handled = true;
        }
    }

    /// <summary>Esc closes the Save-as flyout.</summary>
    private void SaveTemplateFlyoutRoot_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseSaveTemplateFlyout();
            e.Handled = true;
        }
    }

    /// <summary>Confirm Save-as.</summary>
    private void SaveTemplateConfirmButton_Click(object sender, RoutedEventArgs e) =>
        CommitSaveAsTemplate();

    /// <summary>Enable confirm when name is non-empty and unique.</summary>
    private void ValidateSaveTemplateName()
    {
        var name = SaveTemplateNameBox.Text.Trim();
        var empty = string.IsNullOrWhiteSpace(name);
        var conflict = !empty && TemplateNameExists(name);

        SaveTemplateNameError.Visibility = conflict ? Visibility.Visible : Visibility.Collapsed;
        SaveTemplateConfirmButton.IsEnabled = !empty && !conflict;
    }

    /// <summary>True if another template already uses this name.</summary>
    private bool TemplateNameExists(string name) =>
        TemplateStore.NameExists(name);

    /// <summary>Default Save-as name from sample file or untitled.</summary>
    private string SuggestTemplateName()
    {
        if (_selected is not null)
        {
            var fromFile = System.IO.Path.GetFileNameWithoutExtension(_selected.FileName);
            if (!string.IsNullOrWhiteSpace(fromFile))
                return fromFile;
        }
        return Strings.Extraction_UntitledTemplate;
    }

    /// <summary>Persist as a new template and select it in the combo.</summary>
    private void CommitSaveAsTemplate()
    {
        ValidateSaveTemplateName();
        if (!SaveTemplateConfirmButton.IsEnabled)
            return;

        var name = SaveTemplateNameBox.Text.Trim();
        var id = "tpl-" + Guid.NewGuid().ToString("N")[..12];

        _suppressStoreRefresh = true;

        var saved = TemplateStore.Save(BuildTemplateDocument(id, name));
        _suppressStoreRefresh = false;
        _suppressTemplateChange = true;

        RefreshTemplateComboFromStore(preserveSelection: false);

        var match = TemplateCombo.Items
            .OfType<TemplateOption>()
            .FirstOrDefault(o => string.Equals(o.TemplateId, saved.Id, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            TemplateCombo.SelectedItem = match;

        _suppressTemplateChange = false;
        _templateDirty = false;

        if (match is not null)
            ApplyTemplateDocument(match);

        UpdateTemplateModifiedUi();
        UpdateExtractionActions();
        CompleteSaveAsWait(success: true);
        CloseSaveTemplateFlyout();
    }

    /// <summary>Completes leave-flow Save-as TaskCompletionSource.</summary>
    private void CompleteSaveAsWait(bool success)
    {
        if (_saveAsCompletion is null)
            return;

        var tcs = _saveAsCompletion;

        _saveAsCompletion = null;
        tcs.TrySetResult(success);
    }

    /// <summary>Overwrite the selected Existing template on disk.</summary>
    private void SaveExistingTemplateChanges()
    {
        if (GetSelectedTemplate() is not { Kind: TemplateKind.Existing, TemplateId: { } id } option)
            return;

        _suppressStoreRefresh = true;

        TemplateStore.Save(BuildTemplateDocument(id, option.DisplayName));

        _suppressStoreRefresh = false;
        _templateDirty = false;

        UpdateTemplateModifiedUi();
        UpdateExtractionActions();
    }

    /// <summary>Flag unsaved rule changes (Existing and Custom).</summary>
    private void MarkTemplateDirty()
    {
        _templateDirty = true;
        UpdateTemplateModifiedUi();
        UpdateExtractionActions();
    }

    /// <summary>Show/hide the template-modified hint (Hidden keeps layout space).</summary>
    private void UpdateTemplateModifiedUi()
    {
        TemplateModifiedPanel.Visibility = _templateDirty ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>Currently selected TemplateOption, if any.</summary>
    private TemplateOption? GetSelectedTemplate() =>
        TemplateCombo.SelectedItem as TemplateOption;
}
