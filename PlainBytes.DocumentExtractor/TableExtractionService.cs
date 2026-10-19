// SPDX-FileCopyrightText: 2026 PlainBytes Studio
// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
using System.Windows;
using UglyToad.PdfPig;

namespace PlainBytes.DocumentExtractor;

/// <summary>
/// One table column: calibrated header box for the column’s usable content band (Left..Right in the header row).
/// Boxes need not touch neighbors—invoice-style tables keep margin between text and vertical rules; wrap stays in-band.
/// </summary>

internal readonly record struct TableColumnDef(Rect HeaderAreaTopLeft, bool IsRuler);

/// <summary>
/// Grid of cell text after step 4. Outer list = data rows (top to bottom); inner list = columns in template field order.
/// </summary>

internal readonly record struct TableExtractResult(IReadOnlyList<IReadOnlyList<string>> Rows);


/// <summary>
/// Step 4: words in the table body → column assignment → row bands (ruler column) → assembled cell text.
/// All geometry uses stored coordinates: origin top-left, Y = distance from page top (points).
/// Column template assumption: the user boxes each column’s content width in the header row (not necessarily abutting).
/// </summary>

internal static class TableExtractionService
{
    /// <summary>Sub-point tolerance for Y/X comparisons (PDF float jitter).</summary>
    private const double EpsilonPt = 0.25;

    /// <summary>
    /// Extracts the table body on one page.
    /// </summary>
    /// <param name="document">Open PDF.</param>
    /// <param name="pageIndex">0-based page index.</param>
    /// <param name="tableAnchorId">Table anchor id (excluded when resolving vertical end).</param>
    /// <param name="tableAnchorLocatedTopLeft">Located table anchor phrase box in the current file (step 3 top).</param>
    /// <param name="columns">Per-column header strips (usable content width); calibrated; gaps between columns are allowed.</param>
    /// <param name="locatedAnchorsOnPage">All anchors on this page with located top-left boxes (step 3 bottom).</param>
    /// <param name="result">Row/column grid on success; empty rows when the body has no words.</param>
    /// <returns>False when inputs or vertical bounds are invalid; true with empty rows when the body is empty.</returns>

    public static bool TryExtract(
        PdfDocument document,
        int pageIndex,
        string tableAnchorId,
        Rect tableAnchorLocatedTopLeft,
        IReadOnlyList<TableColumnDef> columns,
        IEnumerable<TableBoundsResolver.LocatedAnchor> locatedAnchorsOnPage,
        out TableExtractResult result)
    {
        result = default;

        if (columns.Count == 0)
            return false;

        var pageNumber = pageIndex + 1;
        if (pageNumber < 1 || pageNumber > document.NumberOfPages)
            return false;

        var page = document.GetPage(pageNumber);
        var pageHeight = page.Height;

        // Step 3: vertical extent + horizontal span from column headers.
        var headerAreas = columns.Select(c => c.HeaderAreaTopLeft).ToList();
        if (!TableBoundsResolver.TryResolveVerticalBounds(
                tableAnchorId,
                pageIndex,
                tableAnchorLocatedTopLeft,
                headerAreas,
                locatedAnchorsOnPage,
                pageHeight,
                out var vertical))
            return false;

        var body = new Rect(
            vertical.ColumnSpan.X,
            vertical.BodyTopY,
            vertical.ColumnSpan.Width,
            Math.Max(0, vertical.BodyBottomY - vertical.BodyTopY));

        if (body.Height < EpsilonPt)
            return false;

        // Column X ranges: sort by header left edge; map back to original field index after assignment.
        var orderedColumns = columns
            .Select((def, index) => (def, index))
            .OrderBy(t => t.def.HeaderAreaTopLeft.X)
            .ToList();

        var xBounds = BuildColumnXBounds(orderedColumns.Select(t => t.def.HeaderAreaTopLeft).ToList());

        // Words in reading order, filtered to the table body clip (not header / footer anchors).
        var wordsInBody = PdfTextGeometry.GetOrderedWordsTopLeft(page)
            .Where(w => IntersectsVertically(w.Area, body.Top, body.Bottom)
                        && w.Area.Right > body.Left - EpsilonPt
                        && w.Area.Left < body.Right + EpsilonPt)
            .ToList();

        if (wordsInBody.Count == 0)
        {
            result = new TableExtractResult([]);
            return true;
        }

        // Assign each word to the column strip with the largest horizontal overlap (gutter words are skipped).
        var assigned = new List<(PdfTextGeometry.WordInTopLeft Word, int ColumnIndex)>();
        foreach (var word in wordsInBody)
        {
            var colSorted = AssignColumn(word.Area, xBounds);
            if (colSorted < 0)
                continue;

            var colOriginal = orderedColumns[colSorted].index;
            assigned.Add((word, colOriginal));
        }

        if (assigned.Count == 0)
        {
            result = new TableExtractResult([]);
            return true;
        }

        // Row structure requires an explicit ruler (base) column — no silent leftmost fallback.
        var rulerSortedIndex = orderedColumns.FindIndex(t => t.def.IsRuler);
        if (rulerSortedIndex < 0)
        {
            result = default;
            return false;
        }

        var rulerColumnIndex = orderedColumns[rulerSortedIndex].index;
        var rowBands = BuildRowBands(assigned, rulerColumnIndex, body);
        if (rowBands.Count == 0)
        {
            result = new TableExtractResult([]);
            return true;
        }


        // Fill grid: same cell → join tokens with a space (reading order preserved from word loop).
        var grid = new string[rowBands.Count][];
        for (var r = 0; r < rowBands.Count; r++)
            grid[r] = Enumerable.Repeat(string.Empty, columns.Count).ToArray();


        foreach (var (word, col) in assigned)
        {
            var row = AssignRow(word.Area, rowBands);
            if (row < 0 || col < 0 || col >= columns.Count)
                continue;

            var existing = grid[row][col];
            grid[row][col] = string.IsNullOrEmpty(existing)
                ? word.Text
                : existing + " " + word.Text;
        }


        var rows = grid
            .Where(r => r.Any(cell => !string.IsNullOrWhiteSpace(cell)))
            .Select(r => (IReadOnlyList<string>)r.ToArray())
            .ToArray();
        result = new TableExtractResult(rows);
        return true;
    }


    /// <summary>
    /// Flattens the row grid into one string per template column (newline between rows).
    /// Matches MVP Excel: one PDF row, table columns hold multi-line cell text.
    /// </summary>

    public static IReadOnlyList<string> FormatColumnsForExport(
        TableExtractResult table,
        int columnCount)
    {
        var lines = new string[columnCount];
        for (var c = 0; c < columnCount; c++)
        {
            var sb = new StringBuilder();
            for (var r = 0; r < table.Rows.Count; r++)
            {
                var row = table.Rows[r];
                var value = c < row.Count ? row[c] : string.Empty;
                if (r > 0)
                    sb.AppendLine();

                sb.Append(value);
            }

            lines[c] = sb.ToString();
        }

        return lines;
    }


    /// <summary>
    /// Row band top = ruler-line text top; bottom = next row's text top (last row → table body end).
    /// Uses ruler column words (or all body words if empty). Height = gap between row tops, not glyph bottom.
    /// </summary>
    private static List<(double Top, double Bottom)> BuildRowBands(
        List<(PdfTextGeometry.WordInTopLeft Word, int ColumnIndex)> assigned,
        int rulerColumnIndex,
        Rect body)
    {
        var rulerWords = assigned
            .Where(a => a.ColumnIndex == rulerColumnIndex)
            .Select(a => a.Word)
            .ToList();

        if (rulerWords.Count == 0)
            rulerWords = assigned.Select(a => a.Word).ToList();

        var lines = ClusterIntoLines(rulerWords);
        if (lines.Count == 0)
            return [];

        var rowTops = lines.Select(line => line.Min(w => w.Area.Top)).ToList();

        var bands = new List<(double Top, double Bottom)>(rowTops.Count);
        for (var i = 0; i < rowTops.Count; i++)
        {
            var top = rowTops[i];
            var bottom = i + 1 < rowTops.Count ? rowTops[i + 1] : body.Bottom;
            if (bottom > top + EpsilonPt)
                bands.Add((top, bottom));
        }

        return bands;
    }



    /// <summary>Maps a word to a row index by vertical center against precomputed bands.</summary>

    private static int AssignRow(Rect wordArea, IReadOnlyList<(double Top, double Bottom)> rowBands)
    {
        var centerY = wordArea.Y + wordArea.Height / 2;

        for (var i = 0; i < rowBands.Count; i++)
        {
            var (top, bottom) = rowBands[i];
            if (centerY >= top - EpsilonPt && centerY < bottom + EpsilonPt)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Groups words into horizontal lines (same rule as <see cref="PdfTextGeometry"/> phrase reading order).
    /// Compares word tops with a height-aware tolerance so baseline jitter stays on one line.
    /// </summary>

    private static List<List<PdfTextGeometry.WordInTopLeft>> ClusterIntoLines(
        IReadOnlyList<PdfTextGeometry.WordInTopLeft> words)
    {
        var sorted = words
            .OrderBy(w => w.Area.Top)
            .ThenBy(w => w.Area.Left)
            .ToList();

        if (sorted.Count == 0)
            return [];


        var lines = new List<List<PdfTextGeometry.WordInTopLeft>>();
        foreach (var word in sorted)
        {
            if (lines.Count == 0)
            {
                lines.Add([word]);
                continue;
            }

            var line = lines[^1];
            var referenceTop = line[0].Area.Top;
            var tolerance = Math.Max(
                LineClusterTolerance(line[0].Area.Height),
                LineClusterTolerance(word.Area.Height));

            if (Math.Abs(word.Area.Top - referenceTop) <= tolerance)
                line.Add(word);
            else
                lines.Add([word]);
        }

        return lines;
    }

    /// <summary>Max vertical delta for two words to count as the same text line (PDF points).</summary>

    private static double LineClusterTolerance(double wordHeight) =>
        Math.Max(2.0, wordHeight * 0.55);

    /// <summary>
    /// Picks the left-to-right column strip with maximum horizontal overlap with the word.
    /// Returns -1 when overlap is below tolerance (e.g. word sits in the gutter between content bands).
    /// </summary>

    private static int AssignColumn(Rect wordArea, IReadOnlyList<(double Left, double Right)> xBounds)
    {
        var bestIndex = -1;
        var bestOverlap = EpsilonPt;

        for (var i = 0; i < xBounds.Count; i++)
        {
            var (left, right) = xBounds[i];
            var overlapLeft = Math.Max(wordArea.Left, left);
            var overlapRight = Math.Min(wordArea.Right, right);
            var overlap = overlapRight - overlapLeft;

            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    /// <summary>
    /// Column X strips copied from template header boxes (content usable width; need not be edge-to-edge).
    /// <paramref name="headersLeftToRight"/> sorted by <see cref="Rect.X"/> for stable index order in <see cref="AssignColumn"/>.
    /// </summary>

    private static List<(double Left, double Right)> BuildColumnXBounds(IReadOnlyList<Rect> headersLeftToRight)
    {
        var bounds = new List<(double Left, double Right)>(headersLeftToRight.Count);

        foreach (var h in headersLeftToRight)
            bounds.Add((h.Left, h.Right));

        return bounds;
    }

    /// <summary>True when the word box overlaps the open vertical interval (body top..bottom).</summary>

    private static bool IntersectsVertically(Rect area, double top, double bottom) =>
        area.Bottom > top + EpsilonPt && area.Top < bottom - EpsilonPt;
}
