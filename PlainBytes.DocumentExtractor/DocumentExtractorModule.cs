// SPDX-FileCopyrightText: 2026 PlainBytes Studio
// SPDX-License-Identifier: GPL-3.0-only
using System.Reflection;
using PlainBytes.Core.Interfaces;

namespace PlainBytes.DocumentExtractor;

/// <summary>DocumentExtractor tool module hosted by the shared Shell.</summary>
public sealed class DocumentExtractorModule : IToolModule
{
    public string ToolName => "DocumentExtractor";

    /// <summary>
    /// Application version from the assembly (csproj <c>Version</c> / <c>InformationalVersion</c>),
    /// not the MSIX <c>Package.appxmanifest</c> identity version.
    /// </summary>
    public string ToolVersion => ResolveAssemblyVersion();

    public System.Windows.FrameworkElement GetMainControl() => new MainModule();

    public void Initialize()
    {
    }

    private static string ResolveAssemblyVersion()
    {
        var assembly = typeof(DocumentExtractorModule).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        var version = assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
