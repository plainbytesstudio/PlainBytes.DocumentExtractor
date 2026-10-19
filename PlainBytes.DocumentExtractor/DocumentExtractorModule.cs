// SPDX-FileCopyrightText: 2026 PlainBytes Studio
// SPDX-License-Identifier: GPL-3.0-only
using PlainBytes.Core.Interfaces;

namespace PlainBytes.DocumentExtractor;

/// <summary>DocumentExtractor tool module hosted by the shared Shell.</summary>
public sealed class DocumentExtractorModule : IToolModule
{
    public string ToolName => "DocumentExtractor";

    public string ToolVersion => "1.0.0";

    public System.Windows.FrameworkElement GetMainControl() => new MainModule();

    public void Initialize()
    {
    }
}
