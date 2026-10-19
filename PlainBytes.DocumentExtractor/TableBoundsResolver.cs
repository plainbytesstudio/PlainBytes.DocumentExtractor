// SPDX-FileCopyrightText: 2026 PlainBytes Studio
// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;

namespace PlainBytes.DocumentExtractor;

/// <summary>
/// Step 3 result: how far down the table body extends on one page (top-left coordinates, points).
/// </summary>
/// <param name="BodyTopY">First Y inside the data area (bottom of the column header / user header box).</param>
/// <param name="BodyBottomY">Exclusive lower bound: top of the end anchor, or page height when none.</param>
/// <param name="EndAnchorId">Anchor id whose top became <paramref name="BodyBottomY"/>; null if page bottom.</param>
/// <param name="ColumnSpan">Union of column header boxes (horizontal clip; strips may have gutters between them).</param>
internal readonly record struct TableVerticalBounds(
    double BodyTopY,
    double BodyBottomY,
    string? EndAnchorId,
    Rect ColumnSpan);

/// <summary>Step 3: table body top/bottom from the table anchor and other anchors on the same page.</summary>
internal static class TableBoundsResolver
{
    /// <summary>Tolerance for Y comparisons (PDF float jitter).</summary>
    private const double EpsilonPt = 0.25;

    /// <summary>Anchor phrase located in the current PDF file (top-left box).</summary>
    internal readonly record struct LocatedAnchor(string Id, int PageIndex, Rect LocatedAreaTopLeft);

    /// <summary>
    /// Computes vertical bounds for table extraction and preview overlay.
    /// </summary>
    /// <param name="tableAnchorId">Table anchor id (skipped when picking the end anchor).</param>
    /// <param name="pageIndex">0-based page index.</param>
    /// <param name="tableAnchorLocatedTopLeft">Located table anchor (phrase) box; kept for API compat / validation.</param>
    /// <param name="columnHeaderAreasTopLeft">Per-column header strips (calibrated); horizontal union and body top.</param>
    /// <param name="locatedAnchorsOnPage">All anchors on this page with located boxes (for end-line search).</param>
    /// <param name="pageHeightTopLeft">Page height in top-left space.</param>
    /// <param name="bounds">Output on success.</param>
    /// <returns>False when inputs are invalid or the body height is negligible.</returns>
    public static bool TryResolveVerticalBounds(
        string tableAnchorId,
        int pageIndex,
        Rect tableAnchorLocatedTopLeft,
        IReadOnlyList<Rect> columnHeaderAreasTopLeft,
        IEnumerable<LocatedAnchor> locatedAnchorsOnPage,
        double pageHeightTopLeft,
        out TableVerticalBounds bounds)
    {
        bounds = default;

        if (pageHeightTopLeft <= 0
            || tableAnchorLocatedTopLeft.Width < 0.5
            || tableAnchorLocatedTopLeft.Height < 0.5)
            return false;

        if (columnHeaderAreasTopLeft.Count == 0)
            return false;

        var columnSpan = UnionRects(columnHeaderAreasTopLeft);
        if (columnSpan.Width < 0.5)
            return false;

        // Body starts below the user header / column strips — not the locate-phrase glyph box
        // (phrase is often smaller and sits inside the header, which drew a dashed line through it).
        var bodyTop = columnHeaderAreasTopLeft.Max(h => h.Bottom);
        if (bodyTop >= pageHeightTopLeft - EpsilonPt)
            return false;

        // Default end: page bottom; tighten to the nearest anchor top strictly below bodyTop.
        string? endId = null;
        var bodyBottom = pageHeightTopLeft;

        foreach (var anchor in locatedAnchorsOnPage)
        {
            if (string.Equals(anchor.Id, tableAnchorId, StringComparison.Ordinal))
                continue;
            if (anchor.PageIndex != pageIndex)
                continue;

            var candidateTop = anchor.LocatedAreaTopLeft.Top;
            if (candidateTop <= bodyTop + EpsilonPt)
                continue;

            // Smallest Top among candidates = closest anchor below the table (e.g. totals row).
            if (candidateTop < bodyBottom - EpsilonPt)
            {
                bodyBottom = candidateTop;
                endId = anchor.Id;
            }
        }

        if (bodyBottom <= bodyTop + EpsilonPt)
            return false;

        bounds = new TableVerticalBounds(bodyTop, bodyBottom, endId, columnSpan);
        return true;
    }

    /// <summary>Axis-aligned union of header rectangles (for table horizontal extent).</summary>
    private static Rect UnionRects(IReadOnlyList<Rect> rects)
    {
        var first = rects[0];
        var left = first.Left;
        var top = first.Top;
        var right = first.Right;
        var bottom = first.Bottom;

        for (var i = 1; i < rects.Count; i++)
        {
            var r = rects[i];
            left = Math.Min(left, r.Left);
            top = Math.Min(top, r.Top);
            right = Math.Max(right, r.Right);
            bottom = Math.Max(bottom, r.Bottom);
        }

        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}
