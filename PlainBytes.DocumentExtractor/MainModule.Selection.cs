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
    /// <summary>Hit-test divider / resize / select / clear; else start rubber-band.</summary>
    private void SelectionLayer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_selected is null)
            return;

        var pos = e.GetPosition(SelectionLayer);

        // Double-click on table header: insert/remove column divider.
        if (e.ClickCount >= 2 && TryToggleTableDividerAt(pos))
        {
            e.Handled = true;
            return;
        }

        if (TryBeginTableDividerDragAt(pos))
        {
            SelectionLayer.CaptureMouse();
            e.Handled = true;
            return;
        }
        if (TryBeginFieldResizeAt(pos))
        {
            SelectionLayer.CaptureMouse();
            e.Handled = true;
            return;
        }
        if (TrySelectFieldAt(pos))
        {
            e.Handled = true;
            return;
        }
        if (TrySelectAnchorAt(pos))
        {
            e.Handled = true;
            return;
        }

        // Blank click: clear selection, then rubber-band create on drag.
        ClearOverlaySelection();

        _isSelecting = true;
        _selectionCreatesAnchor = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        _selectionStart = pos;

        ApplyRubberBandStyle(_selectionCreatesAnchor);
        UpdateRubberBand(pos);

        SelectionRubberBand.Visibility = Visibility.Visible;
        SelectionLayer.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>Rubber-band drag, field resize, divider drag, or hover cursors.</summary>
    private void SelectionLayer_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(SelectionLayer);
        if (_isDraggingTableDivider && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateTableDividerDrag(pos);
            e.Handled = true;
            return;
        }
        if (_isResizingField && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateFieldResize(pos);
            e.Handled = true;
            return;
        }
        if (_isSelecting && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateRubberBand(pos);
            e.Handled = true;
            return;
        }
        UpdateOverlayHoverCursor(pos);
    }

    /// <summary>Finish resize, divider drag, or rubber-band create.</summary>
    private void SelectionLayer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(SelectionLayer);

        if (_isDraggingTableDivider)
        {
            EndTableDividerDrag(pos);
            if (SelectionLayer.IsMouseCaptured)
                SelectionLayer.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (_isResizingField)
        {
            EndFieldResize(pos);
            if (SelectionLayer.IsMouseCaptured)
                SelectionLayer.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (!_isSelecting)
            return;

        UpdateRubberBand(pos);

        SelectionLayer.ReleaseMouseCapture();

        FinishSelection();
        e.Handled = true;
    }

    /// <summary>Complete in-progress resize/selection if capture is lost.</summary>
    private void SelectionLayer_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isDraggingTableDivider)
        {
            EndTableDividerDrag(e.GetPosition(SelectionLayer));
            return;
        }
        if (_isResizingField)
        {
            EndFieldResize(e.GetPosition(SelectionLayer));
            return;
        }
        if (!_isSelecting)
            return;

        FinishSelection();
    }

    /// <summary>Field (solid) vs anchor (dashed) rubber-band look.</summary>
    private void ApplyRubberBandStyle(bool anchorMode)
    {
        SelectionRubberBand.Stroke = anchorMode ? AnchorRubberStroke : FieldRubberStroke;
        SelectionRubberBand.Fill = anchorMode ? AnchorRubberFill : FieldRubberFill;
        SelectionRubberBand.StrokeDashArray = anchorMode
            ? new DoubleCollection { 2, 2 }
            : null;
    }

    /// <summary>Sizes the rubber-band rectangle from start to current point.</summary>
    private void UpdateRubberBand(Point current)
    {
        var x = Math.Min(_selectionStart.X, current.X);
        var y = Math.Min(_selectionStart.Y, current.Y);
        var w = Math.Abs(current.X - _selectionStart.X);
        var h = Math.Abs(current.Y - _selectionStart.Y);

        Canvas.SetLeft(SelectionRubberBand, x);
        Canvas.SetTop(SelectionRubberBand, y);

        SelectionRubberBand.Width = w;
        SelectionRubberBand.Height = h;
    }

    /// <summary>Ctrl+drag → anchor; plain drag → field under the expanded anchor.</summary>
    private void FinishSelection()
    {
        var createAnchor = _selectionCreatesAnchor;
        _isSelecting = false;
        _selectionCreatesAnchor = false;
        SelectionRubberBand.Visibility = Visibility.Collapsed;

        var previewRect = new Rect(
            Canvas.GetLeft(SelectionRubberBand),
            Canvas.GetTop(SelectionRubberBand),
            SelectionRubberBand.Width,
            SelectionRubberBand.Height);
        _lastSelectionPreview = previewRect;

        SelectionRubberBand.Width = 0;
        SelectionRubberBand.Height = 0;

        ApplyRubberBandStyle(anchorMode: false);

        if (previewRect.Width < 3 || previewRect.Height < 3)
            return;
        if (_selected is null || !File.Exists(_selected.FilePath))
            return;

        var (surfaceW, surfaceH) = GetPageSurfaceDipSize();
        if (surfaceW <= 0 || surfaceH <= 0)
            return;

        var pdfRect = MapPreviewToPdf(previewRect, surfaceW, surfaceH, _pageWidthPt, _pageHeightPt);
        var pageIndex = _currentPage - 1;

        // Phrase bounds for anchors; field create uses the rubber-band rect itself.
        var (text, textBounds) = ExtractTextAndBounds(_selected.FilePath, _currentPage, pdfRect);
        var expanded = GetExpandedAnchor();
        if (string.IsNullOrWhiteSpace(text))
        {
            // Table header split has its own empty-text handling.
            if (createAnchor || expanded is not { Kind: AnchorKind.Table })
                ShowActionToast(Strings.Toast_NoTextInArea, ActionToastKind.Warning, nearPreview: previewRect);
            if (createAnchor)
                return;
        }

        if (createAnchor)
            _ = AddAnchorFromSelectionAsync(pageIndex, text, textBounds, pdfRect);
        else
        {
            if (_anchors.Count == 0 || expanded is null)
            {
                ShowActionToast(Strings.Toast_NeedAnchorBeforeField, ActionToastKind.Warning, nearPreview: previewRect);
                return;
            }
            AddFieldFromSelection(pageIndex, pdfRect, text);
        }
    }

    /// <summary>Starts 8-knob resize when pressing a handle on the selected field (value fields only).</summary>
    private bool TryBeginFieldResizeAt(Point pos)
    {
        var field = EnumerateFields().FirstOrDefault(f => f.IsSelected && f.PageIndex == _currentPage - 1);
        if (field is null)
            return false;

        // Table columns use draggable dividers instead of independent resize knobs.
        var owner = _anchors.FirstOrDefault(a => a.Id == field.AnchorId);
        if (owner is { Kind: AnchorKind.Table })
            return false;

        var (surfaceW, surfaceH) = GetPageSurfaceDipSize();
        if (surfaceW <= 0 || surfaceH <= 0 || _pageWidthPt <= 0 || _pageHeightPt <= 0)
            return false;
        var preview = MapPdfToPreview(
            ApplyOverlayOffset(field.PdfArea, GetOverlayOffset(field.AnchorId)),
            surfaceW,
            surfaceH,
            _pageWidthPt,
            _pageHeightPt);
        if (HitTestFieldResizeHandle(preview, pos) is not { } handle)
            return false;
        _isResizingField = true;
        _resizeField = field;
        _resizeHandle = handle;
        _resizeStartPreview = preview;
        _resizeStartMouse = pos;
        SelectionLayer.Cursor = CursorForHandle(handle);
        OpenResizePdfDocument();
        return true;
    }

    /// <summary>
    /// Clicking a field overlay selects it (same as clicking its row in the Fields list).
    /// Prefer the already-selected field when boxes overlap, then the topmost hit.
    /// </summary>
    private bool TrySelectFieldAt(Point pos)
    {
        var (surfaceW, surfaceH) = GetPageSurfaceDipSize();
        if (surfaceW <= 0 || surfaceH <= 0 || _pageWidthPt <= 0 || _pageHeightPt <= 0)
            return false;

        var pageIndex = _currentPage - 1;
        FieldItem? hit = null;
        foreach (var field in EnumerateFields())
        {
            if (field.PageIndex != pageIndex)
                continue;

            var preview = MapPdfToPreview(
                ApplyOverlayOffset(field.PdfArea, GetOverlayOffset(field.AnchorId)),
                surfaceW,
                surfaceH,
                _pageWidthPt,
                _pageHeightPt);

            if (!preview.Contains(pos))
                continue;

            if (field.IsSelected)
            {
                hit = field;
                break;
            }
            hit = field;
        }
        if (hit is null)
            return false;

        SelectField(hit);
        ScrollFieldListTo(hit);
        return true;
    }

    /// <summary>Clicking an anchor overlay selects it (same as clicking its row).</summary>
    private bool TrySelectAnchorAt(Point pos)
    {
        var (surfaceW, surfaceH) = GetPageSurfaceDipSize();
        if (surfaceW <= 0 || surfaceH <= 0 || _pageWidthPt <= 0 || _pageHeightPt <= 0)
            return false;

        var pageIndex = _currentPage - 1;
        AnchorItem? hit = null;
        foreach (var anchor in _anchors)
        {
            if (anchor.PageIndex != pageIndex)
                continue;

            // Hit-test the displayed anchor box (table: user rubber-band; value: stored area + offset).
            var area = ApplyOverlayOffset(anchor.PdfArea, GetOverlayOffset(anchor.Id));
            var preview = MapPdfToPreview(area, surfaceW, surfaceH, _pageWidthPt, _pageHeightPt);
            if (!preview.Contains(pos))
                continue;

            if (anchor.IsSelected)
            {
                hit = anchor;
                break;
            }
            hit = anchor;
        }
        if (hit is null)
            return false;

        SelectAnchor(hit);
        return true;
    }

    /// <summary>Clears field/anchor selection highlighting in the list and PDF overlays.</summary>
    private void ClearOverlaySelection()
    {
        var changed = false;

        foreach (var field in EnumerateFields())
        {
            if (!field.IsSelected)
                continue;
            field.IsSelected = false;
            changed = true;
        }
        foreach (var anchor in _anchors)
        {
            if (!anchor.IsSelected)
                continue;
            anchor.IsSelected = false;
            changed = true;
        }
        if (changed)
            RefreshPageOverlays();
    }

    /// <summary>Expands owner anchor and brings the field row into view.</summary>
    private void ScrollFieldListTo(FieldItem item)
    {
        // Defer until the selection rail style has updated; nested under an expanded anchor.
        Dispatcher.BeginInvoke(
            () =>
            {
                var owner = _anchors.FirstOrDefault(a => a.Id == item.AnchorId);
                if (owner is not null)
                    ExpandAnchor(owner);
                item.IsSelected = true;
                // Best-effort: bring the selected field into view after expand.
                Dispatcher.BeginInvoke(
                    () =>
                    {
                        if (AnchorListItems.ItemContainerGenerator.ContainerFromItem(owner)
                            is not FrameworkElement anchorContainer)
                            return;
                        FrameworkElement? match = null;
                        FindFieldContainer(anchorContainer, item, ref match);
                        match?.BringIntoView();
                    },
                    DispatcherPriority.Loaded);
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>DFS for the ItemsControl container matching a FieldItem.</summary>
    private static void FindFieldContainer(DependencyObject root, FieldItem target, ref FrameworkElement? match)
    {
        if (match is not null)
            return;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && ReferenceEquals(fe.DataContext, target))
            {
                match = fe;
                return;
            }
            FindFieldContainer(child, target, ref match);
        }
    }

    /// <summary>Live-update field PdfArea while dragging a resize knob.</summary>
    private void UpdateFieldResize(Point pos)
    {
        if (_resizeField is null)
            return;

        var (surfaceW, surfaceH) = GetPageSurfaceDipSize();
        if (surfaceW <= 0 || surfaceH <= 0 || _pageWidthPt <= 0 || _pageHeightPt <= 0)
            return;

        var dx = pos.X - _resizeStartMouse.X;
        var dy = pos.Y - _resizeStartMouse.Y;
        var preview = ApplyFieldResizeDelta(_resizeStartPreview, dx, dy, _resizeHandle);

        preview = ClampPreviewRectToPage(preview, surfaceW, surfaceH);
        if (preview.Width < FieldResizeMinPreview || preview.Height < FieldResizeMinPreview)
            return;

        _resizeField.PdfArea = RemoveOverlayOffset(
            MapPreviewToPdf(preview, surfaceW, surfaceH, _pageWidthPt, _pageHeightPt),
            GetOverlayOffset(_resizeField.AnchorId));
        RefreshFieldExtractedValue(_resizeField, _resizePdfDocument);
        RefreshPageOverlays();
    }

    /// <summary>Commit resize, refresh extracted preview text, mark dirty.</summary>
    private void EndFieldResize(Point pos)
    {
        if (!_isResizingField)
            return;

        UpdateFieldResize(pos);

        var field = _resizeField;
        _isResizingField = false;
        _resizeField = null;
        SelectionLayer.Cursor = null;
        DisposeResizePdfDocument();

        if (field is null)
            return;

        MarkTemplateDirty();
        RefreshFieldExtractedValue(field);
        RefreshPageOverlays();
        UpdateOverlayHoverCursor(pos);
    }

    /// <summary>Keeps a shared PdfPig doc open during resize for live text.</summary>
    private void OpenResizePdfDocument()
    {
        DisposeResizePdfDocument();
        if (_selected is null || !File.Exists(_selected.FilePath))
            return;
        try
        {
            _resizePdfDocument = PdfTextGeometry.OpenReadShared(_selected.FilePath);
        }
        catch
        {
            _resizePdfDocument = null;
        }
    }

    /// <summary>Disposes the resize-time PdfPig document.</summary>
    private void DisposeResizePdfDocument()
    {
        _resizePdfDocument?.Dispose();
        _resizePdfDocument = null;
    }

    /// <summary>Re-extract one field (or table column preview) after resize.</summary>
    private void RefreshFieldExtractedValue(FieldItem field, PdfDocument? openDocument = null)
    {
        var owner = _anchors.FirstOrDefault(a => a.Id == field.AnchorId);
        if (owner is { Kind: AnchorKind.Table })
        {
            if (openDocument is not null)
                ApplyTableExtractPreview(openDocument, owner);
            else if (_selected is not null && File.Exists(_selected.FilePath))
            {
                using var document = PdfTextGeometry.OpenReadShared(_selected.FilePath);
                EnsureOverlayOffsetsResolved(document);
                ApplyTableExtractPreview(document, owner);
            }
            return;
        }

        var area = ApplyOverlayOffset(field.PdfArea, GetOverlayOffset(field.AnchorId));
        if (openDocument is not null)
        {
            var (text, _) = ExtractTextAndBounds(openDocument, field.PageIndex + 1, area);
            field.Value = string.IsNullOrWhiteSpace(text) ? Strings.Field_NoText : text;
            return;
        }

        if (_selected is null || !File.Exists(_selected.FilePath))
            return;

        var (fromFile, _) = ExtractTextAndBounds(_selected.FilePath, field.PageIndex + 1, area);
        field.Value = string.IsNullOrWhiteSpace(fromFile) ? Strings.Field_NoText : fromFile;
    }

    /// <summary>Runs table extract and shows per-column preview text.</summary>
    private void ApplyTableExtractPreview(PdfDocument document, AnchorItem tableAnchor)
    {
        var columns = tableAnchor.Fields.ToList();

        foreach (var field in columns)
            field.Value = string.Empty;
        if (columns.Count == 0)
            return;

        if (!TryLocateAnchorInFile(document, tableAnchor, out var tableLocated))
        {
            foreach (var field in columns)
                field.Value = Strings.Field_NoText;
            return;
        }

        var offset = GetOverlayOffset(tableAnchor.Id);
        var columnDefs = columns
            .Select(f => new TableColumnDef(ApplyOverlayOffset(f.PdfArea, offset), f.IsRulerColumn))
            .ToList();
        var located = BuildLocatedAnchorsOnPage(document, tableAnchor.PageIndex);

        if (!TableExtractionService.TryExtract(
                document,
                tableAnchor.PageIndex,
                tableAnchor.Id,
                tableLocated,
                columnDefs,
                located,
                out var table))
        {
            foreach (var field in columns)
                field.Value = Strings.Field_NoText;
            return;
        }

        var texts = TableExtractionService.FormatColumnsForExport(table, columns.Count);
        for (var i = 0; i < columns.Count; i++)
        {
            columns[i].Value = string.IsNullOrWhiteSpace(texts[i]) ? Strings.Field_NoText : texts[i];
        }
    }

    /// <summary>Locate all anchors on a page for table bounds resolution.</summary>
    private List<TableBoundsResolver.LocatedAnchor> BuildLocatedAnchorsOnPage(
        PdfDocument document,
        int pageIndex)
    {
        var list = new List<TableBoundsResolver.LocatedAnchor>();

        foreach (var anchor in _anchors)
        {
            if (anchor.PageIndex != pageIndex)
                continue;
            if (!TryLocateAnchorInFile(document, anchor, out var area))
                continue;
            list.Add(new TableBoundsResolver.LocatedAnchor(anchor.Id, pageIndex, area));
        }
        return list;
    }

    /// <summary>Find anchor phrase bounds in the current PDF (top-left).</summary>
    private static bool TryLocateAnchorInFile(PdfDocument document, AnchorItem anchor, out Rect foundTopLeft)
    {
        foundTopLeft = default;
        var pageNumber = anchor.PageIndex + 1;
        if (pageNumber < 1 || pageNumber > document.NumberOfPages)
            return false;

        var page = document.GetPage(pageNumber);
        var preferNearPdf = PdfTextGeometry.ToPdfUserSpace(anchor.PdfArea, page.Height);
        if (!PdfTextGeometry.TryFindPhraseBounds(page, anchor.Text, preferNear: preferNearPdf, out var foundPdf))
            return false;

        foundTopLeft = PdfTextGeometry.FromPdfUserSpace(foundPdf, page.Height);
        return foundTopLeft.Width >= 0.5 && foundTopLeft.Height >= 0.5;
    }

    /// <summary>
    /// Fills each field's preview value from the selected PDF (template area + owning-anchor offset).
    /// Call after applying a template or switching the sample file.
    /// </summary>
    private void RefreshAllFieldExtractedValues()
    {
        var fields = EnumerateFields().ToList();
        if (fields.Count == 0)
            return;

        if (_selected is null || !File.Exists(_selected.FilePath))
        {
            foreach (var field in fields)
                field.Value = string.Empty;
            return;
        }

        try
        {
            using var document = PdfTextGeometry.OpenReadShared(_selected.FilePath);
            EnsureOverlayOffsetsResolved(document);
            foreach (var anchor in _anchors)
            {
                if (anchor.Kind == AnchorKind.Table)
                {
                    ApplyTableExtractPreview(document, anchor);
                    continue;
                }
                foreach (var field in anchor.Fields)
                {
                    var area = ApplyOverlayOffset(field.PdfArea, GetOverlayOffset(field.AnchorId));
                    var (text, _) = ExtractTextAndBounds(document, field.PageIndex + 1, area);
                    field.Value = string.IsNullOrWhiteSpace(text) ? Strings.Field_NoText : text;
                }
            }
        }
        catch
        {
            foreach (var field in fields)
                field.Value = Strings.Field_NoText;
        }
    }

    /// <summary>Shows resize / divider cursors when hovering handles.</summary>
    private void UpdateOverlayHoverCursor(Point pos)
    {
        if (_isSelecting || _isResizingField || _isDraggingTableDivider || _selected is null)
            return;

        if (HitTestTableDividerAt(pos, out _, out _) is not null)
        {
            SelectionLayer.Cursor = Cursors.SizeWE;
            return;
        }

        var field = EnumerateFields().FirstOrDefault(f => f.IsSelected && f.PageIndex == _currentPage - 1);
        if (field is null)
        {
            SelectionLayer.Cursor = null;
            return;
        }

        var owner = _anchors.FirstOrDefault(a => a.Id == field.AnchorId);
        if (owner is { Kind: AnchorKind.Table })
        {
            SelectionLayer.Cursor = null;
            return;
        }

        var (surfaceW, surfaceH) = GetPageSurfaceDipSize();
        if (surfaceW <= 0 || surfaceH <= 0)
        {
            SelectionLayer.Cursor = null;
            return;
        }
        var preview = MapPdfToPreview(
            ApplyOverlayOffset(field.PdfArea, GetOverlayOffset(field.AnchorId)),
            surfaceW,
            surfaceH,
            _pageWidthPt,
            _pageHeightPt);

        SelectionLayer.Cursor = HitTestFieldResizeHandle(preview, pos) is { } handle
            ? CursorForHandle(handle)
            : null;
    }

    /// <summary>Which resize knob contains the point, if any.</summary>
    private static FieldResizeHandleKind? HitTestFieldResizeHandle(Rect preview, Point pos)
    {
        var hit = (FieldResizeKnobSize / 2) + FieldResizeHitSlop;

        foreach (var (kind, center) in EnumerateFieldResizeHandleCenters(preview))
        {
            if (Math.Abs(pos.X - center.X) <= hit && Math.Abs(pos.Y - center.Y) <= hit)
                return kind;
        }
        return null;
    }
    private static IEnumerable<(FieldResizeHandleKind Kind, Point Center)> EnumerateFieldResizeHandleCenters(Rect preview)
    {
        var midX = preview.X + preview.Width / 2;
        var midY = preview.Y + preview.Height / 2;

        yield return (FieldResizeHandleKind.TopLeft, new Point(preview.Left, preview.Top));
        yield return (FieldResizeHandleKind.Top, new Point(midX, preview.Top));
        yield return (FieldResizeHandleKind.TopRight, new Point(preview.Right, preview.Top));
        yield return (FieldResizeHandleKind.Right, new Point(preview.Right, midY));
        yield return (FieldResizeHandleKind.BottomRight, new Point(preview.Right, preview.Bottom));
        yield return (FieldResizeHandleKind.Bottom, new Point(midX, preview.Bottom));
        yield return (FieldResizeHandleKind.BottomLeft, new Point(preview.Left, preview.Bottom));
        yield return (FieldResizeHandleKind.Left, new Point(preview.Left, midY));
    }

    /// <summary>Maps handle kind to WPF Cursor.</summary>
    private static Cursor CursorForHandle(FieldResizeHandleKind handle) => handle switch
    {
        FieldResizeHandleKind.Top or FieldResizeHandleKind.Bottom => Cursors.SizeNS,
        FieldResizeHandleKind.Left or FieldResizeHandleKind.Right => Cursors.SizeWE,
        FieldResizeHandleKind.TopLeft or FieldResizeHandleKind.BottomRight => Cursors.SizeNWSE,
        FieldResizeHandleKind.TopRight or FieldResizeHandleKind.BottomLeft => Cursors.SizeNESW,
        _ => Cursors.Arrow,
    };

    /// <summary>Same edge math as Redactor ApplyMarkResizeDelta.</summary>
    private static Rect ApplyFieldResizeDelta(Rect start, double dx, double dy, FieldResizeHandleKind h)
    {
        var L = start.Left;
        var T = start.Top;
        var R = start.Right;
        var B = start.Bottom;

        switch (h)
        {
            case FieldResizeHandleKind.TopLeft:
                L += dx;
                T += dy;
                break;
            case FieldResizeHandleKind.Top:
                T += dy;
                break;
            case FieldResizeHandleKind.TopRight:
                R += dx;
                T += dy;
                break;
            case FieldResizeHandleKind.Right:
                R += dx;
                break;
            case FieldResizeHandleKind.BottomRight:
                R += dx;
                B += dy;
                break;
            case FieldResizeHandleKind.Bottom:
                B += dy;
                break;
            case FieldResizeHandleKind.BottomLeft:
                L += dx;
                B += dy;
                break;
            case FieldResizeHandleKind.Left:
                L += dx;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(h), h, null);
        }

        var left = Math.Min(L, R);
        var right = Math.Max(L, R);
        var top = Math.Min(T, B);
        var bottom = Math.Max(T, B);

        EnforceMinPreviewOnResize(ref left, ref right, ref top, ref bottom, h);
        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>Keeps resized preview rect above a minimum size.</summary>
    private static void EnforceMinPreviewOnResize(
        ref double left,
        ref double right,
        ref double top,
        ref double bottom,
        FieldResizeHandleKind h)
    {
        if (right - left < FieldResizeMinPreview)
        {
            var fixRightEdge = h is FieldResizeHandleKind.TopLeft
                or FieldResizeHandleKind.Left
                or FieldResizeHandleKind.BottomLeft;
            if (fixRightEdge)
                left = right - FieldResizeMinPreview;
            else
                right = left + FieldResizeMinPreview;
        }
        if (bottom - top < FieldResizeMinPreview)
        {
            var fixBottomEdge = h is FieldResizeHandleKind.TopLeft
                or FieldResizeHandleKind.Top
                or FieldResizeHandleKind.TopRight;
            if (fixBottomEdge)
                top = bottom - FieldResizeMinPreview;
            else
                bottom = top + FieldResizeMinPreview;
        }
    }

    /// <summary>Clamps a preview rect to the page surface.</summary>
    private static Rect ClampPreviewRectToPage(Rect preview, double pageW, double pageH)
    {
        if (pageW < FieldResizeMinPreview || pageH < FieldResizeMinPreview)
            return preview;

        var left = Math.Clamp(preview.Left, 0, pageW - FieldResizeMinPreview);
        var top = Math.Clamp(preview.Top, 0, pageH - FieldResizeMinPreview);
        var right = Math.Clamp(preview.Right, left + FieldResizeMinPreview, pageW);
        var bottom = Math.Clamp(preview.Bottom, top + FieldResizeMinPreview, pageH);

        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>Expanded table anchor used for column divider edit, if any.</summary>
    private AnchorItem? GetTableAnchorForColumnEdit()
    {
        var expanded = GetExpandedAnchor();
        if (expanded is { Kind: AnchorKind.Table, Fields.Count: > 0 })
            return expanded;

        var selectedField = EnumerateFields().FirstOrDefault(f => f.IsSelected);
        if (selectedField is null)
            return null;

        var owner = _anchors.FirstOrDefault(a => a.Id == selectedField.AnchorId);
        return owner is { Kind: AnchorKind.Table, Fields.Count: > 0 } ? owner : null;
    }

    /// <summary>Starts dragging a table column divider under the pointer.</summary>
    private bool TryBeginTableDividerDragAt(Point pos)
    {
        var table = HitTestTableDividerAt(pos, out var dividerIndex, out _);
        if (table is null)
            return false;

        var columns = table.Fields
            .Where(f => f.PageIndex == _currentPage - 1)
            .OrderBy(f => f.PdfArea.X)
            .ToList();
        if (dividerIndex < 0 || dividerIndex >= columns.Count - 1)
            return false;

        _isDraggingTableDivider = true;
        _dividerTableAnchor = table;
        _dividerIndex = dividerIndex;
        _dividerDragColumns = columns;
        _dividerSelectedColIndex = columns.FindIndex(c => c.IsSelected);
        SelectionLayer.Cursor = Cursors.SizeWE;
        OpenResizePdfDocument();
        if (_overlayOffsetsByAnchorId.Count == 0)
            TryResolveOverlayGeometry(out _, out _overlayOffsetsByAnchorId);

        _dividerDragVisual = FieldOverlayCanvas.Children
            .OfType<FrameworkElement>()
            .FirstOrDefault(fe => fe.Tag is TableDividerOverlayTag tag
                && tag.AnchorId == table.Id
                && tag.DividerIndex == dividerIndex);
        _dividerSelectedColVisual = FieldOverlayCanvas.Children
            .OfType<FrameworkElement>()
            .FirstOrDefault(fe => fe.Tag is TableSelectedColOverlayTag tag
                && tag.AnchorId == table.Id);
        return true;
    }

    /// <summary>Live-update abutting column bands while dragging a divider.</summary>
    private void UpdateTableDividerDrag(Point pos)
    {
        if (!_isDraggingTableDivider
            || _dividerTableAnchor is null
            || _dividerDragColumns is null)
            return;

        var (surfaceW, surfaceH) = GetPageSurfaceDipSize();
        if (surfaceW <= 0 || surfaceH <= 0 || _pageWidthPt <= 0 || _pageHeightPt <= 0)
            return;

        var offset = GetOverlayOffset(_dividerTableAnchor.Id);
        var columns = _dividerDragColumns;
        if (_dividerIndex < 0 || _dividerIndex >= columns.Count - 1)
            return;

        var pdfPoint = MapPreviewToPdf(
            new Rect(pos.X, pos.Y, 1, 1),
            surfaceW,
            surfaceH,
            _pageWidthPt,
            _pageHeightPt);
        var templateX = pdfPoint.X - offset.Dx;

        var areas = new List<Rect>(columns.Count);
        for (var i = 0; i < columns.Count; i++)
            areas.Add(columns[i].PdfArea);
        var moved = TableHeaderColumnService.MoveDivider(areas, _dividerIndex, templateX);
        ApplyColumnAreasToFields(columns, moved);

        // Move only the active divider (+ selected strip) — no overlay rebuild during drag.
        var dividerPtX = moved[_dividerIndex + 1].X + offset.Dx;
        Rect headerTemplate = _dividerTableAnchor.HeaderPdfArea;
        if (headerTemplate.IsEmpty || headerTemplate.Width < 0.5)
            headerTemplate = TableHeaderColumnService.UnionHeader(moved);
        var headerDisplay = ApplyOverlayOffset(headerTemplate, offset);
        var dividerPreview = MapPdfToPreview(
            new Rect(dividerPtX, headerDisplay.Top, 0.01, Math.Max(0.5, headerDisplay.Height)),
            surfaceW,
            surfaceH,
            _pageWidthPt,
            _pageHeightPt);
        if (_dividerDragVisual is not null)
            Canvas.SetLeft(_dividerDragVisual, dividerPreview.X - 6);

        if (_dividerSelectedColVisual is not null
            && _dividerSelectedColIndex >= 0
            && _dividerSelectedColIndex < moved.Count)
        {
            var selectedPreview = MapPdfToPreview(
                ApplyOverlayOffset(moved[_dividerSelectedColIndex], offset),
                surfaceW,
                surfaceH,
                _pageWidthPt,
                _pageHeightPt);
            Canvas.SetLeft(_dividerSelectedColVisual, selectedPreview.X);
            _dividerSelectedColVisual.Width = Math.Max(0, selectedPreview.Width);
            if (_dividerSelectedColVisual is Canvas canvas
                && canvas.Children.Count > 0
                && canvas.Children[0] is System.Windows.Shapes.Rectangle box)
            {
                box.Width = Math.Max(0, selectedPreview.Width);
            }
        }
    }

    /// <summary>Commit divider drag and mark template dirty.</summary>
    private void EndTableDividerDrag(Point pos)
    {
        if (!_isDraggingTableDivider)
            return;

        UpdateTableDividerDrag(pos);
        var table = _dividerTableAnchor;
        var columns = _dividerDragColumns;
        _isDraggingTableDivider = false;
        _dividerTableAnchor = null;
        _dividerDragColumns = null;
        _dividerDragVisual = null;
        _dividerSelectedColVisual = null;
        _dividerSelectedColIndex = -1;
        SelectionLayer.Cursor = null;

        if (table is not null && columns is { Count: > 0 })
            RefreshTableColumnNames(table, columns, _resizePdfDocument);

        DisposeResizePdfDocument();

        if (table is null)
            return;

        MarkTemplateDirty();
        RefreshAllFieldExtractedValues();
        RefreshPageOverlays();
        UpdateOverlayHoverCursor(pos);
    }

    /// <summary>
    /// Double-click: near a divider → remove it; inside a column band → insert a divider at X.
    /// </summary>
    private bool TryToggleTableDividerAt(Point pos)
    {
        var table = GetTableAnchorForColumnEdit();
        if (table is null || table.Fields.Count == 0)
            return false;

        var pageIndex = _currentPage - 1;
        var columns = table.Fields
            .Where(f => f.PageIndex == pageIndex)
            .OrderBy(f => f.PdfArea.X)
            .ToList();
        if (columns.Count == 0)
            return false;

        var (surfaceW, surfaceH) = GetPageSurfaceDipSize();
        if (surfaceW <= 0 || surfaceH <= 0 || _pageWidthPt <= 0 || _pageHeightPt <= 0)
            return false;

        var hitTable = HitTestTableDividerAt(pos, out var dividerIndex, out _);
        if (hitTable is not null && ReferenceEquals(hitTable, table))
        {
            var merged = TableHeaderColumnService.TryRemoveDivider(
                columns.Select(c => c.PdfArea).ToList(),
                dividerIndex);
            if (merged is null)
                return false;

            RebuildTableColumnsFromAreas(table, columns[0].PageIndex, merged);
            return true;
        }

        var offset = GetOverlayOffset(table.Id);
        var header = TableHeaderColumnService.UnionHeader(
            columns.Select(c => ApplyOverlayOffset(c.PdfArea, offset)).ToList());
        var headerPreview = MapPdfToPreview(header, surfaceW, surfaceH, _pageWidthPt, _pageHeightPt);
        if (!headerPreview.Contains(pos))
            return false;

        var pdfPoint = MapPreviewToPdf(
            new Rect(pos.X, pos.Y, 1, 1),
            surfaceW,
            surfaceH,
            _pageWidthPt,
            _pageHeightPt);
        var templateX = pdfPoint.X - offset.Dx;
        var inserted = TableHeaderColumnService.TryInsertDivider(
            columns.Select(c => c.PdfArea).ToList(),
            templateX);
        if (inserted is null)
            return false;

        RebuildTableColumnsFromAreas(table, columns[0].PageIndex, inserted);
        return true;
    }

    /// <summary>Hit-tests column dividers of the expandable table on the current page.</summary>
    private AnchorItem? HitTestTableDividerAt(Point pos, out int dividerIndex, out double dividerPreviewX)
    {
        dividerIndex = -1;
        dividerPreviewX = 0;
        var table = GetTableAnchorForColumnEdit();
        if (table is null)
            return null;

        var (surfaceW, surfaceH) = GetPageSurfaceDipSize();
        if (surfaceW <= 0 || surfaceH <= 0 || _pageWidthPt <= 0 || _pageHeightPt <= 0)
            return null;

        var pageIndex = _currentPage - 1;
        var columns = table.Fields
            .Where(f => f.PageIndex == pageIndex)
            .OrderBy(f => f.PdfArea.X)
            .ToList();
        if (columns.Count < 2)
            return null;

        var offset = GetOverlayOffset(table.Id);
        for (var i = 1; i < columns.Count; i++)
        {
            var left = ApplyOverlayOffset(columns[i - 1].PdfArea, offset);
            var right = ApplyOverlayOffset(columns[i].PdfArea, offset);
            var top = Math.Min(left.Top, right.Top);
            var bottom = Math.Max(left.Bottom, right.Bottom);
            var linePdf = new Rect(right.Left, top, 0.01, Math.Max(0.5, bottom - top));
            var preview = MapPdfToPreview(linePdf, surfaceW, surfaceH, _pageWidthPt, _pageHeightPt);
            if (Math.Abs(pos.X - preview.X) <= TableDividerHitSlopDip
                && pos.Y >= preview.Y - 2
                && pos.Y <= preview.Y + preview.Height + 2)
            {
                dividerIndex = i - 1;
                dividerPreviewX = preview.X;
                return table;
            }
        }

        return null;
    }

    /// <summary>Writes moved template areas back onto ordered field items.</summary>
    private static void ApplyColumnAreasToFields(IReadOnlyList<FieldItem> columnsLeftToRight, IReadOnlyList<Rect> areas)
    {
        var count = Math.Min(columnsLeftToRight.Count, areas.Count);
        for (var i = 0; i < count; i++)
            columnsLeftToRight[i].PdfArea = areas[i];
    }

    /// <summary>Rebuilds table fields from abutting areas; names follow band text.</summary>
    private void RebuildTableColumnsFromAreas(AnchorItem table, int pageIndex, IReadOnlyList<Rect> areas)
    {
        if (areas.Count == 0)
            return;

        var previousRulerName = table.Fields.FirstOrDefault(f => f.IsRulerColumn)?.Name;
        table.Fields.Clear();

        IReadOnlyList<PdfTextGeometry.WordInTopLeft> words = [];
        if (_selected is not null && File.Exists(_selected.FilePath))
        {
            try
            {
                using var document = PdfTextGeometry.OpenReadShared(_selected.FilePath);
                var pageNumber = pageIndex + 1;
                if (pageNumber >= 1 && pageNumber <= document.NumberOfPages)
                    words = PdfTextGeometry.GetOrderedWordsTopLeft(document.GetPage(pageNumber));
            }
            catch
            {
                words = [];
            }
        }

        var offset = GetOverlayOffset(table.Id);
        for (var i = 0; i < areas.Count; i++)
        {
            var displayArea = ApplyOverlayOffset(areas[i], offset);
            var name = TableHeaderColumnService.NameFromWords(words, displayArea);
            if (string.IsNullOrWhiteSpace(name))
                name = Strings.Format(Strings.Extraction_ColumnNameFormat, i + 1);

            table.Fields.Add(new FieldItem(
                index: i + 1,
                name: name,
                value: string.Empty,
                pageIndex: pageIndex,
                pdfArea: areas[i],
                anchorId: table.Id)
            {
                IsRulerColumn = previousRulerName is not null
                    && string.Equals(previousRulerName, name, StringComparison.Ordinal)
            });
        }

        if (table.Fields.Count(f => f.IsRulerColumn) > 1)
        {
            var keep = table.Fields.First(f => f.IsRulerColumn);
            foreach (var f in table.Fields)
                f.IsRulerColumn = ReferenceEquals(f, keep);
        }

        RenumberFields(table);
        MarkTemplateDirty();
        UpdateExtractionHeaders();
        ExpandAnchor(table);
        SelectField(table.Fields[0]);
        RefreshAllFieldExtractedValues();
        RefreshPageOverlays();
    }

    /// <summary>Updates column display names from text currently inside each band.</summary>
}

internal enum FieldResizeHandleKind
{
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
}
