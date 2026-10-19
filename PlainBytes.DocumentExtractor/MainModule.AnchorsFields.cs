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
    /// <summary>Creates a value field (name edit) or replaces table columns from a header rubber-band.</summary>
    private void AddFieldFromSelection(int pageIndex, Rect pdfRect, string text)
    {
        var owner = GetExpandedAnchor();
        if (owner is null)
        {
            ShowActionToast(Strings.Toast_NeedAnchorBeforeField, ActionToastKind.Warning, nearPreview: _lastSelectionPreview);
            return;
        }

        if (owner.Kind == AnchorKind.Table)
        {
            // Re-box header: keep current column count (or default) and left-pack dividers again.
            var n = owner.Fields.Count >= 2
                ? owner.Fields.Count
                : AnchorKindPickerOverlay.DefaultTableColumnCount;
            ApplyTableHeaderSelection(owner, pageIndex, pdfRect, n);
            return;
        }

        var field = new FieldItem(
            index: owner.Fields.Count + 1,
            name: Strings.Format(Strings.Extraction_FieldNameFormat, owner.Fields.Count + 1),
            value: string.IsNullOrWhiteSpace(text) ? Strings.Field_NoText : text,
            pageIndex: pageIndex,
            pdfArea: pdfRect,
            anchorId: owner.Id);

        owner.Fields.Add(field);
        RenumberFields(owner);
        UpdateExtractionHeaders();
        ExpandAnchor(owner);
        SelectField(field);
        RefreshPageOverlays();

        _dirtyBeforeNewFieldEdit = _templateDirty;
        _fieldNameEditIsNew = true;
        BeginFieldNameEdit(field);
    }

    /// <summary>
    /// Table anchor: user header box + N left-packed dividers (manual drag to calibrate).
    /// Column names follow text inside each band.
    /// </summary>
    private void ApplyTableHeaderSelection(
        AnchorItem owner,
        int pageIndex,
        Rect headerPdf,
        int columnCount)
    {
        if (_selected is null || !File.Exists(_selected.FilePath))
            return;

        try
        {
            using var document = PdfTextGeometry.OpenReadShared(_selected.FilePath);
            var pageNumber = pageIndex + 1;
            if (pageNumber < 1 || pageNumber > document.NumberOfPages)
            {
                ShowActionToast(Strings.Toast_NoTextInArea, ActionToastKind.Warning, nearPreview: _lastSelectionPreview);
                return;
            }

            var displayHeader = headerPdf;
            if (displayHeader.Width < TableHeaderColumnService.MinColumnWidthPt
                || displayHeader.Height < 0.5)
            {
                ShowActionToast(Strings.Toast_NoTextInArea, ActionToastKind.Warning, nearPreview: _lastSelectionPreview);
                return;
            }

            var page = document.GetPage(pageNumber);
            var words = PdfTextGeometry.GetOrderedWordsTopLeft(page);
            var bands = TableHeaderColumnService.CreateLeftPackedColumns(
                displayHeader,
                columnCount,
                (_, area) => TableHeaderColumnService.NameFromWords(words, area));
            if (bands.Count == 0)
            {
                ShowActionToast(Strings.Toast_NoTextInArea, ActionToastKind.Warning, nearPreview: _lastSelectionPreview);
                return;
            }

            ResolveAnchorOffsets(document, locatedOnCurrentPage: null, _overlayOffsetsByAnchorId);
            var offset = GetOverlayOffset(owner.Id);
            // Outer header box (user rubber-band) in template space; PdfArea stays in sync for persistence.
            SetTableOuterBox(owner, RemoveOverlayOffset(displayHeader, offset));

            var previousRulerName = owner.Fields.FirstOrDefault(f => f.IsRulerColumn)?.Name;
            owner.Fields.Clear();
            foreach (var band in bands)
            {
                var templateArea = RemoveOverlayOffset(band.Area, offset);
                var column = new FieldItem(
                    index: owner.Fields.Count + 1,
                    name: band.Name,
                    value: string.Empty,
                    pageIndex: pageIndex,
                    pdfArea: templateArea,
                    anchorId: owner.Id)
                {
                    IsRulerColumn = previousRulerName is not null
                        && string.Equals(previousRulerName, band.Name, StringComparison.Ordinal)
                };
                owner.Fields.Add(column);
            }

            if (owner.Fields.Count(f => f.IsRulerColumn) > 1)
            {
                var keep = owner.Fields.First(f => f.IsRulerColumn);
                foreach (var f in owner.Fields)
                    f.IsRulerColumn = ReferenceEquals(f, keep);
            }

            RenumberFields(owner);
            MarkTemplateDirty();
            UpdateExtractionHeaders();
            ExpandAnchor(owner);
            SelectField(owner.Fields[0]);
            RefreshAllFieldExtractedValues();
            RefreshPageOverlays();
            ShowActionToast(
                Strings.Format(Strings.Toast_TableColumnsCreated, owner.Fields.Count),
                ActionToastKind.Success,
                nearPreview: _lastSelectionPreview);
        }
        catch
        {
            ShowActionToast(Strings.Toast_NoTextInArea, ActionToastKind.Warning, nearPreview: _lastSelectionPreview);
        }
    }

    private void RefreshTableColumnNames(
        AnchorItem table,
        IReadOnlyList<FieldItem> columnsLeftToRight,
        PdfDocument? openDocument)
    {
        if (_selected is null || !File.Exists(_selected.FilePath) || columnsLeftToRight.Count == 0)
            return;

        try
        {
            var offset = GetOverlayOffset(table.Id);
            if (openDocument is not null)
            {
                RefreshTableColumnNamesCore(columnsLeftToRight, openDocument, offset);
                return;
            }

            using var document = PdfTextGeometry.OpenReadShared(_selected.FilePath);
            RefreshTableColumnNamesCore(columnsLeftToRight, document, offset);
        }
        catch
        {
            // Keep previous names if the PDF cannot be read.
        }
    }

    private static void RefreshTableColumnNamesCore(
        IReadOnlyList<FieldItem> columnsLeftToRight,
        PdfDocument document,
        (double Dx, double Dy) offset)
    {
        var pageIndex = columnsLeftToRight[0].PageIndex;
        var pageNumber = pageIndex + 1;
        if (pageNumber < 1 || pageNumber > document.NumberOfPages)
            return;

        var words = PdfTextGeometry.GetOrderedWordsTopLeft(document.GetPage(pageNumber));
        for (var i = 0; i < columnsLeftToRight.Count; i++)
        {
            var displayArea = ApplyOverlayOffset(columnsLeftToRight[i].PdfArea, offset);
            var name = TableHeaderColumnService.NameFromWords(words, displayArea);
            // Always rewrite — empty band must clear the previous title (fallback matches rebuild).
            columnsLeftToRight[i].Name = string.IsNullOrWhiteSpace(name)
                ? Strings.Format(Strings.Extraction_ColumnNameFormat, i + 1)
                : name;
        }
    }

    /// <summary>
    /// Prompts for Value/Table kind, then adds the anchor.
    /// Inner phrase box (text-union) is always stored in <see cref="AnchorItem.PhrasePdfArea"/>.
    /// Value: display box = inner. Table: display/outer = user rubber-band; offset later applies to outer.
    /// </summary>
    private async Task AddAnchorFromSelectionAsync(
        int pageIndex,
        string text,
        Rect? textBounds,
        Rect rubberPdfRect)
    {
        if (string.IsNullOrWhiteSpace(text) || textBounds is not { } phraseBounds)
            return;

        var anchorText = NormalizeAnchorText(text);
        if (string.IsNullOrWhiteSpace(anchorText))
            return;

        var choice = await PromptAnchorKindAsync().ConfigureAwait(true);
        if (choice is null)
            return;

        var kind = choice.Value.Kind;
        var columnCount = choice.Value.ColumnCount;

        // Outer box: Table keeps rubber-band; Value snaps to phrase (inner = outer).
        var outerArea = kind == AnchorKind.Table ? rubberPdfRect : phraseBounds;

        var anchor = new AnchorItem(
            id: NewAnchorId(),
            index: _anchors.Count + 1,
            text: anchorText,
            pageIndex: pageIndex,
            pdfArea: outerArea)
        {
            Kind = kind,
            PhrasePdfArea = phraseBounds,
        };

        _anchors.Add(anchor);
        MarkTemplateDirty();
        UpdateExtractionHeaders();
        ExpandAnchor(anchor);
        SelectAnchor(anchor);

        // Table rubber-band is the header outer box: create N left-packed dividers for manual drag.
        if (kind == AnchorKind.Table)
        {
            ApplyTableHeaderSelection(anchor, pageIndex, rubberPdfRect, columnCount);
            ScanAnchorsForAmbiguity(focus: anchor);
            return;
        }

        RefreshPageOverlays();
        ScanAnchorsForAmbiguity(focus: anchor);
    }

    /// <summary>Modal overlay: choose anchor kind (and table column count) or cancel.</summary>
    private async Task<(AnchorKind Kind, int ColumnCount)?> PromptAnchorKindAsync()
    {
        if (_anchorKindPromptOpen)
            return null;

        var shell = Window.GetWindow(this) as ShellWindow;
        if (shell is null)
            return (AnchorKind.Value, 0);

        _anchorKindPromptOpen = true;
        var tcs = new TaskCompletionSource<(AnchorKind, int)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlay = new AnchorKindPickerOverlay();

        void Finish((AnchorKind Kind, int ColumnCount)? result)
        {
            overlay.KindChosen -= OnChosen;
            overlay.Cancelled -= OnCancelled;
            shell.HideOverlay();
            tcs.TrySetResult(result);
        }

        void OnChosen(AnchorKind kind, int columnCount) => Finish((kind, columnCount));
        void OnCancelled() => Finish(null);

        overlay.KindChosen += OnChosen;
        overlay.Cancelled += OnCancelled;
        shell.ShowOverlay(overlay);
        try
        {
            return await tcs.Task.ConfigureAwait(true);
        }
        finally
        {
            _anchorKindPromptOpen = false;
        }
    }

    /// <summary>Generates a stable anchor id for persistence.</summary>
    private static string NewAnchorId() =>
        "anc-" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>Flattens all fields under all anchors.</summary>
    private IEnumerable<FieldItem> EnumerateFields() =>
        _anchors.SelectMany(a => a.Fields);

    /// <summary>The single expanded anchor (field create target), if any.</summary>
    private AnchorItem? GetExpandedAnchor() =>
        _anchors.FirstOrDefault(a => a.IsExpanded);

    /// <summary>Expands <paramref name="anchor"/> and collapses every other anchor panel.</summary>
    private void ExpandAnchor(AnchorItem anchor)
    {
        foreach (var item in _anchors)
            item.IsExpanded = ReferenceEquals(item, anchor);
    }

    /// <summary>Toggle expand/collapse for one anchor group.</summary>
    private void AnchorExpand_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: AnchorItem item })
            return;

        if (item.IsExpanded)
            item.IsExpanded = false;
        else
            ExpandAnchor(item);
        RefreshPageOverlays();
    }

    /// <summary>Switch anchor to value-fields kind.</summary>
    private void AnchorKindValue_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: AnchorItem item })
            return;

        SetAnchorKind(item, AnchorKind.Value);
    }

    /// <summary>Switch anchor to table kind.</summary>
    private void AnchorKindTable_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: AnchorItem item })
            return;

        SetAnchorKind(item, AnchorKind.Table);
    }

    /// <summary>UI-only kind switch; clears children when kind changes (no mixing value/table items).</summary>
    private void SetAnchorKind(AnchorItem item, AnchorKind kind)
    {
        if (item.Kind == kind)
            return;
        if (item.Fields.Count > 0)
            item.Fields.Clear();

        item.Kind = kind;
        RenumberFields(item);
        MarkTemplateDirty();
        UpdateExtractionHeaders();
        RefreshPageOverlays();
    }
    private (double Dx, double Dy) GetOverlayOffset(string anchorId) =>
        _overlayOffsetsByAnchorId.TryGetValue(anchorId, out var offset) ? offset : (0, 0);

    /// <summary>
    /// Inner phrase sample used for locate offset (Value and Table share the same definition).
    /// </summary>
    private static Rect GetInnerPhraseSample(AnchorItem anchor) =>
        anchor.PhrasePdfArea.Width > 0.5 && anchor.PhrasePdfArea.Height > 0.5
            ? anchor.PhrasePdfArea
            : anchor.PdfArea;

    /// <summary>Persist table header outer box on both <see cref="AnchorItem.HeaderPdfArea"/> and <see cref="AnchorItem.PdfArea"/>.</summary>
    private static void SetTableOuterBox(AnchorItem owner, Rect templateOuter)
    {
        owner.HeaderPdfArea = templateOuter;
        owner.PdfArea = templateOuter;
    }

    /// <summary>
    /// Warn when the sample page has multiple phrase hits with the same text and the same
    /// inner-box size (width/height). Same text with a different border is ignored.
    /// </summary>
    /// <param name="focus">
    /// When set, only that anchor is checked (create path). When null, all anchors are checked.
    /// </param>
    private void ScanAnchorsForAmbiguity(AnchorItem? focus = null)
    {
        if (_anchors.Count == 0 || _selected is null || !File.Exists(_selected.FilePath))
            return;
        try
        {
            using var document = PdfTextGeometry.OpenReadShared(_selected.FilePath);
            var ambiguous = new List<(string Text, int Matches)>();
            var toCheck = focus is null ? _anchors.AsEnumerable() : new[] { focus };

            foreach (var anchor in toCheck)
            {
                var pageNumber = anchor.PageIndex + 1;
                if (pageNumber < 1 || pageNumber > document.NumberOfPages)
                    continue;

                var sampleBox = GetInnerPhraseSample(anchor);
                if (sampleBox.Width < 0.5 || sampleBox.Height < 0.5)
                    continue;

                var matches = PdfTextGeometry.CountPhraseMatches(
                    document.GetPage(pageNumber),
                    anchor.Text,
                    sampleBox);
                if (matches > 1)
                    ambiguous.Add((anchor.Text, matches));
            }
            if (ambiguous.Count == 0)
                return;

            ShowActionToast(
                BuildAnchorAmbiguityMessage(ambiguous),
                ActionToastKind.Warning,
                nearPreview: _lastSelectionPreview);
        }
        catch
        {
            // Ignore match-count failures.
        }
    }

    /// <summary>Formats ambiguity toast for one or many anchors.</summary>
    private static string BuildAnchorAmbiguityMessage(IReadOnlyList<(string Text, int Matches)> ambiguous)
    {
        var tip = Strings.Toast_AmbiguityTip;
        if (ambiguous.Count == 1)
        {
            var (text, matches) = ambiguous[0];
            return Strings.Format(Strings.Toast_AmbiguityOne, matches, TruncateForToast(text), tip);
        }

        var lines = ambiguous
            .Select(a => Strings.Format(Strings.Toast_AmbiguityBullet, TruncateForToast(a.Text), a.Matches))
            .ToArray();
        return Strings.Format(Strings.Toast_AmbiguityMany, ambiguous.Count, string.Join("\n", lines), tip);
    }

    /// <summary>Shortens long phrases for toast text.</summary>
    private static string TruncateForToast(string text, int max = 48)
    {
        var t = text.Trim();

        if (t.Length <= max)
            return t;
        return t[..(max - 1)] + "…";
    }

    /// <summary>Normalizes phrase text for matching (shared with extract).</summary>
    private static string NormalizeAnchorText(string text) =>
        PdfTextGeometry.NormalizePhrase(text);

    /// <summary>Begin potential anchor-row drag (ignore clicks on buttons).</summary>
    private void AnchorRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestorButton(source) is not null)
            return;
        if (sender is not FrameworkElement { DataContext: AnchorItem item })
            return;

        _listDragStart = e.GetPosition(null);
        _listDragPayload = item;
        _listDragMoved = false;
    }

    /// <summary>Start anchor reorder drag after move threshold.</summary>
    private void AnchorRow_MouseMove(object sender, MouseEventArgs e)
    {
        if (_listDragPayload is not AnchorItem src || e.LeftButton != MouseButtonState.Pressed)
            return;
        if (!HasExceededDragThreshold(e.GetPosition(null)))
            return;

        _listDragMoved = true;
        src.IsListDragging = true;
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(AnchorItem), src), DragDropEffects.Move);
        }
        finally
        {
            src.IsListDragging = false;
            ClearListDropAdornment();
            _listDragPayload = null;
        }
    }

    /// <summary>Show insertion line above/below the hovered anchor row.</summary>
    private void AnchorRow_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(AnchorItem))
            || sender is not FrameworkElement { DataContext: AnchorItem target } fe)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        SetListDropAdornment(target, IsDropInsertBefore(fe, e));
    }

    /// <summary>Reorder anchors on drop; renumber indices.</summary>
    private void AnchorRow_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.Data.GetDataPresent(typeof(AnchorItem)))
                return;
            if (sender is not FrameworkElement { DataContext: AnchorItem target } fe)
                return;
            if (e.Data.GetData(typeof(AnchorItem)) is not AnchorItem source)
                return;

            var insertBefore = IsDropInsertBefore(fe, e);
            if (!TryMoveListItem(_anchors, source, target, insertBefore))
                return;

            RenumberAnchors();
            MarkTemplateDirty();
            SelectAnchor(source);
            e.Handled = true;
        }
        finally
        {
            ClearListDropAdornment();
        }
    }

    /// <summary>Begin potential field-row drag (ignore buttons / rename editor).</summary>
    private void FieldRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && (FindAncestorButton(source) is not null || FindAncestorTextBox(source) is not null))
            return;
        if (sender is not FrameworkElement { DataContext: FieldItem item })
            return;
        if (item.IsEditing)
            return;

        _listDragStart = e.GetPosition(null);
        _listDragPayload = item;
        _listDragMoved = false;
    }

    /// <summary>Start field reorder drag after move threshold (same anchor only).</summary>
    private void FieldRow_MouseMove(object sender, MouseEventArgs e)
    {
        if (_listDragPayload is not FieldItem src || e.LeftButton != MouseButtonState.Pressed)
            return;
        if (!HasExceededDragThreshold(e.GetPosition(null)))
            return;

        _listDragMoved = true;
        src.IsListDragging = true;
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(FieldItem), src), DragDropEffects.Move);
        }
        finally
        {
            src.IsListDragging = false;
            ClearListDropAdornment();
            _listDragPayload = null;
        }
    }

    /// <summary>Show insertion line above/below the hovered field row (same anchor only).</summary>
    private void FieldRow_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(FieldItem))
            || sender is not FrameworkElement { DataContext: FieldItem target } fe
            || e.Data.GetData(typeof(FieldItem)) is not FieldItem source
            || !string.Equals(source.AnchorId, target.AnchorId, StringComparison.Ordinal))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            ClearListDropAdornment();
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        SetListDropAdornment(target, IsDropInsertBefore(fe, e));
    }

    /// <summary>Reorder fields within the owning anchor on drop.</summary>
    private void FieldRow_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.Data.GetDataPresent(typeof(FieldItem)))
                return;
            if (sender is not FrameworkElement { DataContext: FieldItem target } fe)
                return;
            if (e.Data.GetData(typeof(FieldItem)) is not FieldItem source)
                return;
            if (!string.Equals(source.AnchorId, target.AnchorId, StringComparison.Ordinal))
                return;

            var owner = _anchors.FirstOrDefault(a => a.Id == source.AnchorId);
            if (owner is null)
                return;

            var insertBefore = IsDropInsertBefore(fe, e);
            if (!TryMoveListItem(owner.Fields, source, target, insertBefore))
                return;

            RenumberFields(owner);
            MarkTemplateDirty();
            UpdateExtractionHeaders();
            SelectField(source);
            e.Handled = true;
        }
        finally
        {
            ClearListDropAdornment();
        }
    }

    /// <summary>Clear insertion line when the pointer leaves a row (ignore child-element leave).</summary>
    private void ListRow_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement fe)
            return;

        var p = e.GetPosition(fe);
        if (p.X >= 0 && p.Y >= 0 && p.X <= fe.ActualWidth && p.Y <= fe.ActualHeight)
            return;

        if (ReferenceEquals(_listDropAdornmentItem, fe.DataContext))
            ClearListDropAdornment();
    }

    private static bool IsDropInsertBefore(FrameworkElement row, DragEventArgs e) =>
        e.GetPosition(row).Y < row.ActualHeight * 0.5;

    private void SetListDropAdornment(object item, bool insertBefore)
    {
        if (ReferenceEquals(_listDropAdornmentItem, item))
        {
            if (item is AnchorItem currentAnchor)
            {
                if (currentAnchor.ShowDropLineBefore == insertBefore
                    && currentAnchor.ShowDropLineAfter == !insertBefore)
                    return;
            }
            else if (item is FieldItem currentField)
            {
                if (currentField.ShowDropLineBefore == insertBefore
                    && currentField.ShowDropLineAfter == !insertBefore)
                    return;
            }
        }

        ClearListDropAdornment();
        _listDropAdornmentItem = item;
        if (item is AnchorItem anchor)
        {
            anchor.ShowDropLineBefore = insertBefore;
            anchor.ShowDropLineAfter = !insertBefore;
        }
        else if (item is FieldItem field)
        {
            field.ShowDropLineBefore = insertBefore;
            field.ShowDropLineAfter = !insertBefore;
        }
    }

    private void ClearListDropAdornment()
    {
        if (_listDropAdornmentItem is AnchorItem anchor)
        {
            anchor.ShowDropLineBefore = false;
            anchor.ShowDropLineAfter = false;
        }
        else if (_listDropAdornmentItem is FieldItem field)
        {
            field.ShowDropLineBefore = false;
            field.ShowDropLineAfter = false;
        }

        _listDropAdornmentItem = null;
    }

    /// <summary>Move <paramref name="source"/> before/after <paramref name="target"/> in the collection.</summary>
    private static bool TryMoveListItem<T>(ObservableCollection<T> list, T source, T target, bool insertBefore)
        where T : class
    {
        var oldIndex = list.IndexOf(source);
        var targetIndex = list.IndexOf(target);
        if (oldIndex < 0 || targetIndex < 0)
            return false;

        var insertIndex = insertBefore ? targetIndex : targetIndex + 1;
        if (oldIndex < insertIndex)
            insertIndex--;
        if (oldIndex == insertIndex)
            return false;

        list.Move(oldIndex, insertIndex);
        return true;
    }

    private bool HasExceededDragThreshold(Point current)
    {
        return Math.Abs(current.X - _listDragStart.X) >= SystemParameters.MinimumHorizontalDragDistance
               || Math.Abs(current.Y - _listDragStart.Y) >= SystemParameters.MinimumVerticalDragDistance;
    }

    /// <summary>Select anchor row and scroll PDF to it.</summary>
    private void AnchorItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_listDragMoved)
        {
            _listDragMoved = false;
            _listDragPayload = null;
            return;
        }

        if (e.OriginalSource is DependencyObject source && FindAncestorButton(source) is not null)
            return;
        if (sender is not FrameworkElement { DataContext: AnchorItem item })
            return;

        SelectAnchor(item);
        ExpandAnchor(item);
        NavigateToPdfRegion(item.PageIndex, ResolveAnchorDisplayArea(item));
    }

    /// <summary>Select field row and scroll PDF to it.</summary>
    private void FieldItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_listDragMoved)
        {
            _listDragMoved = false;
            _listDragPayload = null;
            return;
        }

        if (e.OriginalSource is DependencyObject source
            && (FindAncestorButton(source) is not null || FindAncestorTextBox(source) is not null))
            return;
        if (sender is not FrameworkElement { DataContext: FieldItem item })
            return;

        SelectField(item);
        NavigateToPdfRegion(
            item.PageIndex,
            ApplyOverlayOffset(item.PdfArea, GetOverlayOffset(item.AnchorId)));
    }

    /// <summary>
    /// Display area for an anchor on the selected file.
    /// Value: located inner phrase bounds. Table: outer header box + phrase offset.
    /// </summary>
    private Rect ResolveAnchorDisplayArea(AnchorItem item)
    {
        if (item.Kind == AnchorKind.Table)
        {
            var outer = item.HeaderPdfArea.Width > 0.5 && item.HeaderPdfArea.Height > 0.5
                ? item.HeaderPdfArea
                : item.PdfArea;
            return ApplyOverlayOffset(outer, GetOverlayOffset(item.Id));
        }

        if (item.PageIndex == _currentPage - 1
            && TryResolveOverlayGeometry(out var located, out _)
            && located.TryGetValue(item.Id, out var area))
            return area;

        if (_selected is null || !File.Exists(_selected.FilePath))
            return item.PdfArea;

        try
        {
            using var document = PdfTextGeometry.OpenReadShared(_selected.FilePath);
            var pageNumber = item.PageIndex + 1;
            if (pageNumber < 1 || pageNumber > document.NumberOfPages)
                return item.PdfArea;
            var page = document.GetPage(pageNumber);
            var innerSample = GetInnerPhraseSample(item);
            var preferNearPdf = PdfTextGeometry.ToPdfUserSpace(innerSample, page.Height);
            if (!PdfTextGeometry.TryFindPhraseBounds(page, item.Text, preferNearPdf, out var foundPdf))
                return item.PdfArea;
            return PdfTextGeometry.FromPdfUserSpace(foundPdf, page.Height);
        }
        catch
        {
            return item.PdfArea;
        }
    }

    /// <summary>Begin renaming a value field (not table columns).</summary>
    private void FieldEditName_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: FieldItem item })
            return;

        // Table column names come from header selection text and cannot be renamed.
        var owner = _anchors.FirstOrDefault(a => a.Id == item.AnchorId);
        if (owner is { Kind: AnchorKind.Table })
            return;

        SelectField(item);
        _fieldNameEditIsNew = false;
        BeginFieldNameEdit(item);
    }

    /// <summary>Toggle the table ruler (base) column for row banding.</summary>
    private void FieldRulerColumn_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: FieldItem item })
            return;

        var owner = _anchors.FirstOrDefault(a => a.Id == item.AnchorId);
        if (owner is not { Kind: AnchorKind.Table })
            return;

        // Toggle off if already ruler; otherwise set as the sole ruler on this table.
        if (item.IsRulerColumn)
        {
            item.IsRulerColumn = false;
        }
        else
        {
            foreach (var column in owner.Fields)
                column.IsRulerColumn = ReferenceEquals(column, item);
        }

        MarkTemplateDirty();
        SelectField(item);
    }

    /// <summary>Delete an anchor and its fields.</summary>
    private void AnchorDelete_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: AnchorItem item })
            return;

        // Deleting an anchor removes its entire fixed-structure field group.
        _anchors.Remove(item);
        RenumberAnchors();
        MarkTemplateDirty();
        UpdateExtractionHeaders();
        RefreshPageOverlays();
        RefreshAllFieldExtractedValues();
    }

    /// <summary>Delete one value field. Table columns cannot be deleted from the list.</summary>
    private void FieldDelete_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: FieldItem item })
            return;

        var owner = _anchors.FirstOrDefault(a => a.Id == item.AnchorId);
        if (owner is { Kind: AnchorKind.Table })
            return;

        if (item.IsEditing)
            item.IsEditing = false;

        owner?.Fields.Remove(item);
        if (owner is not null)
            RenumberFields(owner);

        MarkTemplateDirty();
        UpdateExtractionHeaders();
        RefreshPageOverlays();
    }

    /// <summary>Focus/select when the edit box is first created.</summary>
    private void FieldNameEditBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && box.DataContext is FieldItem { IsEditing: true })
            FocusAndSelectFieldName(box);
    }

    /// <summary>Re-focus when edit mode becomes visible again.</summary>
    private void FieldNameEditBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Visibility toggles on edit; Loaded only runs once, so re-select when shown again.
        if (sender is TextBox { IsVisible: true } box
            && box.DataContext is FieldItem { IsEditing: true })
            FocusAndSelectFieldName(box);
    }

    /// <summary>Deferred focus + SelectAll for the name TextBox.</summary>
    private static void FocusAndSelectFieldName(TextBox box)
    {
        box.Dispatcher.BeginInvoke(
            () =>
            {
                if (!box.IsVisible)
                    return;
                box.Focus();
                box.SelectAll();
            },
            DispatcherPriority.Input);
    }

    /// <summary>Enter commits; Esc cancels (new field → remove).</summary>
    private void FieldNameEditBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: FieldItem item } box)
            return;

        if (e.Key == Key.Enter)
        {
            CommitFieldNameEdit(item, box.Text, cancel: false);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CommitFieldNameEdit(item, box.Text, cancel: true);
            e.Handled = true;
        }
    }

    /// <summary>Commit name when leaving the edit box.</summary>
    private void FieldNameEditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: FieldItem item } box && item.IsEditing)
            CommitFieldNameEdit(item, box.Text, cancel: false);
    }

    /// <summary>Enter inline name edit for a value field.</summary>
    private void BeginFieldNameEdit(FieldItem item)
    {
        var owner = _anchors.FirstOrDefault(a => a.Id == item.AnchorId);
        if (owner is { Kind: AnchorKind.Table })
            return;

        foreach (var field in EnumerateFields())
            field.IsEditing = false;

        _fieldNameEditOriginal = item.Name;
        item.IsEditing = true;
    }

    /// <summary>Commit or cancel name edit; Esc on new field removes it.</summary>
    private void CommitFieldNameEdit(FieldItem item, string text, bool cancel)
    {
        if (!item.IsEditing)
            return;

        // Esc on a newly created value field removes it (cancel whole add).
        if (cancel && _fieldNameEditIsNew)
        {
            _fieldNameEditIsNew = false;
            item.IsEditing = false;
            _fieldNameEditOriginal = null;

            var owner = _anchors.FirstOrDefault(a => a.Id == item.AnchorId);
            // Table columns are never created via inline name edit; refuse delete for safety.
            if (owner is not { Kind: AnchorKind.Table })
            {
                owner?.Fields.Remove(item);
                if (owner is not null)
                    RenumberFields(owner);
            }

            _templateDirty = _dirtyBeforeNewFieldEdit;

            UpdateTemplateModifiedUi();
            UpdateExtractionActions();
            UpdateExtractionHeaders();
            RefreshPageOverlays();
            return;
        }

        var isNew = _fieldNameEditIsNew;
        var next = cancel ? (_fieldNameEditOriginal ?? item.Name) : text.Trim();

        if (string.IsNullOrWhiteSpace(next))
            next = _fieldNameEditOriginal ?? item.Name;
        if (!cancel)
        {
            if (isNew || !string.Equals(item.Name, next, StringComparison.Ordinal))
            {
                item.Name = next;
                MarkTemplateDirty();
            }
        }
        else
        {
            // Rename cancel: restore previous name (unchanged behavior).
            item.Name = next;
        }
        item.IsEditing = false;
        _fieldNameEditOriginal = null;
        _fieldNameEditIsNew = false;
    }

    /// <summary>Select one anchor (clears field selection).</summary>
    private void SelectAnchor(AnchorItem item)
    {
        foreach (var field in EnumerateFields())
        {
            if (field.IsEditing)
                field.IsEditing = false;
            field.IsSelected = false;
        }

        foreach (var anchor in _anchors)
            anchor.IsSelected = ReferenceEquals(anchor, item);
        RefreshPageOverlays();
    }

    /// <summary>Select one field, expand its owner, refresh overlays.</summary>
    private void SelectField(FieldItem item)
    {
        foreach (var anchor in _anchors)
            anchor.IsSelected = false;

        foreach (var field in EnumerateFields())
        {
            if (!ReferenceEquals(field, item) && field.IsEditing)
                field.IsEditing = false;
            field.IsSelected = ReferenceEquals(field, item);
        }

        var owner = _anchors.FirstOrDefault(a => a.Id == item.AnchorId);
        if (owner is not null)
            ExpandAnchor(owner);

        RefreshPageOverlays();
    }

    /// <summary>1-based anchor indices.</summary>
    private void RenumberAnchors()
    {
        for (var i = 0; i < _anchors.Count; i++)
            _anchors[i].Index = i + 1;
    }

    /// <summary>
    /// Renumbers fields within each anchor independently (1..n per anchor).
    /// When <paramref name="anchor"/> is set, only that anchor's fields are renumbered.
    /// </summary>
    private void RenumberFields(AnchorItem? anchor = null)
    {
        if (anchor is not null)
        {
            for (var i = 0; i < anchor.Fields.Count; i++)
                anchor.Fields[i].Index = i + 1;
            return;
        }
        foreach (var item in _anchors)
        {
            for (var i = 0; i < item.Fields.Count; i++)
                item.Fields[i].Index = i + 1;
        }
    }
}
