// SPDX-FileCopyrightText: 2026 PlainBytes Studio
// SPDX-License-Identifier: GPL-3.0-only
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfiumViewer;
using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;
using PdfiumDocument = PdfiumViewer.PdfDocument;

namespace PlainBytes.DocumentExtractor;

/// <summary>
/// PdfiumViewer-based page rasterizer (same stack as PlainBytes.Redactor).
/// </summary>
internal sealed class PdfPreviewRenderer : IDisposable
{
    private const double MinDpi = 72.0;
    private const double MaxDpi = 2400.0;
    private const int CacheCapacity = 12;

    private readonly object _gate = new();
    private readonly Dictionary<(string Path, int PageIndex, int Dpi), ImageSource> _cache = new();
    private readonly LinkedList<(string Path, int PageIndex, int Dpi)> _lru = new();

    private PdfiumDocument? _document;
    private string? _boundPath;
    private int _generation;

    public void Bind(string path)
    {
        var normalized = Path.GetFullPath(path.Trim());
        lock (_gate)
        {
            if (_document is not null
                && string.Equals(_boundPath, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            ReleaseLocked();
            _document = PdfiumDocument.Load(normalized);
            _boundPath = normalized;
            _generation++;
        }
    }

    public void Unbind()
    {
        lock (_gate)
            ReleaseLocked();
    }

    public int ReadGeneration()
    {
        lock (_gate)
            return _generation;
    }

    public bool TryGetCached(string path, int pageIndex, double dpi, out ImageSource? image)
    {
        image = null;
        var key = MakeKey(path, pageIndex, dpi);
        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out var cached))
                return false;

            _lru.Remove(key);
            _lru.AddFirst(key);
            image = cached;
            return true;
        }
    }

    public ImageSource RenderPage(string path, int pageIndex, double widthPt, double heightPt, double dpi)
    {
        dpi = Math.Clamp(dpi, MinDpi, MaxDpi);
        var normalized = Path.GetFullPath(path.Trim());

        if (TryGetCached(normalized, pageIndex, dpi, out var cached) && cached is not null)
            return cached;

        var scale = dpi / 72.0;
        var width = Math.Max(1, (int)Math.Ceiling(widthPt * scale));
        var height = Math.Max(1, (int)Math.Ceiling(heightPt * scale));
        const PdfRenderFlags flags = PdfRenderFlags.Annotations | PdfRenderFlags.LcdText;

        Bitmap bitmap;
        lock (_gate)
        {
            if (_document is null
                || !string.Equals(_boundPath, normalized, StringComparison.OrdinalIgnoreCase))
            {
                using var doc = PdfiumDocument.Load(normalized);
                bitmap = (Bitmap)doc.Render(pageIndex, width, height, (float)dpi, (float)dpi, flags);
            }
            else
            {
                bitmap = (Bitmap)_document.Render(pageIndex, width, height, (float)dpi, (float)dpi, flags);
            }
        }

        using (bitmap)
        {
            var image = ToFrozenBitmapSource(bitmap);
            PutCache(normalized, pageIndex, dpi, image);
            return image;
        }
    }

    public void Dispose() => Unbind();

    private void PutCache(string path, int pageIndex, double dpi, ImageSource image)
    {
        var key = MakeKey(path, pageIndex, dpi);
        lock (_gate)
        {
            if (_cache.ContainsKey(key))
            {
                _lru.Remove(key);
                _cache.Remove(key);
            }

            _cache[key] = image;
            _lru.AddFirst(key);

            while (_lru.Count > CacheCapacity)
            {
                var last = _lru.Last!.Value;
                _lru.RemoveLast();
                _cache.Remove(last);
            }
        }
    }

    private void ReleaseLocked()
    {
        _document?.Dispose();
        _document = null;
        _boundPath = null;
        _cache.Clear();
        _lru.Clear();
        _generation++;
    }

    private static (string Path, int PageIndex, int Dpi) MakeKey(string path, int pageIndex, double dpi) =>
        (Path.GetFullPath(path.Trim()), pageIndex, (int)Math.Round(Math.Clamp(dpi, MinDpi, MaxDpi)));

    private static BitmapSource ToFrozenBitmapSource(Bitmap bitmap)
    {
        Bitmap? converted = null;
        try
        {
            var sourceBitmap = bitmap.PixelFormat == GdiPixelFormat.Format32bppArgb
                ? bitmap
                : converted = bitmap.Clone(
                    new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                    GdiPixelFormat.Format32bppArgb);

            var rect = new Rectangle(0, 0, sourceBitmap.Width, sourceBitmap.Height);
            var data = sourceBitmap.LockBits(rect, ImageLockMode.ReadOnly, GdiPixelFormat.Format32bppArgb);
            try
            {
                var stride = data.Stride;
                var bytes = new byte[Math.Abs(stride) * sourceBitmap.Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);

                var writable = new WriteableBitmap(
                    sourceBitmap.Width,
                    sourceBitmap.Height,
                    96,
                    96,
                    PixelFormats.Bgra32,
                    null);
                writable.WritePixels(
                    new Int32Rect(0, 0, sourceBitmap.Width, sourceBitmap.Height),
                    bytes,
                    stride,
                    0);
                writable.Freeze();
                return writable;
            }
            finally
            {
                sourceBitmap.UnlockBits(data);
            }
        }
        finally
        {
            converted?.Dispose();
        }
    }
}
