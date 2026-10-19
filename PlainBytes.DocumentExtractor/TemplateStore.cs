// SPDX-FileCopyrightText: 2026 PlainBytes Studio
// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace PlainBytes.DocumentExtractor;

/// <summary>Persisted extraction template (anchors + field regions).</summary>
internal sealed class ExtractionTemplate
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime LastModified { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Region Y origin. <c>topLeft</c> = distance from page top (current).
    /// Missing / other = legacy PDF user-space bottom-left (migrated on load when page height is known).
    /// </summary>
    public string CoordinateOrigin { get; set; } = TemplateRegion.OriginTopLeft;

    public List<TemplateRegion> Anchors { get; set; } = [];
    public List<TemplateRegion> Fields { get; set; } = [];
}

/// <summary>Named PDF region; Y is distance from the page top (origin top-left).</summary>
internal sealed class TemplateRegion
{
    public const string OriginTopLeft = "topLeft";

    public string Name { get; set; } = "";

    /// <summary>
    /// Anchor stable id (required for anchors). Null for field regions.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Selected anchor text used for locating the same phrase in other PDFs.
    /// Null/empty for field regions.
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// Owning anchor id (required for fields). Null for anchor regions.
    /// </summary>
    public string? AnchorId { get; set; }

    /// <summary>Anchor only: <see cref="AnchorKind.Value"/> or <see cref="AnchorKind.Table"/>.</summary>
    public string? Kind { get; set; }

    /// <summary>
    /// Optional phrase text-union (inner) box. Same for Value and Table.
    /// When present, locate offset uses this; <see cref="X"/>/<see cref="Y"/>/<see cref="Width"/>/<see cref="Height"/>
    /// stay the Value box or Table header outer box.
    /// </summary>
    public double PhraseX { get; set; }
    public double PhraseY { get; set; }
    public double PhraseWidth { get; set; }
    public double PhraseHeight { get; set; }

    public bool HasPhraseBounds => PhraseWidth > 0.5 && PhraseHeight > 0.5;

    public Rect ToPhraseRect() => new(PhraseX, PhraseY, PhraseWidth, PhraseHeight);

    /// <summary>Table column only: ruler (base) column for row banding.</summary>
    public bool IsRulerColumn { get; set; }

    public int PageIndex { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public Rect ToRect() => new(X, Y, Width, Height);

    /// <summary>Field region bound to <paramref name="anchorId"/>.</summary>
    public static TemplateRegion FromField(
        string name,
        string anchorId,
        int pageIndex,
        Rect area,
        bool isRulerColumn = false) => new()
    {
        Name = name,
        AnchorId = anchorId,
        PageIndex = pageIndex,
        X = area.X,
        Y = area.Y,
        Width = area.Width,
        Height = area.Height,
        IsRulerColumn = isRulerColumn,
    };

    /// <summary>Anchor region; <paramref name="text"/> is the selected phrase to match.</summary>
    public static TemplateRegion FromAnchor(
        string id,
        string text,
        int pageIndex,
        Rect area,
        AnchorKind kind = AnchorKind.Value,
        Rect? phraseArea = null) => new()
    {
        Id = id,
        Name = text,
        Text = text,
        PageIndex = pageIndex,
        X = area.X,
        Y = area.Y,
        Width = area.Width,
        Height = area.Height,
        Kind = kind == AnchorKind.Table ? "table" : "value",
        PhraseX = phraseArea?.X ?? 0,
        PhraseY = phraseArea?.Y ?? 0,
        PhraseWidth = phraseArea?.Width ?? 0,
        PhraseHeight = phraseArea?.Height ?? 0,
    };

    /// <summary>Resolved anchor search text (prefers <see cref="Text"/>, falls back to <see cref="Name"/>).</summary>
    public string ResolveAnchorText() =>
        !string.IsNullOrWhiteSpace(Text) ? Text.Trim() : (Name ?? string.Empty).Trim();
}

/// <summary>Export/import bundle that can hold one or many templates in a single file.</summary>
internal sealed class ExtractionTemplatePack
{
    public const string FormatId = "plainbytes.documentextractor.templates";

    public string Format { get; set; } = FormatId;
    public int Version { get; set; } = 3;
    public List<ExtractionTemplate> Templates { get; set; } = [];
}

/// <summary>
/// Offline template catalog under LocalApplicationData (one JSON file per template).
/// </summary>
internal static class TemplateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string RootDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PlainBytes",
        "DocumentExtractor",
        "Templates");

    /// <summary>Raised after create / update / rename / delete / import.</summary>
    public static event Action? Changed;

    public static IReadOnlyList<ExtractionTemplate> GetAll()
    {
        EnsureRoot();
        var list = new List<ExtractionTemplate>();
        foreach (var path in Directory.EnumerateFiles(RootDir, "*.json"))
        {
            var doc = TryReadSingle(path);
            if (doc is not null)
                list.Add(doc);
        }

        return list
            .OrderByDescending(t => t.LastModified)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static ExtractionTemplate? Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        return TryReadSingle(PathFor(id));
    }

    public static bool NameExists(string name, string? exceptId = null)
    {
        foreach (var t in GetAll())
        {
            if (exceptId is not null && string.Equals(t.Id, exceptId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static ExtractionTemplate Save(ExtractionTemplate template)
    {
        Write(template);
        NotifyChanged();
        return template;
    }

    public static bool Delete(string id)
    {
        var path = PathFor(id);
        if (!File.Exists(path))
            return false;

        File.Delete(path);
        NotifyChanged();
        return true;
    }

    /// <summary>Notify listeners without rewriting files (e.g. after a suppressed batch import).</summary>
    public static void NotifyChanged() => Changed?.Invoke();

    /// <summary>
    /// Imports a single-template JSON or a multi-template pack. Returns how many templates were added.
    /// </summary>
    public static int ImportFromFile(string path)
    {
        var imported = ReadImportCandidates(path);
        if (imported.Count == 0)
            return 0;

        foreach (var doc in imported)
        {
            doc.Id = "tpl-" + Guid.NewGuid().ToString("N")[..12];
            if (string.IsNullOrWhiteSpace(doc.Name))
                doc.Name = Path.GetFileNameWithoutExtension(path);
            doc.Name = EnsureUniqueName(doc.Name);
            doc.Anchors ??= [];
            doc.Fields ??= [];
            Write(doc);
        }

        return imported.Count;
    }

    /// <summary>Writes all templates into one JSON file (single template or pack).</summary>
    public static void ExportToFile(IReadOnlyList<ExtractionTemplate> templates, string path)
    {
        if (templates.Count == 0)
            return;

        if (templates.Count == 1)
        {
            var json = JsonSerializer.Serialize(templates[0], JsonOptions);
            File.WriteAllText(path, json);
            return;
        }

        var pack = new ExtractionTemplatePack
        {
            Templates = templates.ToList(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(pack, JsonOptions));
    }

    public static string EnsureUniqueName(string baseName, string? exceptId = null)
    {
        if (!NameExists(baseName, exceptId))
            return baseName;

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!NameExists(candidate, exceptId))
                return candidate;
        }

        return $"{baseName} ({Guid.NewGuid().ToString("N")[..4]})";
    }

    private static void Write(ExtractionTemplate template)
    {
        if (string.IsNullOrWhiteSpace(template.Id))
            template.Id = "tpl-" + Guid.NewGuid().ToString("N")[..12];

        template.LastModified = DateTime.UtcNow;
        EnsureRoot();
        var json = JsonSerializer.Serialize(template, JsonOptions);
        File.WriteAllText(PathFor(template.Id), json);
    }

    private static string PathFor(string id) =>
        Path.Combine(RootDir, SanitizeId(id) + ".json");

    private static string SanitizeId(string id)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            id = id.Replace(c, '_');
        return id;
    }

    private static void EnsureRoot() => Directory.CreateDirectory(RootDir);

    private static List<ExtractionTemplate> ReadImportCandidates(string path)
    {
        try
        {
            if (!File.Exists(path))
                return [];

            var json = File.ReadAllText(path);

            // Prefer pack format when present.
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("templates", out _)
                && (doc.RootElement.TryGetProperty("format", out var format)
                    || doc.RootElement.TryGetProperty("Format", out format)))
            {
                var pack = JsonSerializer.Deserialize<ExtractionTemplatePack>(json, JsonOptions);
                if (pack?.Templates is { Count: > 0 })
                    return pack.Templates.Where(IsValidTemplate).ToList();
            }

            // Also accept pack without format field if "templates" array exists.
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("templates", out var templatesEl)
                && templatesEl.ValueKind == JsonValueKind.Array)
            {
                var pack = JsonSerializer.Deserialize<ExtractionTemplatePack>(json, JsonOptions);
                if (pack?.Templates is { Count: > 0 })
                    return pack.Templates.Where(IsValidTemplate).ToList();
            }

            var single = JsonSerializer.Deserialize<ExtractionTemplate>(json, JsonOptions);
            return single is not null && IsValidTemplate(single) ? [single] : [];
        }
        catch
        {
            return [];
        }
    }

    private static bool IsValidTemplate(ExtractionTemplate? doc) =>
        doc is not null && !string.IsNullOrWhiteSpace(doc.Name);

    private static ExtractionTemplate? TryReadSingle(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var json = File.ReadAllText(path);
            var doc = JsonSerializer.Deserialize<ExtractionTemplate>(json, JsonOptions);
            if (doc is null || string.IsNullOrWhiteSpace(doc.Id) || string.IsNullOrWhiteSpace(doc.Name))
                return null;

            doc.Anchors ??= [];
            doc.Fields ??= [];
            return doc;
        }
        catch
        {
            return null;
        }
    }
}
