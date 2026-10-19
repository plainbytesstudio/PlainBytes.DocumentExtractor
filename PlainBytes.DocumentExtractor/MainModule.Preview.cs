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
    /// <summary>Go to previous PDF page.</summary>
    private void PrevPageButton_Click(object sender, RoutedEventArgs e) =>
        SetCurrentPage(_currentPage - 1);

    /// <summary>Go to next PDF page.</summary>
    private void NextPageButton_Click(object sender, RoutedEventArgs e) =>
        SetCurrentPage(_currentPage + 1);

    /// <summary>Zoom out one step.</summary>
    private void ZoomOutButton_Click(object sender, RoutedEventArgs e) =>
        SetZoom(PreviousZoomStep(_zoom), debounceRender: false);

    /// <summary>Zoom in one step.</summary>
    private void ZoomInButton_Click(object sender, RoutedEventArgs e) =>
        SetZoom(NextZoomStep(_zoom), debounceRender: false);

    /// <summary>Enter commits the typed page number.</summary>
    private void PageNumberBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        CommitPageNumberInput();
        e.Handled = true;
    }

    /// <summary>Commit page number when the box loses focus.</summary>
    private void PageNumberBox_LostFocus(object sender, RoutedEventArgs e) =>
        CommitPageNumberInput();

    /// <summary>Ctrl+wheel zooms; otherwise default scroll.</summary>
    private void PdfViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;
        e.Handled = true;
        var delta = e.Delta > 0 ? 0.05 : -0.05;
        SetZoom(_zoom + delta, debounceRender: true);
    }

    /// <summary>
    /// Pick the largest ZoomSteps value whose page width and height both fit in the preview viewport.
    /// Used only when opening the first file from an empty workspace.
    /// </summary>
    private void FitZoomToPreviewViewport()
    {
        var viewport = GetPreviewContentViewportSize();
        if (viewport.Width < 8 || viewport.Height < 8)
        {
            // Workspace may not be laid out yet (e.g. first file after empty state).
            Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(() =>
                {
                    if (_selected is null)
                        return;
                    var z = ComputeMaxFitZoom(GetPreviewContentViewportSize());
                    if (Math.Abs(z - _zoom) > 0.001)
                        SetZoom(z, debounceRender: false);
                    else
                    {
                        UpdatePreviewChrome();
                        ApplyPageSurfaceSize();
                    }
                }));
            return;
        }

        _zoom = ComputeMaxFitZoom(viewport);
    }

    /// <summary>Usable DIP size inside PdfViewer (viewport minus padding).</summary>
    private Size GetPreviewContentViewportSize()
    {
        var pad = PdfViewer.Padding;
        var w = PdfViewer.ViewportWidth > 1 ? PdfViewer.ViewportWidth : PdfViewer.ActualWidth;
        var h = PdfViewer.ViewportHeight > 1 ? PdfViewer.ViewportHeight : PdfViewer.ActualHeight;
        if ((w < 8 || h < 8) && PdfViewer.Parent is FrameworkElement parent)
        {
            if (w < 8)
                w = parent.ActualWidth;
            if (h < 8)
                h = parent.ActualHeight;
        }

        w = Math.Max(0, w - pad.Left - pad.Right);
        h = Math.Max(0, h - pad.Top - pad.Bottom);
        return new Size(w, h);
    }

    /// <summary>Largest zoom step that fits both page axes into <paramref name="viewport"/>.</summary>
    private double ComputeMaxFitZoom(Size viewport)
    {
        if (viewport.Width < 8 || viewport.Height < 8 || _pageWidthPt <= 0 || _pageHeightPt <= 0)
            return Math.Clamp(_zoom, ZoomSteps[0], ZoomSteps[^1]);

        var pageW = _pageWidthPt * PointToDip;
        var pageH = _pageHeightPt * PointToDip;
        var best = ZoomSteps[0];
        foreach (var step in ZoomSteps)
        {
            if (pageW * step <= viewport.Width && pageH * step <= viewport.Height)
                best = step;
            else
                break;
        }

        return best;
    }

    /// <summary>Cache current page width/height in PDF points.</summary>
    private void ApplyPageMetrics(PdfDocument document, int pageNumber)
    {
        var page = document.GetPage(pageNumber);
        _pageWidthPt = Math.Max(1, page.Width);
        _pageHeightPt = Math.Max(1, page.Height);
    }

    /// <summary>Change page index, refresh surface size / overlays / raster.</summary>
    private void SetCurrentPage(int page)
    {
        if (_pageCount <= 0)
            return;

        var next = Math.Clamp(page, 1, _pageCount);
        if (next == _currentPage && PageNumberBox.Text == _currentPage.ToString(CultureInfo.InvariantCulture))
        {
            UpdatePreviewChrome();
            return;
        }

        if (_isResizingField)
            EndFieldResize(_resizeStartMouse);

        _currentPage = next;
        if (_selected is not null && File.Exists(_selected.FilePath))
        {
            try
            {
                using var document = PdfDocument.Open(_selected.FilePath);
                ApplyPageMetrics(document, _currentPage);
            }
            catch
            {
                // Keep previous page size on failure.
            }
        }

        UpdatePreviewChrome();
        ApplyPageSurfaceSize();
        RefreshPageOverlays();
        _ = RefreshPreviewAsync(force: true);
    }

    /// <summary>Parse PageNumberBox and navigate.</summary>
    private void CommitPageNumberInput()
    {
        if (!int.TryParse(PageNumberBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var page))
        {
            PageNumberBox.Text = _currentPage.ToString(CultureInfo.InvariantCulture);
            return;
        }
        SetCurrentPage(page);
    }

    /// <summary>Apply zoom; optional debounce before re-render.</summary>
    private void SetZoom(double zoom, bool debounceRender)
    {
        _zoom = Math.Clamp(zoom, ZoomSteps[0], ZoomSteps[^1]);

        UpdatePreviewChrome();
        ApplyPageSurfaceSize();
        RefreshPageOverlays();

        if (debounceRender)
        {
            _zoomRenderTimer.Stop();
            _zoomRenderTimer.Start();
        }
        else
        {
            _zoomRenderTimer.Stop();
            _ = RefreshPreviewAsync(force: true, showLoading: false);
        }
    }

    /// <summary>Sets PdfPageSurface Width/Height from page size × zoom.</summary>
    private void ApplyPageSurfaceSize()
    {
        var (w, h) = GetPageSurfaceDipSize();

        PdfPageSurface.Width = w;
        PdfPageSurface.Height = h;
    }

    /// <summary>
    /// Preview DIP size for the current page/zoom. Prefer this over ActualWidth/Height â
    /// those lag until the next layout pass after Width/Height change (e.g. zoom).
    /// </summary>
    private (double Width, double Height) GetPageSurfaceDipSize()
    {
        if (_pageWidthPt > 0 && _pageHeightPt > 0)
            return (_pageWidthPt * PointToDip * _zoom, _pageHeightPt * PointToDip * _zoom);

        return (PdfPageSurface.ActualWidth, PdfPageSurface.ActualHeight);
    }

    /// <summary>Show the PDF preview loading mask.</summary>
    private void ShowPdfPageLoading()
    {
        PdfViewerLoadingHost.Opacity = 1;
        PdfViewerLoadingHost.IsHitTestVisible = true;
        PdfViewerLoadingHost.Visibility = Visibility.Visible;
    }

    /// <summary>Hide the PDF preview loading mask.</summary>
    private void HidePdfPageLoading()
    {
        PdfViewerLoadingHost.Opacity = 0;
        PdfViewerLoadingHost.IsHitTestVisible = false;
    }

    /// <summary>Rasterize current page off the UI thread (cancellable).</summary>
    /// <param name="force">Ignore cache and re-render.</param>
    /// <param name="showLoading">
    /// When false (e.g. zoom), keep the previous bitmap visible without the loading mask.
    /// </param>
    private async Task RefreshPreviewAsync(bool force, bool showLoading = true)
    {
        if (_selected is null || !File.Exists(_selected.FilePath))
        {
            PdfPageImage.Source = null;
            HidePdfPageLoading();
            return;
        }

        var path = _selected.FilePath;
        var pageIndex = _currentPage - 1;
        var widthPt = _pageWidthPt;
        var heightPt = _pageHeightPt;

        var dpi = Math.Clamp(BaseRenderDpi * _zoom, 72.0, 2400.0);

        // Cache hit: skip Pdfium and hide loading immediately.
        if (!force
            && _previewRenderer.TryGetCached(path, pageIndex, dpi, out var cached)
            && cached is not null)
        {
            PdfPageImage.Source = cached;
            HidePdfPageLoading();
            return;
        }

        var requestId = ++_renderRequestId;

        if (showLoading)
            ShowPdfPageLoading();

        // Cancel any in-flight render when the user flips page/zoom quickly.
        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();

        var ct = _renderCts.Token;
        try
        {
            var image = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                return _previewRenderer.RenderPage(path, pageIndex, widthPt, heightPt, dpi);
            }, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested || requestId != _renderRequestId)
                return;
            PdfPageImage.Source = image;
            RefreshPageOverlays();
        }
        catch (OperationCanceledException)
        {
            // superseded
        }
        catch
        {
            if (requestId == _renderRequestId)
                PdfPageImage.Source = null;
        }
        finally
        {
            if (requestId == _renderRequestId)
                HidePdfPageLoading();
        }
    }

    /// <summary>Page number, zoom %, and nav/zoom button enablement.</summary>
    private void UpdatePreviewChrome()
    {
        PageNumberBox.Text = _currentPage.ToString(CultureInfo.InvariantCulture);
        PageCountText.Text = _pageCount.ToString(CultureInfo.InvariantCulture);
        ZoomPercentText.Text = $"{(int)Math.Round(_zoom * 100)}%";
        PrevPageButton.IsEnabled = _currentPage > 1;
        NextPageButton.IsEnabled = _currentPage < _pageCount;
        ZoomOutButton.IsEnabled = _zoom > ZoomSteps[0] + 0.001;
        ZoomInButton.IsEnabled = _zoom < ZoomSteps[^1] - 0.001;
    }

    /// <summary>
    /// Switches to the region's page if needed, then scrolls the PDF preview so the area is visible and highlighted.
    /// </summary>
    private void NavigateToPdfRegion(int pageIndex, Rect pdfArea)
    {
        var page = pageIndex + 1;

        if (page != _currentPage)
            SetCurrentPage(page);

        Dispatcher.BeginInvoke(
            () => ScrollPreviewToPdfArea(pdfArea),
            DispatcherPriority.Loaded);
    }

    /// <summary>Scrolls the preview so the PDF region center is in view.</summary>
    private void ScrollPreviewToPdfArea(Rect pdfArea)
    {
        if (PdfViewer.Content is not FrameworkElement content)
            return;

        var (surfaceW, surfaceH) = GetPageSurfaceDipSize();
        if (surfaceW <= 0 || surfaceH <= 0 || _pageWidthPt <= 0 || _pageHeightPt <= 0)
            return;

        var preview = MapPdfToPreview(pdfArea, surfaceW, surfaceH, _pageWidthPt, _pageHeightPt);
        var centerOnSurface = new Point(
            preview.X + preview.Width / 2,
            preview.Y + preview.Height / 2);
        Point centerInContent;

        try
        {
            centerInContent = PdfPageSurface.TranslatePoint(centerOnSurface, content);
        }
        catch
        {
            return;
        }

        var x = centerInContent.X - PdfViewer.ViewportWidth / 2;
        var y = centerInContent.Y - PdfViewer.ViewportHeight / 2;

        PdfViewer.ScrollToHorizontalOffset(Math.Clamp(x, 0, Math.Max(0, PdfViewer.ScrollableWidth)));
        PdfViewer.ScrollToVerticalOffset(Math.Clamp(y, 0, Math.Max(0, PdfViewer.ScrollableHeight)));
    }

    /// <summary>Next entry in ZoomSteps.</summary>
    private static double NextZoomStep(double current)
    {
        foreach (var step in ZoomSteps)
        {
            if (step > current + 0.001)
                return step;
        }
        return ZoomSteps[^1];
    }

    /// <summary>Previous entry in ZoomSteps.</summary>
    private static double PreviousZoomStep(double current)
    {
        for (var i = ZoomSteps.Length - 1; i >= 0; i--)
        {
            if (ZoomSteps[i] < current - 0.001)
                return ZoomSteps[i];
        }
        return ZoomSteps[0];
    }
}
