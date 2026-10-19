// SPDX-FileCopyrightText: 2026 PlainBytes Studio
// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Resources;

namespace PlainBytes.DocumentExtractor.Help;

internal static class HelpText
{
    private static readonly CultureInfo FallbackCulture = CultureInfo.GetCultureInfo("en");

    private static readonly ResourceManager Manager =
        new("PlainBytes.DocumentExtractor.Help.HelpResources", typeof(HelpText).Assembly);

    public static string Get(string key)
    {
        var value = Manager.GetString(key, CultureInfo.CurrentUICulture);
        if (string.IsNullOrEmpty(value))
            value = Manager.GetString(key, FallbackCulture);
        return string.IsNullOrEmpty(value) ? key : value;
    }
}
