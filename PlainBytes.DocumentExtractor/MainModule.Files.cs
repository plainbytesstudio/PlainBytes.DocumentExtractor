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
    /// <summary>Empty-state primary: open the PDF file picker.</summary>
    private void ChooseFilesButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        OpenFilePicker();
    }

    /// <summary>Files panel add button: open the PDF file picker.</summary>
    private void AddFilesButton_Click(object sender, RoutedEventArgs e) => OpenFilePicker();

    /// <summary>Idle drop-zone drag enter.</summary>
    private void DropZone_DragEnter(object sender, DragEventArgs e) => UpdateDropZoneDragState(e);

    /// <summary>Idle drop-zone drag over.</summary>
    private void DropZone_DragOver(object sender, DragEventArgs e) => UpdateDropZoneDragState(e);

    /// <summary>Idle drop-zone drag leave.</summary>
    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        _dropZoneDragOver = false;
        ApplyDropZoneVisual(active: false);
        e.Handled = true;
    }

    /// <summary>Idle drop-zone drop: add PDFs.</summary>
    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        _dropZoneDragOver = false;
        ApplyDropZoneVisual(active: false);
        e.Handled = true;
        AddDroppedPaths(e);
    }

    /// <summary>Files column drag enter.</summary>
    private void FilesColumn_DragEnter(object sender, DragEventArgs e) => UpdateFilesColumnDragState(e);

    /// <summary>Files column drag over.</summary>
    private void FilesColumn_DragOver(object sender, DragEventArgs e) => UpdateFilesColumnDragState(e);

    /// <summary>Files column drag leave.</summary>
    private void FilesColumn_DragLeave(object sender, DragEventArgs e)
    {
        if (!IsPointerStillInside(FilesColumn, e))
            HideFilesDropOverlay();
        e.Handled = true;
    }

    /// <summary>Files column drop: add PDFs.</summary>
    private void FilesColumn_Drop(object sender, DragEventArgs e)
    {
        HideFilesDropOverlay();
        e.Handled = true;
        AddDroppedPaths(e);
    }

    /// <summary>Select a PDF in the files list.</summary>
    private void FileItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestorButton(source) is not null)
            return;
        if (sender is FrameworkElement { DataContext: PdfFileItem item })
            SelectItem(item);
    }

    /// <summary>Remove one PDF; reselect neighbor if needed.</summary>
    private void FileItemRemove_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: PdfFileItem item })
            return;
        var index = _files.IndexOf(item);
        var wasSelected = ReferenceEquals(_selected, item);
        _files.Remove(item);
        if (_files.Count == 0)
        {
            _selected = null;
            LoadSelectedDocument(resetPage: true);
            UpdateWorkspaceVisibility();
            return;
        }
        if (wasSelected)
        {
            var nextIndex = Math.Clamp(index, 0, _files.Count - 1);
            SelectItem(_files[nextIndex]);
        }
        UpdateFilesHeader();
    }

    /// <summary>Shows the multi-select PDF open dialog.</summary>
    private void OpenFilePicker()
    {
        var dialog = new OpenFileDialog
        {
            Title = Strings.Dialog_ChoosePdfs,
            Multiselect = true,
            Filter = $"{Strings.Dialog_FilterPdf}|*.pdf|{Strings.Dialog_AllFiles}|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        _ = AddFilesAsync(dialog.FileNames);
    }

    /// <summary>Reads file/folder paths from a drag-drop payload.</summary>
    private void AddDroppedPaths(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
            return;
        _ = AddFilesAsync(paths, expandDropped: paths.Any(Directory.Exists));
    }

    /// <summary>Sync entry: queue AddFilesAsync.</summary>
    private void AddFiles(IEnumerable<string> paths) =>
        _ = AddFilesAsync(paths);

    /// <summary>Scan paths (optional overlay), append PDFs, select first when needed.</summary>
    private async Task AddFilesAsync(IEnumerable<string> paths, bool expandDropped = false)
    {
        var rawPaths = paths as string[] ?? paths.ToArray();
        if (rawPaths.Length == 0)
            return;
        await _fileLoadGate.WaitAsync();
        ShellWindow? shell = null;
        LoadingOverlay? overlay = null;
        try
        {
            var shouldShowOverlay = rawPaths.Length >= LoadingOverlayThreshold
                || (expandDropped && rawPaths.Any(Directory.Exists));
            shell = Window.GetWindow(this) as ShellWindow;
            if (shouldShowOverlay && shell is not null)
            {
                overlay = new LoadingOverlay();
                shell.ShowOverlay(overlay);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            }
            var existingPaths = _files
                .Select(f => f.FilePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            IProgress<(int Done, int Total)>? progress = overlay is null
                ? null
                : new Progress<(int Done, int Total)>(report =>
                    overlay.SetMessage(Strings.Format(Strings.Loading_LoadingPdfs, report.Done, report.Total)));
            var scan = await Task.Run(() =>
                ScanPdfPaths(rawPaths, existingPaths, progress)).ConfigureAwait(true);
            if (scan.SkippedNoTextNames.Count > 0)
                ShowActionToast(
                    BuildNoSelectableTextMessage(scan.SkippedNoTextNames),
                    ActionToastKind.Warning,
                    nearElement: DropZone.Visibility == Visibility.Visible ? DropZone : ExtractButton);
            if (scan.Added.Count == 0)
                return;
            var openedFromEmpty = _files.Count == 0;
            foreach (var item in scan.Added)
                _files.Add(item);
            if (_selected is null || openedFromEmpty)
                SelectItem(scan.Added[0], fitZoomToViewport: openedFromEmpty);
            UpdateFilesHeader();
            UpdateWorkspaceVisibility();
        }
        finally
        {
            shell?.HideOverlay();
            _fileLoadGate.Release();
        }
    }

    /// <summary>Scan result: accepted PDFs plus names skipped for lacking text.</summary>
    private sealed record PdfScanResult(
        List<PdfFileItem> Added,
        List<string> SkippedNoTextNames);

    /// <summary>Background scan: expand folders, skip non-PDF / no-text PDFs.</summary>
    private static PdfScanResult ScanPdfPaths(
        IEnumerable<string> rawPaths,
        HashSet<string> existingPaths,
        IProgress<(int Done, int Total)>? progress)
    {
        var expanded = ExpandPaths(rawPaths).ToList();
        var total = expanded.Count;
        var added = new List<PdfFileItem>();
        var skippedNoTextNames = new List<string>();
        var done = 0;
        foreach (var path in expanded)
        {
            done++;
            progress?.Report((done, total));
            if (!IsPdfPath(path) || existingPaths.Contains(path))
                continue;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                    continue;
                if (!TryOpenPdfHasSelectableText(info.FullName, out var hasText))
                    continue;
                if (!hasText)
                {
                    skippedNoTextNames.Add(info.Name);
                    continue;
                }
                existingPaths.Add(info.FullName);
                added.Add(new PdfFileItem(
                    filePath: info.FullName,
                    fileName: info.Name,
                    metaLine: $"{info.LastWriteTime:yyyy-MM-dd} · {FormatSize(info.Length)}",
                    sizeBytes: info.Length));
            }
            catch
            {
                // Skip unreadable paths.
            }
        }
        return new PdfScanResult(added, skippedNoTextNames);
    }

    /// <summary>Toast copy for PDFs rejected as image-only / no text.</summary>
    private static string BuildNoSelectableTextMessage(IReadOnlyList<string> fileNames)
    {
        var suffix = Strings.Toast_ScannedNotSupported;
        if (fileNames.Count == 1)
            return Strings.Format(Strings.Toast_NoSelectableTextOne, fileNames[0], suffix);
        var list = string.Join("\n", fileNames.Select(n => $"• {n}"));
        return Strings.Format(Strings.Toast_NoSelectableTextMany, fileNames.Count, list, suffix);
    }

    /// <summary>
    /// Opens the PDF and reports whether any page has selectable text.
    /// Returns false if the file cannot be opened as a PDF.
    /// </summary>
    private static bool TryOpenPdfHasSelectableText(string path, out bool hasSelectableText)
    {
        hasSelectableText = false;
        try
        {
            using var document = PdfDocument.Open(path);
            hasSelectableText = PdfTextGeometry.HasSelectableText(document);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Flattens dropped folders into file paths.</summary>
    private static IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*.pdf", SearchOption.TopDirectoryOnly))
                    yield return file;
            }
            else
            {
                yield return path;
            }
        }
    }

    /// <summary>True when path ends with .pdf.</summary>
    private static bool IsPdfPath(string path) =>
        path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Marks the sample PDF and loads its preview.</summary>
    /// <param name="fitZoomToViewport">
    /// True only for the first file opened from an empty list (fit max zoom into preview).
    /// Switching or appending keeps the current zoom ratio.
    /// </param>
    private void SelectItem(PdfFileItem item, bool fitZoomToViewport = false)
    {
        _selected = item;
        foreach (var file in _files)
            file.IsSelected = ReferenceEquals(file, item);
            
        LoadSelectedDocument(resetPage: true, fitZoomToViewport: fitZoomToViewport);
    }

    /// <summary>Open metrics, overlays, field preview values, then async page raster.</summary>
    private void LoadSelectedDocument(bool resetPage, bool fitZoomToViewport = false)
    {
        _pageCount = 1;
        _pageWidthPt = DefaultPageWidthDip / PointToDip;
        _pageHeightPt = DefaultPageHeightDip / PointToDip;
        PdfPageImage.Source = null;
        _previewRenderer.Unbind();
        if (_selected is null || !File.Exists(_selected.FilePath))
        {
            _currentPage = 1;
            HidePdfPageLoading();
            UpdatePreviewChrome();
            ApplyPageSurfaceSize();
            RefreshPageOverlays();
            return;
        }
        ShowPdfPageLoading();

        try
        {
            // Sync open for page metrics only; heavy raster is RefreshPreviewAsync.
            using var document = PdfDocument.Open(_selected.FilePath);
            _pageCount = Math.Max(1, document.NumberOfPages);
            if (resetPage)
                _currentPage = 1;
            else
                _currentPage = Math.Clamp(_currentPage, 1, _pageCount);
            ApplyPageMetrics(document, _currentPage);
            _previewRenderer.Bind(_selected.FilePath);
            if (resetPage && fitZoomToViewport)
                FitZoomToPreviewViewport();
        }
        catch
        {
            _pageCount = 1;
            _currentPage = 1;
        }

        UpdatePreviewChrome();
        ApplyPageSurfaceSize();

        // Overlays + field values block UI; preview raster is async.
        RefreshPageOverlays();
        RefreshAllFieldExtractedValues();
        _ = RefreshPreviewAsync(force: true);
        ScanAnchorsForAmbiguity();
    }

    /// <summary>FILES (n) header text.</summary>
    private void UpdateFilesHeader() =>
        FilesHeaderText.Text = Strings.Format(Strings.Panel_FilesCount, _files.Count);

    /// <summary>Accept file drops on idle zone; update stroke.</summary>
    private void UpdateDropZoneDragState(DragEventArgs e)
    {
        var canDrop = HasPdfDrop(e);

        e.Effects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (canDrop == _dropZoneDragOver)
            return;

        _dropZoneDragOver = canDrop;
        ApplyDropZoneVisual(active: canDrop);
    }

    /// <summary>Accept file drops on files column; show overlay.</summary>
    private void UpdateFilesColumnDragState(DragEventArgs e)
    {
        var canDrop = HasPdfDrop(e);
        e.Effects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        if (canDrop)
            ShowFilesDropOverlay();
        else
            HideFilesDropOverlay();
    }

    /// <summary>Dim files column during drag-over.</summary>
    private void ShowFilesDropOverlay()
    {
        if (_filesColumnDragOver)
            return;

        _filesColumnDragOver = true;
        FilesDropOverlay.Visibility = Visibility.Visible;
    }

    /// <summary>Hide files-column drop overlay.</summary>
    private void HideFilesDropOverlay()
    {
        _filesColumnDragOver = false;
        FilesDropOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>Idle drop-zone fill/stroke for drag-active vs idle.</summary>
    private void ApplyDropZoneVisual(bool active)
    {
        DropZone.Background = active
            ? new SolidColorBrush(Color.FromRgb(0xEF, 0xF6, 0xFF))
            : new SolidColorBrush(Color.FromRgb(0xFA, 0xFC, 0xFF));

        if (DropZoneStroke is Rectangle stroke)
            stroke.Stroke = new SolidColorBrush(
                active ? Color.FromRgb(0x16, 0x77, 0xFF) : Color.FromRgb(0x60, 0xA5, 0xFA));
    }

    /// <summary>True when the drag payload includes a PDF file or a folder.</summary>
    private static bool HasPdfDrop(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return false;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
            return false;

        return paths.Any(p =>
            Directory.Exists(p)
            || p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when the pointer is still inside the element (DragLeave filter).</summary>
    private static bool IsPointerStillInside(FrameworkElement element, DragEventArgs e)
    {
        var pos = e.GetPosition(element);

        return pos.X >= 0
            && pos.Y >= 0
            && pos.X <= element.ActualWidth
            && pos.Y <= element.ActualHeight;
    }
}
