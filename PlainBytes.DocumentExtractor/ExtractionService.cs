// SPDX-FileCopyrightText: 2026 PlainBytes Studio
// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Windows;
using ClosedXML.Excel;
using PlainBytes.DocumentExtractor.Resources;
using UglyToad.PdfPig;

namespace PlainBytes.DocumentExtractor;

/// <summary>
/// Template anchor used by batch extraction.
/// Coordinates are stored with origin at the page top-left (Y = distance from page top).
/// </summary>
/// <param name="Id">Stable id referenced by fields.</param>
/// <param name="Label">Full selected phrase to locate.</param>
/// <param name="PageIndex">0-based page index within the PDF.</param>
/// <param name="PdfArea">Sample region in top-left stored coordinates (points).</param>
internal sealed record AnchorSpec(
    string Id,
    string Label,
    int PageIndex,
    Rect PdfArea,
    AnchorKind Kind = AnchorKind.Value,
    Rect? PhrasePdfArea = null);

/// <summary>
/// Template field bound to one anchor. Offset comes from that anchor only.
/// </summary>
/// <param name="Label">Column / field name.</param>
/// <param name="PageIndex">0-based page index within the PDF.</param>
/// <param name="PdfArea">Sample region in top-left stored coordinates (points).</param>
/// <param name="AnchorId">Owning anchor id (required).</param>
internal sealed record FieldSpec(
    string Label,
    int PageIndex,
    Rect PdfArea,
    string AnchorId,
    bool IsRulerColumn = false);

/// <summary>Per-file extraction outcome written to Excel and shown in the progress UI.</summary>
/// <param name="Path">Absolute path of the source PDF.</param>
/// <param name="Success">
/// True when the file was processed (bound anchors resolved). False on open/locate/layout failure.
/// </param>
/// <param name="Error">Failure message when <paramref name="Success"/> is false; otherwise null.</param>
/// <param name="SummaryValues">Non-table field texts in template order (empty on failure).</param>
/// <param name="TableDetails">Extracted grid rows per table anchor (empty when failed or no table).</param>
/// <param name="Status">UI/Excel status: Success or Failed. Empty field values do not change Success.</param>
internal sealed record ExtractFileResult(
    string Path,
    bool Success,
    string? Error,
    IReadOnlyList<string> SummaryValues,
    IReadOnlyList<TableAnchorExtract> TableDetails,
    string Status);

/// <summary>One table anchor’s data rows for the detail sheet (cells match <see cref="FieldSpec"/> order for that anchor).</summary>
internal sealed record TableAnchorExtract(string AnchorId, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>Progress payload: completed count includes the file just finished.</summary>
internal sealed record ExtractProgressReport(int Completed, int Total, string? CurrentFile);

/// <summary>
/// Batch PDF field extraction against a template.
/// Pipeline per file: open → locate each bound anchor independently → apply that anchor's
/// (dx, dy) only to its fields → Excel.
/// Anchors do not share a page-level offset; relative distance between anchors is not checked
/// (regions may move independently, e.g. header vs footer when the middle grows).
/// </summary>
internal static class ExtractionService
{
    /// <summary>Anchor phrase could not be found on the expected page.</summary>
    private const string AnchorNotFound =
        "One or more required anchors could not be found. The document layout or anchor text may be different from the sample.";

    /// <summary>
    /// Located anchor size differs from the sample → treat as a different document class / layout.
    /// </summary>
    private const string AnchorLayoutMismatch =
        "The anchors could not reliably determine the document position. Review the document and template configuration.";

    private const string FieldAnchorMissing =
        "One or more fields are not bound to a valid anchor. Recreate the template with anchors first.";

    private const string SourceFileColumnHeader = "Source File";
    private const int ExcelSheetNameMaxLength = 31;

    /// <summary>
    /// Extracts fields from each PDF and writes one Excel workbook.
    /// Cancellation stops further files; Excel is written only when the loop finishes without cancel.
    /// </summary>
    public static IReadOnlyList<ExtractFileResult> ExtractBatch(
        IReadOnlyList<string> filePaths,
        IReadOnlyList<AnchorSpec> anchors,
        IReadOnlyList<FieldSpec> fields,
        string outputPath,
        IProgress<ExtractProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        var results = new List<ExtractFileResult>(filePaths.Count);
        var total = filePaths.Count;

        for (var i = 0; i < filePaths.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
                return results;

            var path = filePaths[i];
            progress?.Report(new ExtractProgressReport(i, total, path));

            ExtractFileResult result;
            try
            {
                result = ExtractOne(path, anchors, fields);
            }
            catch (OperationCanceledException)
            {
                return results;
            }
            catch (Exception ex)
            {
                result = new ExtractFileResult(
                    path,
                    false,
                    ex.Message,
                    EmptyValues(GetSummaryFields(fields, anchors).Count),
                    [],
                    "Failed");
            }

            results.Add(result);
            progress?.Report(new ExtractProgressReport(i + 1, total, path));
        }

        if (cancellationToken.IsCancellationRequested)
            return results;

        WriteExcel(outputPath, anchors, fields, results);
        return results;
    }

    private static List<FieldSpec> GetSummaryFields(
        IReadOnlyList<FieldSpec> fields,
        IReadOnlyList<AnchorSpec> anchors)
    {
        var anchorsById = anchors.ToDictionary(a => a.Id, StringComparer.Ordinal);
        return fields
            .Where(f => anchorsById.TryGetValue(f.AnchorId, out var a) && a.Kind != AnchorKind.Table)
            .ToList();
    }

    /// <summary>
    /// Extracts one PDF: resolve per-anchor offsets, then read each field at template area + its anchor offset.
    /// Empty field text is allowed and still counts as Success.
    /// </summary>
    private static ExtractFileResult ExtractOne(
        string path,
        IReadOnlyList<AnchorSpec> anchors,
        IReadOnlyList<FieldSpec> fields)
    {
        if (!File.Exists(path))
        {
            return new ExtractFileResult(
                path,
                false,
                "The file could not be found.",
                EmptyValues(GetSummaryFields(fields, anchors).Count),
                [],
                "Failed");
        }

        using var document = PdfTextGeometry.OpenReadShared(path);

        if (!TryResolveAnchorOffsets(document, anchors, fields, out var offsetsByAnchorId, out var error))
        {
            return new ExtractFileResult(
                path,
                false,
                error,
                EmptyValues(GetSummaryFields(fields, anchors).Count),
                [],
                "Failed");
        }

        var anchorsById = anchors
            .Where(a => !string.IsNullOrWhiteSpace(a.Id))
            .ToDictionary(a => a.Id, StringComparer.Ordinal);

        var summaryValues = new List<string>();
        foreach (var field in fields)
        {
            if (!anchorsById.TryGetValue(field.AnchorId, out var owner) || owner.Kind == AnchorKind.Table)
                continue;

            offsetsByAnchorId.TryGetValue(field.AnchorId, out var offset);
            var area = new Rect(
                field.PdfArea.X + offset.Dx,
                field.PdfArea.Y + offset.Dy,
                field.PdfArea.Width,
                field.PdfArea.Height);
            summaryValues.Add(ExtractFieldText(document, field.PageIndex + 1, area));
        }

        var tableDetails = new List<TableAnchorExtract>();
        var tableAnchorIds = fields
            .Select(f => f.AnchorId)
            .Where(id => anchorsById.TryGetValue(id, out var a) && a.Kind == AnchorKind.Table)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var tableAnchorId in tableAnchorIds)
        {
            var owner = anchorsById[tableAnchorId];
            var tableFields = fields.Where(f => f.AnchorId == tableAnchorId).ToList();
            if (TryExtractTableGrid(
                    document,
                    owner,
                    tableFields,
                    anchors,
                    offsetsByAnchorId,
                    out var rows))
                tableDetails.Add(new TableAnchorExtract(tableAnchorId, rows));
        }

        return new ExtractFileResult(path, true, null, summaryValues, tableDetails, "Success");
    }

    private static bool TryExtractTableGrid(
        PdfDocument document,
        AnchorSpec tableAnchor,
        IReadOnlyList<FieldSpec> tableFields,
        IReadOnlyList<AnchorSpec> allAnchors,
        Dictionary<string, (double Dx, double Dy)> offsetsByAnchorId,
        out IReadOnlyList<IReadOnlyList<string>> rows)
    {
        rows = [];

        if (tableFields.Count == 0)
            return true;

        if (!TryLocateAnchor(document, tableAnchor, out var tableLocated))
            return true;

        var pageIndex = tableAnchor.PageIndex;
        var locatedOnPage = BuildLocatedAnchorsOnPage(document, allAnchors, pageIndex);

        var columns = tableFields
            .Select(f =>
            {
                offsetsByAnchorId.TryGetValue(f.AnchorId, out var offset);
                var header = new Rect(
                    f.PdfArea.X + offset.Dx,
                    f.PdfArea.Y + offset.Dy,
                    f.PdfArea.Width,
                    f.PdfArea.Height);
                return new TableColumnDef(header, f.IsRulerColumn);
            })
            .ToList();

        if (!TableExtractionService.TryExtract(
                document,
                pageIndex,
                tableAnchor.Id,
                tableLocated,
                columns,
                locatedOnPage,
                out var table))
            return true;

        rows = table.Rows;
        return true;
    }

    private static List<TableBoundsResolver.LocatedAnchor> BuildLocatedAnchorsOnPage(
        PdfDocument document,
        IReadOnlyList<AnchorSpec> anchors,
        int pageIndex)
    {
        var list = new List<TableBoundsResolver.LocatedAnchor>();
        foreach (var anchor in anchors)
        {
            if (anchor.PageIndex != pageIndex)
                continue;
            if (!TryLocateAnchor(document, anchor, out var found))
                continue;
            list.Add(new TableBoundsResolver.LocatedAnchor(anchor.Id, pageIndex, found));
        }

        return list;
    }

    /// <summary>
    /// Locates each anchor that owns at least one field and computes that anchor's translation.
    /// Size must match the sample; inter-anchor distances are not validated.
    /// </summary>
    private static bool TryResolveAnchorOffsets(
        PdfDocument document,
        IReadOnlyList<AnchorSpec> anchors,
        IReadOnlyList<FieldSpec> fields,
        out Dictionary<string, (double Dx, double Dy)> offsetsByAnchorId,
        out string? error)
    {
        offsetsByAnchorId = new Dictionary<string, (double Dx, double Dy)>(StringComparer.Ordinal);
        error = null;

        if (fields.Count == 0)
            return true;

        var anchorsById = anchors
            .Where(a => !string.IsNullOrWhiteSpace(a.Id))
            .ToDictionary(a => a.Id, StringComparer.Ordinal);

        var requiredIds = fields
            .Select(f => f.AnchorId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (requiredIds.Count == 0 || requiredIds.Any(id => !anchorsById.ContainsKey(id)))
        {
            error = FieldAnchorMissing;
            return false;
        }

        foreach (var id in requiredIds)
        {
            var anchor = anchorsById[id];
            if (!TryLocateAnchor(document, anchor, out var found))
            {
                error = AnchorNotFound;
                return false;
            }

            // Table anchors keep a user-drawn outer box; size is not compared to the phrase glyph union.
            var phraseSample = anchor.PhrasePdfArea ?? anchor.PdfArea;
            if (anchor.Kind != AnchorKind.Table
                && !PdfTextGeometry.SizeMatches(phraseSample, found))
            {
                error = AnchorLayoutMismatch;
                return false;
            }

            offsetsByAnchorId[id] = (
                found.X - phraseSample.X,
                found.Y - phraseSample.Y);
        }

        return true;
    }

    /// <summary>
    /// Finds the anchor phrase on its page and returns the text-union bounds in top-left space
    /// (same union rule as creating an anchor). Prefers a match near the template inner box when ambiguous.
    /// </summary>
    private static bool TryLocateAnchor(PdfDocument document, AnchorSpec anchor, out Rect foundTopLeft)
    {
        foundTopLeft = default;
        var pageNumber = anchor.PageIndex + 1;
        if (pageNumber < 1 || pageNumber > document.NumberOfPages)
            return false;

        var page = document.GetPage(pageNumber);
        var innerSample = anchor.PhrasePdfArea ?? anchor.PdfArea;
        var preferNearPdf = PdfTextGeometry.ToPdfUserSpace(innerSample, page.Height);
        if (!PdfTextGeometry.TryFindPhraseBounds(
                page,
                anchor.Label,
                preferNear: preferNearPdf,
                out var foundPdf))
            return false;

        foundTopLeft = PdfTextGeometry.FromPdfUserSpace(foundPdf, page.Height);
        return true;
    }

    /// <summary>
    /// Reads field text from a calibrated top-left region using the same intersection rule
    /// as rubber-band selection / field preview.
    /// </summary>
    private static string ExtractFieldText(PdfDocument document, int pageNumber1Based, Rect topLeftArea)
    {
        if (pageNumber1Based < 1 || pageNumber1Based > document.NumberOfPages)
            return string.Empty;

        var page = document.GetPage(pageNumber1Based);
        var pdfUser = PdfTextGeometry.ToPdfUserSpace(topLeftArea, page.Height);
        var (text, _) = PdfTextGeometry.ExtractIntersectingTextAndBounds(page, pdfUser);
        return text;
    }

    /// <summary>
    /// Summary sheet: Source File | non-table fields | Status (one row per PDF).
    /// One detail sheet per table anchor (name = anchor label, max 31 chars):
    /// Source File | that table's columns | one row per table data row.
    /// </summary>
    private static void WriteExcel(
        string outputPath,
        IReadOnlyList<AnchorSpec> anchors,
        IReadOnlyList<FieldSpec> fields,
        IReadOnlyList<ExtractFileResult> results)
    {
        using var workbook = new XLWorkbook();
        var summaryFields = GetSummaryFields(fields, anchors);
        var anchorsById = anchors
            .Where(a => !string.IsNullOrWhiteSpace(a.Id))
            .ToDictionary(a => a.Id, StringComparer.Ordinal);

        var summarySheet = workbook.Worksheets.Add(
            MakeUniqueSheetName(workbook, Strings.Excel_SummarySheetName));
        summarySheet.Cell(1, 1).Value = SourceFileColumnHeader;
        for (var c = 0; c < summaryFields.Count; c++)
            summarySheet.Cell(1, c + 2).Value = summaryFields[c].Label;
        summarySheet.Cell(1, summaryFields.Count + 2).Value = "Status";

        summarySheet.Range(1, 1, 1, summaryFields.Count + 2).Style.Font.Bold = true;

        for (var r = 0; r < results.Count; r++)
        {
            var row = r + 2;
            var item = results[r];
            summarySheet.Cell(row, 1).Value = Path.GetFileName(item.Path);
            for (var c = 0; c < summaryFields.Count; c++)
            {
                var value = c < item.SummaryValues.Count ? item.SummaryValues[c] : string.Empty;
                summarySheet.Cell(row, c + 2).Value = value;
            }

            summarySheet.Cell(row, summaryFields.Count + 2).Value = item.Status;
        }

        summarySheet.Columns().AdjustToContents();

        // Preserve template field order of first appearance per table anchor.
        var tableAnchorIds = fields
            .Select(f => f.AnchorId)
            .Where(id => anchorsById.TryGetValue(id, out var a) && a.Kind == AnchorKind.Table)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var tableAnchorId in tableAnchorIds)
        {
            var owner = anchorsById[tableAnchorId];
            var tableFields = fields.Where(f => f.AnchorId == tableAnchorId).ToList();
            if (tableFields.Count == 0)
                continue;

            var detailSheet = workbook.Worksheets.Add(MakeUniqueSheetName(workbook, owner.Label));
            detailSheet.Cell(1, 1).Value = SourceFileColumnHeader;
            for (var c = 0; c < tableFields.Count; c++)
                detailSheet.Cell(1, c + 2).Value = tableFields[c].Label;

            detailSheet.Range(1, 1, 1, tableFields.Count + 1).Style.Font.Bold = true;

            var detailRow = 2;
            foreach (var item in results)
            {
                if (!item.Success || item.TableDetails.Count == 0)
                    continue;

                var block = item.TableDetails.FirstOrDefault(b =>
                    string.Equals(b.AnchorId, tableAnchorId, StringComparison.Ordinal));
                if (block is null || block.Rows.Count == 0)
                    continue;

                var sourceFile = Path.GetFileName(item.Path);
                foreach (var dataRow in block.Rows)
                {
                    detailSheet.Cell(detailRow, 1).Value = sourceFile;
                    for (var c = 0; c < tableFields.Count && c < dataRow.Count; c++)
                        detailSheet.Cell(detailRow, c + 2).Value = dataRow[c];
                    detailRow++;
                }

                // One blank row after each table, then two blank rows after each file.
                detailRow += 3;
            }

            detailSheet.Columns().AdjustToContents();
        }

        workbook.SaveAs(outputPath);
    }

    /// <summary>Excel worksheet name: sanitize illegal chars and truncate to 31; ensure workbook-unique.</summary>
    private static string MakeUniqueSheetName(XLWorkbook workbook, string? preferred)
    {
        var baseName = SanitizeSheetName(preferred);
        var name = baseName;
        var n = 2;
        while (workbook.Worksheets.Any(ws =>
                   string.Equals(ws.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            var suffix = $" ({n})";
            var maxBase = Math.Max(1, ExcelSheetNameMaxLength - suffix.Length);
            var truncated = baseName.Length <= maxBase ? baseName : baseName[..maxBase];
            name = truncated + suffix;
            n++;
        }

        return name;
    }

    /// <summary>Strip \ / ? * [ ] and clamp to <see cref="ExcelSheetNameMaxLength"/>.</summary>
    private static string SanitizeSheetName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Sheet";

        Span<char> buffer = stackalloc char[raw.Length];
        var len = 0;
        foreach (var ch in raw.Trim())
        {
            buffer[len++] = ch is '\\' or '/' or '?' or '*' or '[' or ']' ? '_' : ch;
        }

        var s = new string(buffer[..len]);
        if (s.Length > ExcelSheetNameMaxLength)
            s = s[..ExcelSheetNameMaxLength];
        return string.IsNullOrWhiteSpace(s) ? "Sheet" : s;
    }

    /// <summary>Placeholder field values for failed files (keeps Excel column count stable).</summary>
    private static IReadOnlyList<string> EmptyValues(int count) =>
        Enumerable.Repeat(string.Empty, Math.Max(count, 0)).ToArray();
}
