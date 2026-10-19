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
    /// <summary>Redraws anchor/field/table-bound overlays for the current page.</summary>
    /// <param name="resolveGeometry">
    /// When false, reuse cached overlay offsets (live divider/resize drag) and skip PDF reopen.
    /// </param>
    private void RefreshPageOverlays(bool resolveGeometry = true)
    {
        FieldOverlayCanvas.Children.Clear();

        var (surfaceW, surfaceH) = GetPageSurfaceDipSize();
        if (surfaceW <= 0 || surfaceH <= 0 || _pageWidthPt <= 0 || _pageHeightPt <= 0)
            return;

        var pageIndex = _currentPage - 1;
        Dictionary<string, Rect> locatedAnchors;
        if (resolveGeometry || _overlayOffsetsByAnchorId.Count == 0)
        {
            TryResolveOverlayGeometry(out locatedAnchors, out _overlayOffsetsByAnchorId);
        }
        else
        {
            locatedAnchors = new Dictionary<string, Rect>(StringComparer.Ordinal);
        }

        foreach (var anchor in _anchors)
        {
            if (anchor.PageIndex != pageIndex)
                continue;

            // Table with column header configured: header band is the primary frame.
            if (anchor.Kind == AnchorKind.Table
                && anchor.IsExpanded
                && anchor.Fields.Count > 0
                && !anchor.HeaderPdfArea.IsEmpty)
                continue;

            Rect area;
            if (anchor.Kind == AnchorKind.Table)
            {
                // Outer header + phrase offset (same offset applied to columns when expanded).
                var outer = anchor.HeaderPdfArea.Width > 0.5 && anchor.HeaderPdfArea.Height > 0.5
                    ? anchor.HeaderPdfArea
                    : anchor.PdfArea;
                area = ApplyOverlayOffset(outer, GetOverlayOffset(anchor.Id));
            }
            else if (!locatedAnchors.TryGetValue(anchor.Id, out area))
            {
                // Cached-offset path (no PDF resolve): fall back to template + offset.
                area = ApplyOverlayOffset(anchor.PdfArea, GetOverlayOffset(anchor.Id));
            }

            var preview = MapPdfToPreview(area, surfaceW, surfaceH, _pageWidthPt, _pageHeightPt);
            if (preview.Width < 1 || preview.Height < 1)
                continue;

            FieldOverlayCanvas.Children.Add(CreateAnchorOverlay(preview, anchor.IsSelected));
        }
        foreach (var field in EnumerateFields())
        {
            if (field.PageIndex != pageIndex)
                continue;

            var owner = _anchors.FirstOrDefault(a => a.Id == field.AnchorId);
            // Table columns: one outer header box (user rubber-band) + dividers — not per-column frames.
            if (owner is { Kind: AnchorKind.Table })
                continue;

            var area = ApplyOverlayOffset(field.PdfArea, GetOverlayOffset(field.AnchorId));
            var preview = MapPdfToPreview(area, surfaceW, surfaceH, _pageWidthPt, _pageHeightPt);
            var stroke = Color.FromRgb(0xE5, 0x48, 0x4D);
            var fill = field.IsSelected
                ? Color.FromArgb(0x28, 0xE5, 0x48, 0x4D)
                : Color.FromArgb(0x18, 0xE5, 0x48, 0x4D);
            var thickness = field.IsSelected ? 2.0 : 1.0;

            FieldOverlayCanvas.Children.Add(CreateFieldOverlay(preview, stroke, fill, thickness, field.IsSelected));
        }

        DrawTableHeaderBandOverlays(pageIndex, surfaceW, surfaceH);
        DrawTableColumnDividerOverlays(pageIndex, surfaceW, surfaceH);
    }

    /// <summary>
    /// Draws the table header as the exact user-selected outer rectangle (never text-fitted),
    /// with a light highlight on the selected column strip.
    /// </summary>
    private void DrawTableHeaderBandOverlays(int pageIndex, double surfaceW, double surfaceH)
    {
        foreach (var tableAnchor in _anchors)
        {
            if (tableAnchor.Kind != AnchorKind.Table || !tableAnchor.IsExpanded)
                continue;

            var columns = tableAnchor.Fields
                .Where(f => f.PageIndex == pageIndex)
                .OrderBy(f => f.PdfArea.X)
                .ToList();
            if (columns.Count == 0)
                continue;

            var offset = GetOverlayOffset(tableAnchor.Id);

            // Prefer the stored user rubber-band; fall back to column union for older sessions.
            Rect headerTemplate = tableAnchor.HeaderPdfArea;
            if (headerTemplate.IsEmpty || headerTemplate.Width < 0.5 || headerTemplate.Height < 0.5)
                headerTemplate = TableHeaderColumnService.UnionHeader(columns.Select(c => c.PdfArea).ToList());

            var headerDisplay = ApplyOverlayOffset(headerTemplate, offset);
            if (headerDisplay.Width < 0.5 || headerDisplay.Height < 0.5)
                continue;

            var headerPreview = MapPdfToPreview(headerDisplay, surfaceW, surfaceH, _pageWidthPt, _pageHeightPt);
            var anySelected = columns.Any(c => c.IsSelected) || tableAnchor.IsSelected;
            var stroke = Color.FromRgb(0xE5, 0x48, 0x4D);
            var fill = Color.FromArgb(0x14, 0xE5, 0x48, 0x4D);
            var thickness = anySelected ? 2.0 : 1.5;

            // Expanded tables skip CreateAnchorOverlay (dashed+pin); keep pin on the header band.
            var headerOverlay = CreateFieldOverlay(headerPreview, stroke, fill, thickness, showResizeHandles: false);
            if (headerOverlay is Canvas headerCanvas)
                AttachAnchorPinBadge(headerCanvas);
            FieldOverlayCanvas.Children.Add(headerOverlay);

            var selected = columns.FirstOrDefault(c => c.IsSelected);
            if (selected is null)
                continue;

            var selectedPreview = MapPdfToPreview(
                ApplyOverlayOffset(selected.PdfArea, offset),
                surfaceW,
                surfaceH,
                _pageWidthPt,
                _pageHeightPt);
            FieldOverlayCanvas.Children.Add(
                CreateFieldOverlay(
                    selectedPreview,
                    Color.FromRgb(0xE5, 0x48, 0x4D),
                    Color.FromArgb(0x28, 0xE5, 0x48, 0x4D),
                    0,
                    showResizeHandles: false,
                    tag: new TableSelectedColOverlayTag(tableAnchor.Id)));
        }
    }

    /// <summary>
    /// Vertical dividers between table columns (expanded table anchor). Drag to recalibrate column X bands.
    /// </summary>
    private void DrawTableColumnDividerOverlays(int pageIndex, double surfaceW, double surfaceH)
    {
        foreach (var tableAnchor in _anchors)
        {
            if (tableAnchor.Kind != AnchorKind.Table
                || !tableAnchor.IsExpanded
                || tableAnchor.Fields.Count < 2)
                continue;

            var columns = tableAnchor.Fields
                .Where(f => f.PageIndex == pageIndex)
                .OrderBy(f => f.PdfArea.X)
                .ToList();
            if (columns.Count < 2)
                continue;

            var offset = GetOverlayOffset(tableAnchor.Id);

            // Divider height follows the user header box when available.
            Rect headerTemplate = tableAnchor.HeaderPdfArea;
            if (headerTemplate.IsEmpty || headerTemplate.Width < 0.5)
                headerTemplate = TableHeaderColumnService.UnionHeader(columns.Select(c => c.PdfArea).ToList());
            var headerDisplay = ApplyOverlayOffset(headerTemplate, offset);

            for (var i = 1; i < columns.Count; i++)
            {
                var right = ApplyOverlayOffset(columns[i].PdfArea, offset);
                var dividerX = right.Left;
                var linePdf = new Rect(
                    dividerX,
                    headerDisplay.Top,
                    0.01,
                    Math.Max(0.5, headerDisplay.Height));
                var preview = MapPdfToPreview(linePdf, surfaceW, surfaceH, _pageWidthPt, _pageHeightPt);
                FieldOverlayCanvas.Children.Add(
                    CreateTableDividerOverlay(preview, new TableDividerOverlayTag(tableAnchor.Id, i - 1)));
            }
        }
    }

    /// <summary>Visual-only vertical divider handle for table column boundaries.</summary>
    private static UIElement CreateTableDividerOverlay(Rect preview, object? tag = null)
    {
        var x = preview.X;
        var y = preview.Y;
        var h = Math.Max(8, preview.Height);
        var root = new Canvas
        {
            Width = 12,
            Height = h,
            IsHitTestVisible = false,
            ClipToBounds = false,
            Tag = tag,
        };

        var line = new Line
        {
            X1 = 6,
            X2 = 6,
            Y1 = 0,
            Y2 = h,
            Stroke = FreezeBrush(Color.FromRgb(0x0D, 0x94, 0x88)),
            StrokeThickness = 2,
            IsHitTestVisible = false,
        };
        root.Children.Add(line);

        var grip = new System.Windows.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = FreezeBrush(Color.FromRgb(0x0D, 0x94, 0x88)),
            Stroke = Brushes.White,
            StrokeThickness = 1,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(grip, 2);
        Canvas.SetTop(grip, Math.Max(0, (h - 8) / 2));
        root.Children.Add(grip);

        Canvas.SetLeft(root, x - 6);
        Canvas.SetTop(root, y);
        return root;
    }

    /// <summary>
    /// Locates every anchor phrase in the current file and stores per-anchor offsets
    /// (found − template). Current-page located boxes are returned for overlay drawing.
    /// </summary>
    private bool TryResolveOverlayGeometry(
        out Dictionary<string, Rect> locatedAnchorsOnCurrentPage,
        out Dictionary<string, (double Dx, double Dy)> offsetsByAnchorId)
    {
        locatedAnchorsOnCurrentPage = new Dictionary<string, Rect>(StringComparer.Ordinal);
        offsetsByAnchorId = new Dictionary<string, (double Dx, double Dy)>(StringComparer.Ordinal);
        if (_selected is null || !File.Exists(_selected.FilePath) || _anchors.Count == 0)
            return false;

        try
        {
            using var document = PdfTextGeometry.OpenReadShared(_selected.FilePath);
            ResolveAnchorOffsets(document, locatedAnchorsOnCurrentPage, offsetsByAnchorId);
            return true;
        }
        catch
        {
            locatedAnchorsOnCurrentPage = new Dictionary<string, Rect>(StringComparer.Ordinal);
            offsetsByAnchorId = new Dictionary<string, (double Dx, double Dy)>(StringComparer.Ordinal);
            return false;
        }
    }

    /// <summary>Lazy-fill offsets if overlays were not resolved yet.</summary>
    private void EnsureOverlayOffsetsResolved(PdfDocument document)
    {
        if (_overlayOffsetsByAnchorId.Count > 0 || _anchors.Count == 0)
            return;

        ResolveAnchorOffsets(
            document,
            locatedOnCurrentPage: null,
            _overlayOffsetsByAnchorId);
    }

    /// <summary>
    /// Locate each anchor phrase → inner box on the current PDF → offset vs template inner box.
    /// Value: offset applied to fields. Table: offset applied to header outer box + columns.
    /// </summary>
    private void ResolveAnchorOffsets(
        PdfDocument document,
        Dictionary<string, Rect>? locatedOnCurrentPage,
        Dictionary<string, (double Dx, double Dy)> offsetsByAnchorId)
    {
        var pageIndex = _currentPage - 1;
        foreach (var anchor in _anchors)
        {
            var pageNumber = anchor.PageIndex + 1;
            if (pageNumber < 1 || pageNumber > document.NumberOfPages)
                continue;

            var page = document.GetPage(pageNumber);
            var innerSample = GetInnerPhraseSample(anchor);
            var preferNearPdf = PdfTextGeometry.ToPdfUserSpace(innerSample, page.Height);
            if (!PdfTextGeometry.TryFindPhraseBounds(
                    page,
                    anchor.Text,
                    preferNear: preferNearPdf,
                    out var foundPdf))
                continue;

            var foundInner = PdfTextGeometry.FromPdfUserSpace(foundPdf, page.Height);
            if (foundInner.Width < 0.5 || foundInner.Height < 0.5)
                continue;

            // offset = new inner top-left − template inner top-left
            offsetsByAnchorId[anchor.Id] = (
                foundInner.X - innerSample.X,
                foundInner.Y - innerSample.Y);

            if (locatedOnCurrentPage is not null && anchor.PageIndex == pageIndex)
                locatedOnCurrentPage[anchor.Id] = foundInner;
        }
    }

    /// <summary>template rect + (dx,dy) → display rect.</summary>
    private static Rect ApplyOverlayOffset(Rect templateArea, (double Dx, double Dy) offset) =>
        new(
            templateArea.X + offset.Dx,
            templateArea.Y + offset.Dy,
            templateArea.Width,
            templateArea.Height);

    /// <summary>display rect − (dx,dy) → template rect for persistence.</summary>
    private static Rect RemoveOverlayOffset(Rect displayArea, (double Dx, double Dy) offset) =>
        new(
            displayArea.X - offset.Dx,
            displayArea.Y - offset.Dy,
            displayArea.Width,
            displayArea.Height);

    /// <summary>Dashed pin overlay for an anchor phrase box.</summary>
    private static UIElement CreateAnchorOverlay(Rect preview, bool isSelected)
    {
        var stroke = Color.FromRgb(0xE5, 0x48, 0x4D);
        var fill = isSelected
            ? Color.FromArgb(0x28, 0xE5, 0x48, 0x4D)
            : Color.FromArgb(0x08, 0xE5, 0x48, 0x4D);
        var thickness = isSelected ? 2.0 : 1.0;
        var root = new Canvas
        {
            Width = Math.Max(0, preview.Width),
            Height = Math.Max(0, preview.Height),
            IsHitTestVisible = false,
            ClipToBounds = false,
        };
        var box = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(0, preview.Width),
            Height = Math.Max(0, preview.Height),
            Stroke = new SolidColorBrush(stroke),
            StrokeThickness = thickness,
            StrokeDashArray = new DoubleCollection { 2, 2 },
            Fill = new SolidColorBrush(fill),
            IsHitTestVisible = false,
        };

        Canvas.SetLeft(box, 0);
        Canvas.SetTop(box, 0);
        root.Children.Add(box);
        AttachAnchorPinBadge(root);
        Canvas.SetLeft(root, preview.X);
        Canvas.SetTop(root, preview.Y);
        return root;
    }

    /// <summary>Pin badge at the top-left of an anchor / table-header overlay.</summary>
    private static void AttachAnchorPinBadge(Canvas root)
    {
        // Solid badge + Fluent glyph: emoji often renders as tofu/blank on Segoe.
        var badge = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            Child = new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = Wpf.Ui.Controls.SymbolRegular.Pin24,
                FontSize = 11,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        Canvas.SetLeft(badge, -1);
        Canvas.SetTop(badge, -17);
        root.Children.Add(badge);
    }

    /// <summary>Filled rect (+ knobs when selected) for a field region.</summary>
    private static UIElement CreateFieldOverlay(
        Rect preview,
        Color stroke,
        Color fill,
        double strokeThickness,
        bool showResizeHandles,
        object? tag = null)
    {
        var root = new Canvas
        {
            Width = Math.Max(0, preview.Width),
            Height = Math.Max(0, preview.Height),
            IsHitTestVisible = false,
            ClipToBounds = false,
            Tag = tag,
        };
        var box = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(0, preview.Width),
            Height = Math.Max(0, preview.Height),
            Stroke = new SolidColorBrush(stroke),
            StrokeThickness = strokeThickness,
            Fill = new SolidColorBrush(fill),
            IsHitTestVisible = false,
        };

        Canvas.SetLeft(box, 0);
        Canvas.SetTop(box, 0);

        root.Children.Add(box);
        if (showResizeHandles)
            AddFieldResizeKnobs(root, preview.Width, preview.Height, stroke);

        Canvas.SetLeft(root, preview.X);
        Canvas.SetTop(root, preview.Y);
        return root;
    }

    /// <summary>Visio-style 8 knobs (same layout as Redactor marks). Hit-testing is done on SelectionLayer.</summary>
    private static void AddFieldResizeKnobs(Canvas root, double width, double height, Color border)
    {
        var chrome = new Grid
        {
            Width = Math.Max(0, width),
            Height = Math.Max(0, height),
            ClipToBounds = false,
            IsHitTestVisible = false,
        };
        var half = FieldResizeKnobSize / 2;
        var brush = new SolidColorBrush(border);
        brush.Freeze();

        void AddKnob(HorizontalAlignment h, VerticalAlignment v, Thickness margin)
        {
            chrome.Children.Add(new Border
            {
                Width = FieldResizeKnobSize,
                Height = FieldResizeKnobSize,
                HorizontalAlignment = h,
                VerticalAlignment = v,
                Margin = margin,
                Background = Brushes.White,
                BorderBrush = brush,
                BorderThickness = new Thickness(1),
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
                IsHitTestVisible = false,
            });
        }

        AddKnob(HorizontalAlignment.Center, VerticalAlignment.Top, new Thickness(0, -half, 0, 0));
        AddKnob(HorizontalAlignment.Center, VerticalAlignment.Bottom, new Thickness(0, 0, 0, -half));
        AddKnob(HorizontalAlignment.Left, VerticalAlignment.Center, new Thickness(-half, 0, 0, 0));
        AddKnob(HorizontalAlignment.Right, VerticalAlignment.Center, new Thickness(0, 0, -half, 0));
        AddKnob(HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(-half, -half, 0, 0));
        AddKnob(HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0, -half, -half, 0));
        AddKnob(HorizontalAlignment.Left, VerticalAlignment.Bottom, new Thickness(-half, 0, 0, -half));
        AddKnob(HorizontalAlignment.Right, VerticalAlignment.Bottom, new Thickness(0, 0, -half, -half));
        Canvas.SetLeft(chrome, 0);

        Canvas.SetTop(chrome, 0);
        root.Children.Add(chrome);
    }

    /// <summary>Creates a frozen SolidColorBrush.</summary>
    private static SolidColorBrush FreezeBrush(Color color)
    {
        var brush = new SolidColorBrush(color);

        brush.Freeze();
        return brush;
    }

    /// <summary>Preview DIP rect → PDF top-left points.</summary>
    private static Rect MapPreviewToPdf(
        Rect preview,
        double surfaceW,
        double surfaceH,
        double pageWidthPt,
        double pageHeightPt)
    {
        var scaleX = surfaceW / pageWidthPt;
        var scaleY = surfaceH / pageHeightPt;

        // Stored PDF region: origin top-left (Y grows downward from page top).
        var pdfX = preview.X / scaleX;
        var pdfY = preview.Y / scaleY;
        var pdfW = preview.Width / scaleX;
        var pdfH = preview.Height / scaleY;

        return new Rect(pdfX, pdfY, pdfW, pdfH);
    }

    /// <summary>PDF top-left points → preview DIP rect.</summary>
    private static Rect MapPdfToPreview(
        Rect pdf,
        double surfaceW,
        double surfaceH,
        double pageWidthPt,
        double pageHeightPt)
    {
        var scaleX = surfaceW / pageWidthPt;
        var scaleY = surfaceH / pageHeightPt;
        var x = pdf.X * scaleX;
        var y = pdf.Y * scaleY;
        var w = pdf.Width * scaleX;
        var h = pdf.Height * scaleY;
        return new Rect(x, y, w, h);
    }
    private static (string Text, Rect? Bounds) ExtractTextAndBounds(string path, int pageNumber1Based, Rect pdfArea)
    {
        try
        {
            using var document = PdfTextGeometry.OpenReadShared(path);
            return ExtractTextAndBounds(document, pageNumber1Based, pdfArea);
        }
        catch
        {
            return (string.Empty, null);
        }
    }
    private static (string Text, Rect? Bounds) ExtractTextAndBounds(
        PdfDocument document,
        int pageNumber1Based,
        Rect pdfArea)
    {
        try
        {
            if (pageNumber1Based < 1 || pageNumber1Based > document.NumberOfPages)
                return (string.Empty, null);

            var page = document.GetPage(pageNumber1Based);

            // UI/template store top-left; PdfPig intersection uses bottom-left user space.
            var pdfUser = PdfTextGeometry.ToPdfUserSpace(pdfArea, page.Height);
            var (text, bounds) = PdfTextGeometry.ExtractIntersectingTextAndBounds(page, pdfUser);
            if (bounds is null)
                return (text, null);

            return (text, PdfTextGeometry.FromPdfUserSpace(bounds.Value, page.Height));
        }
        catch
        {
            return (string.Empty, null);
        }
    }

    /// <summary>Identifies a table column divider overlay for live drag updates.</summary>
    private sealed record TableDividerOverlayTag(string AnchorId, int DividerIndex);

    /// <summary>Identifies the selected table-column highlight overlay for live drag updates.</summary>
    private sealed record TableSelectedColOverlayTag(string AnchorId);
}
