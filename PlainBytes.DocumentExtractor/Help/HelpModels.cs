// SPDX-FileCopyrightText: 2026 PlainBytes Studio
// SPDX-License-Identifier: GPL-3.0-only
namespace PlainBytes.DocumentExtractor.Help;

public sealed class HelpSection
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required IReadOnlyList<HelpBlock> Blocks { get; init; }
}

public abstract class HelpBlock;

public sealed class HelpDescriptionBlock : HelpBlock
{
    public required string Text { get; init; }
}

public sealed class HelpHeadingBlock : HelpBlock
{
    public required string Text { get; init; }
}

public sealed class HelpStepItem
{
    public required string Title { get; init; }
    public required string Body { get; init; }
}

public sealed class HelpStepsBlock : HelpBlock
{
    public required IReadOnlyList<HelpStepItem> Items { get; init; }
}

public sealed class HelpCardItem
{
    public required string Title { get; init; }
    public required string Body { get; init; }
    public string? IconPath { get; init; }
    public string? Meta { get; init; }
}

public sealed class HelpCardsBlock : HelpBlock
{
    public required IReadOnlyList<HelpCardItem> Items { get; init; }
}

public sealed class HelpFaqItem
{
    public required string Question { get; init; }
    public required string Answer { get; init; }
}

public sealed class HelpFaqBlock : HelpBlock
{
    public required IReadOnlyList<HelpFaqItem> Items { get; init; }
}

public sealed class HelpVersionItem
{
    public required string Version { get; init; }
    public required string VersionLabel { get; init; }
    public required string Title { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

public sealed class HelpVersionBlock : HelpBlock
{
    public required IReadOnlyList<HelpVersionItem> Items { get; init; }
}

public sealed class HelpTipBlock : HelpBlock
{
    public required string Title { get; init; }
    public required string Body { get; init; }
}

public sealed class HelpLinkBlock : HelpBlock
{
    public required string Text { get; init; }
    public required string ActionId { get; init; }
}
