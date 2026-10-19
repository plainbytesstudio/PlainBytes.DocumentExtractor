// SPDX-FileCopyrightText: 2026 PlainBytes Studio
// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using PlainBytes.DocumentExtractor.Resources;

namespace PlainBytes.DocumentExtractor;

/// <summary>
/// Table header column geometry: left-packed default dividers, manual drag helpers,
/// and names taken from text inside each band. Outer box is always the user rubber-band.
/// </summary>
internal static class TableHeaderColumnService
{
    /// <summary>Minimum column width in PDF points (template space).</summary>
    public const double MinColumnWidthPt = 8.0;

    /// <summary>One column strip: abutting template rect + display name from band text.</summary>
    public readonly record struct ColumnBand(Rect Area, string Name);

    /// <summary>
    /// Clusters words inside <paramref name="headerTopLeft"/> by horizontal gaps and returns
    /// abutting bands that cover the full header width (no gutters).
    /// </summary>
    public static IReadOnlyList<ColumnBand> SplitHeader(
        IReadOnlyList<PdfTextGeometry.WordInTopLeft> pageWords,
        Rect headerTopLeft)
    {
        if (headerTopLeft.Width < MinColumnWidthPt || headerTopLeft.Height < 0.5)
            return [];

        var words = pageWords
            .Where(w => IntersectsHeader(w.Area, headerTopLeft))
            .OrderBy(w => w.Area.Left)
            .ToList();

        if (words.Count == 0)
            return [];

        var clusters = ClusterByHorizontalGaps(words);
        if (clusters.Count == 0)
            return [];

        if (clusters.Count == 1)
        {
            var name = JoinClusterName(clusters[0]);
            return
            [
                new ColumnBand(headerTopLeft, string.IsNullOrWhiteSpace(name) ? Strings.Format(Strings.Extraction_ColumnNameFormat, 1) : name)
            ];
        }

        var clusterBounds = clusters
            .Select(c =>
            {
                var left = c.Min(w => w.Area.Left);
                var right = c.Max(w => w.Area.Right);
                return (Left: left, Right: right, Words: c);
            })
            .ToList();

        var dividers = new List<double>(clusterBounds.Count - 1);
        for (var i = 0; i < clusterBounds.Count - 1; i++)
        {
            var mid = (clusterBounds[i].Right + clusterBounds[i + 1].Left) / 2.0;
            dividers.Add(mid);
        }

        return BuildBandsFromDividers(
            headerTopLeft,
            dividers,
            i => JoinClusterName(clusterBounds[i].Words));
    }

    /// <summary>
    /// Creates exactly <paramref name="columnCount"/> abutting columns with N−1 dividers
    /// packed to the left. Does not reduce N when the header is narrow — step shrinks instead.
    /// </summary>
    public static IReadOnlyList<ColumnBand> CreateLeftPackedColumns(
        Rect headerTopLeft,
        int columnCount,
        Func<int, Rect, string> nameForColumn)
    {
        if (headerTopLeft.Width < 1.0 || headerTopLeft.Height < 0.5)
            return [];

        var n = Math.Clamp(columnCount, 2, 40);

        // First N−1 columns share a left-packed step; the last column takes the remainder.
        // Shrink step if needed so all N columns still fit (never drop dividers).
        var step = Math.Min(MinColumnWidthPt, (headerTopLeft.Width - 1.0) / n);
        if (step < 0.5)
            step = headerTopLeft.Width / n;

        var result = new List<ColumnBand>(n);
        for (var i = 0; i < n; i++)
        {
            var left = i == 0 ? headerTopLeft.Left : headerTopLeft.Left + i * step;
            var right = i == n - 1
                ? headerTopLeft.Right
                : headerTopLeft.Left + (i + 1) * step;
            var area = new Rect(left, headerTopLeft.Top, Math.Max(0.5, right - left), headerTopLeft.Height);
            var name = nameForColumn(i, area);
            if (string.IsNullOrWhiteSpace(name))
                name = Strings.Format(Strings.Extraction_ColumnNameFormat, i + 1);
            result.Add(new ColumnBand(area, name));
        }

        return result;
    }

    /// <summary>
    /// Builds abutting bands from divider X positions (template space), clamped inside the header.
    /// </summary>
    public static IReadOnlyList<ColumnBand> BuildBandsFromDividers(
        Rect headerTopLeft,
        IReadOnlyList<double> dividerXs,
        Func<int, string> nameForColumnIndex)
    {
        var ordered = dividerXs
            .Where(x => x > headerTopLeft.Left + MinColumnWidthPt
                        && x < headerTopLeft.Right - MinColumnWidthPt)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        var edges = new List<double> { headerTopLeft.Left };
        edges.AddRange(ordered);
        edges.Add(headerTopLeft.Right);

        // Drop edges that would create a band narrower than the minimum.
        for (var i = 1; i < edges.Count; i++)
        {
            if (edges[i] - edges[i - 1] >= MinColumnWidthPt)
                continue;
            if (i == edges.Count - 1)
                edges[i - 1] = edges[i] - MinColumnWidthPt;
            else
                edges.RemoveAt(i--);
        }

        if (edges.Count < 2)
            return [new ColumnBand(headerTopLeft, nameForColumnIndex(0))];

        var bands = new List<ColumnBand>(edges.Count - 1);
        for (var i = 0; i < edges.Count - 1; i++)
        {
            var left = edges[i];
            var right = edges[i + 1];
            var area = new Rect(left, headerTopLeft.Top, right - left, headerTopLeft.Height);
            var name = nameForColumnIndex(i);
            if (string.IsNullOrWhiteSpace(name))
                name = Strings.Format(Strings.Extraction_ColumnNameFormat, i + 1);
            bands.Add(new ColumnBand(area, name));
        }

        return bands;
    }

    /// <summary>Divider X positions between abutting column areas (left edges of columns 1..n-1).</summary>
    public static IReadOnlyList<double> GetDividerXs(IReadOnlyList<Rect> columnAreasLeftToRight)
    {
        if (columnAreasLeftToRight.Count < 2)
            return [];

        var ordered = columnAreasLeftToRight.OrderBy(a => a.X).ToList();
        var dividers = new List<double>(ordered.Count - 1);
        for (var i = 1; i < ordered.Count; i++)
            dividers.Add(ordered[i].Left);
        return dividers;
    }

    /// <summary>Union rect of column bands (header outer box).</summary>
    public static Rect UnionHeader(IReadOnlyList<Rect> columnAreas)
    {
        if (columnAreas.Count == 0)
            return Rect.Empty;

        var left = columnAreas.Min(a => a.Left);
        var top = columnAreas.Min(a => a.Top);
        var right = columnAreas.Max(a => a.Right);
        var bottom = columnAreas.Max(a => a.Bottom);
        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>Name from words whose centers fall in the band.</summary>
    public static string NameFromWords(
        IReadOnlyList<PdfTextGeometry.WordInTopLeft> pageWords,
        Rect bandTopLeft)
    {
        var parts = pageWords
            .Where(w => IntersectsHeader(w.Area, bandTopLeft))
            .OrderBy(w => w.Area.Left)
            .Select(w => w.Text.Trim())
            .Where(t => t.Length > 0);
        return PdfTextGeometry.NormalizePhrase(string.Join(' ', parts));
    }

    /// <summary>
    /// Moves divider <paramref name="dividerIndex"/> (0 = between col0 and col1) to <paramref name="newX"/>,
    /// keeping abutting bands and min widths.
    /// </summary>
    public static IReadOnlyList<Rect> MoveDivider(
        IReadOnlyList<Rect> columnAreasLeftToRight,
        int dividerIndex,
        double newX)
    {
        var ordered = columnAreasLeftToRight.OrderBy(a => a.X).ToList();
        if (dividerIndex < 0 || dividerIndex >= ordered.Count - 1)
            return ordered;

        var header = UnionHeader(ordered);
        var leftLimit = ordered[dividerIndex].Left + MinColumnWidthPt;
        var rightLimit = ordered[dividerIndex + 1].Right - MinColumnWidthPt;
        var x = Math.Clamp(newX, leftLimit, rightLimit);

        var leftCol = ordered[dividerIndex];
        var rightCol = ordered[dividerIndex + 1];
        ordered[dividerIndex] = new Rect(leftCol.X, leftCol.Y, x - leftCol.X, leftCol.Height);
        ordered[dividerIndex + 1] = new Rect(x, rightCol.Y, rightCol.Right - x, rightCol.Height);
        return ordered;
    }

    /// <summary>Inserts a divider at <paramref name="x"/> inside the column that contains it.</summary>
    public static IReadOnlyList<Rect>? TryInsertDivider(IReadOnlyList<Rect> columnAreasLeftToRight, double x)
    {
        var ordered = columnAreasLeftToRight.OrderBy(a => a.X).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var col = ordered[i];
            if (x <= col.Left + MinColumnWidthPt || x >= col.Right - MinColumnWidthPt)
                continue;

            ordered[i] = new Rect(col.X, col.Y, x - col.X, col.Height);
            ordered.Insert(i + 1, new Rect(x, col.Y, col.Right - x, col.Height));
            return ordered;
        }

        return null;
    }

    /// <summary>Removes divider <paramref name="dividerIndex"/> by merging the two adjacent columns.</summary>
    public static IReadOnlyList<Rect>? TryRemoveDivider(IReadOnlyList<Rect> columnAreasLeftToRight, int dividerIndex)
    {
        var ordered = columnAreasLeftToRight.OrderBy(a => a.X).ToList();
        if (dividerIndex < 0 || dividerIndex >= ordered.Count - 1)
            return null;

        var left = ordered[dividerIndex];
        var right = ordered[dividerIndex + 1];
        ordered[dividerIndex] = new Rect(left.X, left.Y, right.Right - left.X, left.Height);
        ordered.RemoveAt(dividerIndex + 1);
        return ordered;
    }

    private static bool IntersectsHeader(Rect word, Rect header)
    {
        var cx = word.Left + word.Width / 2;
        var cy = word.Top + word.Height / 2;
        return cx >= header.Left && cx <= header.Right
               && cy >= header.Top && cy <= header.Bottom;
    }

    private static List<List<PdfTextGeometry.WordInTopLeft>> ClusterByHorizontalGaps(
        List<PdfTextGeometry.WordInTopLeft> wordsLeftToRight)
    {
        var gaps = new List<double>();
        for (var i = 1; i < wordsLeftToRight.Count; i++)
        {
            var gap = wordsLeftToRight[i].Area.Left - wordsLeftToRight[i - 1].Area.Right;
            if (gap > 0)
                gaps.Add(gap);
        }

        var avgHeight = wordsLeftToRight.Average(w => Math.Max(w.Area.Height, 1));
        var threshold = Math.Max(3.0, avgHeight * 0.55);
        if (gaps.Count > 0)
        {
            var sorted = gaps.OrderBy(g => g).ToList();
            var median = sorted[sorted.Count / 2];
            threshold = Math.Max(threshold, median * 1.75);
        }

        var clusters = new List<List<PdfTextGeometry.WordInTopLeft>>
        {
            new() { wordsLeftToRight[0] }
        };

        for (var i = 1; i < wordsLeftToRight.Count; i++)
        {
            var gap = wordsLeftToRight[i].Area.Left - wordsLeftToRight[i - 1].Area.Right;
            if (gap > threshold)
                clusters.Add([]);
            clusters[^1].Add(wordsLeftToRight[i]);
        }

        return clusters;
    }

    private static string JoinClusterName(IReadOnlyList<PdfTextGeometry.WordInTopLeft> cluster) =>
        PdfTextGeometry.NormalizePhrase(string.Join(' ', cluster.Select(w => w.Text.Trim())));

    private static IReadOnlyList<ColumnBand> BuildBandsFromDividers(
        Rect headerTopLeft,
        List<double> dividers,
        Func<int, string> nameForColumnIndex) =>
        BuildBandsFromDividers(headerTopLeft, (IReadOnlyList<double>)dividers, nameForColumnIndex);
}
