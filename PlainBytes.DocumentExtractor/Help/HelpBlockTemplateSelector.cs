// SPDX-FileCopyrightText: 2026 PlainBytes Studio
// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;

namespace PlainBytes.DocumentExtractor.Help;

public sealed class HelpBlockTemplateSelector : DataTemplateSelector
{
    public DataTemplate? DescriptionTemplate { get; set; }
    public DataTemplate? HeadingTemplate { get; set; }
    public DataTemplate? StepsTemplate { get; set; }
    public DataTemplate? CardsTemplate { get; set; }
    public DataTemplate? FaqTemplate { get; set; }
    public DataTemplate? VersionTemplate { get; set; }
    public DataTemplate? TipTemplate { get; set; }
    public DataTemplate? LinkTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item switch
        {
            HelpDescriptionBlock => DescriptionTemplate,
            HelpHeadingBlock => HeadingTemplate,
            HelpStepsBlock => StepsTemplate,
            HelpCardsBlock => CardsTemplate,
            HelpFaqBlock => FaqTemplate,
            HelpVersionBlock => VersionTemplate,
            HelpTipBlock => TipTemplate,
            HelpLinkBlock => LinkTemplate,
            _ => base.SelectTemplate(item, container),
        };
}
