// SPDX-FileCopyrightText: 2026 PlainBytes Studio
// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Text;
using System.Windows;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace PlainBytes.DocumentExtractor;

/// <summary>
/// Shared PDF text geometry used when creating anchors/fields and when applying them during extraction.
/// Area text: complete Word.Text when the whole word is inside the selection; otherwise covered letters only.
/// Phrase locate still matches via ordered full words. Creation and extract paths stay aligned.
/// </summary>
internal static class PdfTextGeometry
{
    /// <summary>
    /// Opens a PDF from a memory copy so Pdfium can keep the path locked for preview.
    /// </summary>
    public static PdfDocument OpenReadShared(string path) =>
        PdfDocument.Open(File.ReadAllBytes(path));

    /// <summary>
    /// Allowed difference (PDF points) when comparing region width/height or inter-anchor distance.
    /// </summary>
    public const double GeometryTolerancePt = 0.75;

    /// <summary>
    /// Converts a stored region (origin top-left: Y = distance from page top to the top edge)
    /// into PdfPig PDF user space (origin bottom-left: Y = bottom edge).
    /// </summary>
    public static Rect ToPdfUserSpace(Rect topLeftOrigin, double pageHeight) =>
        new(
            topLeftOrigin.X,
            pageHeight - topLeftOrigin.Y - topLeftOrigin.Height,
            topLeftOrigin.Width,
            topLeftOrigin.Height);

    /// <summary>
    /// Converts a PdfPig PDF user-space rect (origin bottom-left) into stored form
    /// (origin top-left: Y = distance from page top to the top edge).
    /// </summary>
    public static Rect FromPdfUserSpace(Rect pdfUser, double pageHeight) =>
        new(
            pdfUser.X,
            pageHeight - pdfUser.Y - pdfUser.Height,
            pdfUser.Width,
            pdfUser.Height);

    /// <summary>
    /// Returns non-empty words on the page with PDF user-space rects, ordered for reading:
    /// top-to-bottom by line band, then left-to-right within each line.
    /// PdfPig exposes text as individual words; multi-word phrases are rebuilt from this list.
    /// </summary>
    /// <summary>Word text and bounds in stored top-left space (reading order).</summary>
    internal readonly record struct WordInTopLeft(string Text, Rect Area);

    /// <summary>Same reading order as <see cref="GetOrderedWords"/>; areas use origin top-left.</summary>
    public static IReadOnlyList<WordInTopLeft> GetOrderedWordsTopLeft(Page page)
    {
        var pageHeight = page.Height;
        return GetOrderedWordObjects(page)
            .Select(w =>
            {
                var b = w.BoundingBox;
                var pdfUser = new Rect(b.Left, b.Bottom, Math.Max(0, b.Width), Math.Max(0, b.Height));
                var topLeft = FromPdfUserSpace(pdfUser, pageHeight);
                return new WordInTopLeft((w.Text ?? string.Empty).Trim(), topLeft);
            })
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .ToList();
    }

    public static IReadOnlyList<(string Text, Rect Rect)> GetOrderedWords(Page page) =>
        GetOrderedWordObjects(page)
            .Select(w =>
            {
                var b = w.BoundingBox;
                var rect = new Rect(b.Left, b.Bottom, Math.Max(0, b.Width), Math.Max(0, b.Height));
                return (Text: (w.Text ?? string.Empty).Trim(), Rect: rect);
            })
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .ToList();

    /// <summary>
    /// Extracts text and bounds from a rubber-band (or field) area — the rule used when creating
    /// an anchor or previewing a field value.
    /// For each word in reading order:
    /// <list type="bullet">
    /// <item>If the whole word box lies inside the selection → take the complete <see cref="Word.Text"/>.</item>
    /// <item>Otherwise → take only letters whose glyph center is inside the selection (no spaces inside the word).</item>
    /// </list>
    /// Contributing words are joined with a single space. Bounds are the union of the taken glyphs/word boxes.
    /// </summary>
    public static (string Text, Rect? Bounds) ExtractIntersectingTextAndBounds(Page page, Rect pdfArea)
    {
        var pieces = new List<string>();
        Rect? union = null;

        foreach (var word in GetOrderedWordObjects(page))
        {
            var wordBox = word.BoundingBox;
            var wordRect = new Rect(
                wordBox.Left,
                wordBox.Bottom,
                Math.Max(0, wordBox.Width),
                Math.Max(0, wordBox.Height));

            // Whole word inside the selection → keep the complete PDF word as-is.
            if (pdfArea.Contains(wordRect))
            {
                var full = (word.Text ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(full))
                    continue;

                pieces.Add(full);
                union = union is null ? wordRect : Rect.Union(union.Value, wordRect);
                continue;
            }

            // Word only partly overlaps → keep covered letters only (still one token, no inner spaces).
            var covered = new List<(string Value, Rect Rect)>();
            foreach (var letter in word.Letters)
            {
                var value = letter.Value ?? string.Empty;
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                var b = letter.GlyphRectangle;
                var rect = new Rect(b.Left, b.Bottom, Math.Max(0, b.Width), Math.Max(0, b.Height));
                if (!pdfArea.Contains(Center(rect)))
                    continue;

                covered.Add((value, rect));
            }

            if (covered.Count == 0)
                continue;

            pieces.Add(string.Concat(covered.Select(c => c.Value)));
            foreach (var c in covered)
                union = union is null ? c.Rect : Rect.Union(union.Value, c.Rect);
        }

        if (pieces.Count == 0)
            return (string.Empty, null);

        return (string.Join(' ', pieces).Trim(), union);
    }

    /// <summary>
    /// Words on the page in reading order: cluster into visual lines (top-to-bottom),
    /// then left-to-right within each line.
    /// Exact Top-edge sort alone is unsafe — same-line glyphs often differ by a few points of
    /// baseline, which swapped e.g. "ATTN:" after the name.
    /// </summary>
    private static IEnumerable<Word> GetOrderedWordObjects(Page page)
    {
        var words = page.GetWords()
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .OrderByDescending(w => w.BoundingBox.Top)
            .ThenBy(w => w.BoundingBox.Left)
            .ToList();

        if (words.Count == 0)
            return words;

        var lines = new List<List<Word>>();
        foreach (var word in words)
        {
            if (lines.Count == 0)
            {
                lines.Add(new List<Word> { word });
                continue;
            }

            var line = lines[^1];
            var referenceTop = line[0].BoundingBox.Top;
            var tolerance = Math.Max(LineClusterTolerance(line[0]), LineClusterTolerance(word));
            if (Math.Abs(word.BoundingBox.Top - referenceTop) <= tolerance)
            {
                line.Add(word);
            }
            else
            {
                lines.Add(new List<Word> { word });
            }
        }

        return lines.SelectMany(line => line.OrderBy(w => w.BoundingBox.Left));
    }

    /// <summary>
    /// How far two word tops may differ and still count as one reading line (PDF points).
    /// </summary>
    private static double LineClusterTolerance(Word word) =>
        Math.Max(2.0, word.BoundingBox.Height * 0.55);

    /// <summary>
    /// Collapses whitespace so stored anchor phrases and matched page text compare consistently.
    /// </summary>
    public static string NormalizePhrase(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Locates a stored anchor phrase on the page and returns its text-union bounds
    /// (same union rule as <see cref="ExtractIntersectingTextAndBounds"/>).
    /// Tries near <paramref name="preferNear"/> first (sample position), then the full page.
    /// </summary>
    public static bool TryFindPhraseBounds(
        Page page,
        string phrase,
        Rect? preferNear,
        out Rect bounds)
    {
        bounds = default;
        var target = NormalizePhrase(phrase);
        if (string.IsNullOrEmpty(target))
            return false;

        var words = GetOrderedWords(page);
        if (words.Count == 0)
            return false;

        if (preferNear is { } near && TryMatchPhrase(words, target, near, out bounds))
            return true;

        return TryMatchPhrase(words, target, preferNear: null, out bounds);
    }

    /// <summary>
    /// Returns whether two regions have the same width and height within <see cref="GeometryTolerancePt"/>.
    /// Used to verify that a located anchor matches the template sample size.
    /// </summary>
    public static bool SizeMatches(Rect expected, Rect actual) =>
        Math.Abs(expected.Width - actual.Width) <= GeometryTolerancePt
        && Math.Abs(expected.Height - actual.Height) <= GeometryTolerancePt;

    /// <summary>
    /// Euclidean distance between the top-left origins (<see cref="Rect.X"/>, <see cref="Rect.Y"/>)
    /// of two stored regions. Used for multi-anchor relative-layout checks.
    /// </summary>
    public static double OriginDistance(Rect a, Rect b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Returns whether two distances match within <see cref="GeometryTolerancePt"/>.
    /// </summary>
    public static bool DistanceMatches(double expected, double actual) =>
        Math.Abs(expected - actual) <= GeometryTolerancePt;

    /// <summary>
    /// Counts non-overlapping phrase matches on the page whose text-union size matches
    /// <paramref name="expectedSize"/> (width/height within <see cref="GeometryTolerancePt"/>).
    /// Used to warn only when both text and border size are duplicated — same text with a
    /// different box size is not treated as ambiguous.
    /// </summary>
    public static int CountPhraseMatches(Page page, string phrase, Rect expectedSize)
    {
        var target = NormalizePhrase(phrase);
        if (string.IsNullOrEmpty(target))
            return 0;

        var words = GetOrderedWords(page);
        if (words.Count == 0)
            return 0;

        var count = 0;
        for (var i = 0; i < words.Count; i++)
        {
            var combined = new StringBuilder();
            Rect? union = null;
            for (var j = i; j < words.Count; j++)
            {
                if (combined.Length > 0)
                    combined.Append(' ');
                combined.Append(words[j].Text);
                union = union is null ? words[j].Rect : Rect.Union(union.Value, words[j].Rect);

                var normalized = NormalizePhrase(combined.ToString());
                if (normalized.Length > target.Length + 8)
                    break;

                if (!string.Equals(normalized, target, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Text match alone is not enough — require the same border size as the sample.
                if (!SizeMatches(expectedSize, union!.Value))
                    break;

                count++;
                // Skip overlapping windows that start inside this match.
                i = j;
                break;
            }
        }

        return count;
    }

    /// <summary>
    /// Returns true if the document has at least one selectable word on any page.
    /// </summary>
    public static bool HasSelectableText(PdfDocument document)
    {
        for (var i = 1; i <= document.NumberOfPages; i++)
        {
            var page = document.GetPage(i);
            if (GetOrderedWords(page).Count > 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Scans ordered words with a sliding window, joining consecutive words with spaces until the
    /// normalized phrase equals <paramref name="target"/>.
    /// On match, <paramref name="found"/> is the union of those word rects (creation-compatible).
    /// When <paramref name="preferNear"/> is set, picks the match closest to that sample region;
    /// otherwise returns the first match.
    /// </summary>
    private static bool TryMatchPhrase(
        IReadOnlyList<(string Text, Rect Rect)> words,
        string target,
        Rect? preferNear,
        out Rect found)
    {
        found = default;
        Rect? best = null;
        var bestScore = double.MaxValue;

        for (var i = 0; i < words.Count; i++)
        {
            var combined = new StringBuilder();
            Rect? union = null;
            for (var j = i; j < words.Count; j++)
            {
                if (combined.Length > 0)
                    combined.Append(' ');
                combined.Append(words[j].Text);
                union = union is null ? words[j].Rect : Rect.Union(union.Value, words[j].Rect);

                var normalized = NormalizePhrase(combined.ToString());
                if (normalized.Length > target.Length + 8)
                    break;

                if (!string.Equals(normalized, target, StringComparison.OrdinalIgnoreCase))
                    continue;

                var rect = union!.Value;
                if (preferNear is null)
                {
                    found = rect;
                    return true;
                }

                var score = DistanceSquared(Center(rect), Center(preferNear.Value));
                if (score < bestScore)
                {
                    bestScore = score;
                    best = rect;
                }

                break;
            }
        }

        if (best is null)
            return false;

        found = best.Value;
        return true;
    }

    /// <summary>Center point of a PDF user-space rectangle.</summary>
    private static Point Center(Rect rect) =>
        new(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

    /// <summary>Squared Euclidean distance between two points (avoids a square root when ranking).</summary>
    private static double DistanceSquared(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}
