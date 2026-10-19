// SPDX-FileCopyrightText: 2026 PlainBytes Studio
// SPDX-License-Identifier: GPL-3.0-only
namespace PlainBytes.DocumentExtractor.Help;

internal static class HelpCatalog
{
    public static IReadOnlyList<HelpSection> GetSections() =>
    [
        BuildQuickStart(),
        BuildOverview(),
        BuildDocuments(),
        BuildExtraction(),
        BuildTemplates(),
        BuildFaq(),
        BuildCustomDev(),
        BuildPrivacy(),
        BuildTips(),
        BuildChangelog(),
    ];

    private static string T(string key) => HelpText.Get(key);

    private static HelpSection BuildQuickStart() => new()
    {
        Id = "QuickStart",
        Title = T("Section.QuickStart.Title"),
        Blocks =
        [
            new HelpDescriptionBlock { Text = T("QuickStart.Description") },
            new HelpTipBlock { Title = T("QuickStart.Tip.Title"), Body = T("QuickStart.Tip.Body") },
            new HelpStepsBlock
            {
                Items =
                [
                    new HelpStepItem { Title = T("QuickStart.Step1.Title"), Body = T("QuickStart.Step1.Body") },
                    new HelpStepItem { Title = T("QuickStart.Step2.Title"), Body = T("QuickStart.Step2.Body") },
                    new HelpStepItem { Title = T("QuickStart.Step3.Title"), Body = T("QuickStart.Step3.Body") },
                    new HelpStepItem { Title = T("QuickStart.Step4.Title"), Body = T("QuickStart.Step4.Body") },
                    new HelpStepItem { Title = T("QuickStart.Step5.Title"), Body = T("QuickStart.Step5.Body") },
                ],
            },
        ],
    };

    private static HelpSection BuildOverview() => new()
    {
        Id = "Overview",
        Title = T("Section.Overview.Title"),
        Blocks =
        [
            new HelpDescriptionBlock { Text = T("Overview.Description") },
            new HelpCardsBlock
            {
                Items =
                [
                    new HelpCardItem { Title = T("Overview.Card1.Title"), Body = T("Overview.Card1.Body") },
                    new HelpCardItem { Title = T("Overview.Card2.Title"), Body = T("Overview.Card2.Body") },
                    new HelpCardItem { Title = T("Overview.Card3.Title"), Body = T("Overview.Card3.Body") },
                    new HelpCardItem { Title = T("Overview.Card4.Title"), Body = T("Overview.Card4.Body") },
                    new HelpCardItem { Title = T("Overview.Card5.Title"), Body = T("Overview.Card5.Body") },
                    new HelpCardItem { Title = T("Overview.Card6.Title"), Body = T("Overview.Card6.Body") },
                ],
            },
        ],
    };

    private static HelpSection BuildDocuments() => new()
    {
        Id = "Documents",
        Title = T("Section.Documents.Title"),
        Blocks =
        [
            new HelpDescriptionBlock { Text = T("Documents.Description") },
            new HelpHeadingBlock { Text = T("Documents.Supported.Heading") },
            new HelpDescriptionBlock { Text = T("Documents.Supported.Description") },
            new HelpHeadingBlock { Text = T("Documents.Unsupported.Heading") },
            new HelpDescriptionBlock { Text = T("Documents.Unsupported.Description") },
            new HelpTipBlock { Title = T("Documents.Tip.Title"), Body = T("Documents.Tip.Body") },
        ],
    };

    private static HelpSection BuildExtraction() => new()
    {
        Id = "Extraction",
        Title = T("Section.Extraction.Title"),
        Blocks =
        [
            new HelpDescriptionBlock { Text = T("Extraction.Description") },
            new HelpHeadingBlock { Text = T("Extraction.Template.Heading") },
            new HelpDescriptionBlock { Text = T("Extraction.Template.Description") },
            new HelpHeadingBlock { Text = T("Extraction.Anchors.Heading") },
            new HelpDescriptionBlock { Text = T("Extraction.Anchors.Description") },
            new HelpHeadingBlock { Text = T("Extraction.ValueAnchors.Heading") },
            new HelpDescriptionBlock { Text = T("Extraction.ValueAnchors.Description") },
            new HelpHeadingBlock { Text = T("Extraction.TableAnchors.Heading") },
            new HelpDescriptionBlock { Text = T("Extraction.TableAnchors.Description") },
            new HelpHeadingBlock { Text = T("Extraction.Fields.Heading") },
            new HelpDescriptionBlock { Text = T("Extraction.Fields.Description") },
            new HelpHeadingBlock { Text = T("Extraction.Preview.Heading") },
            new HelpDescriptionBlock { Text = T("Extraction.Preview.Description") },
            new HelpHeadingBlock { Text = T("Extraction.Excel.Heading") },
            new HelpDescriptionBlock { Text = T("Extraction.Excel.Description") },
        ],
    };

    private static HelpSection BuildTemplates() => new()
    {
        Id = "Templates",
        Title = T("Section.Templates.Title"),
        Blocks =
        [
            new HelpDescriptionBlock { Text = T("Templates.Description") },
            new HelpHeadingBlock { Text = T("Templates.Browse.Heading") },
            new HelpDescriptionBlock { Text = T("Templates.Browse.Description") },
            new HelpHeadingBlock { Text = T("Templates.Import.Heading") },
            new HelpDescriptionBlock { Text = T("Templates.Import.Description") },
            new HelpHeadingBlock { Text = T("Templates.Export.Heading") },
            new HelpDescriptionBlock { Text = T("Templates.Export.Description") },
            new HelpHeadingBlock { Text = T("Templates.Rename.Heading") },
            new HelpDescriptionBlock { Text = T("Templates.Rename.Description") },
            new HelpHeadingBlock { Text = T("Templates.Delete.Heading") },
            new HelpDescriptionBlock { Text = T("Templates.Delete.Description") },
            new HelpHeadingBlock { Text = T("Templates.Storage.Heading") },
            new HelpDescriptionBlock { Text = T("Templates.Storage.Description") },
        ],
    };

    private static HelpSection BuildFaq() => new()
    {
        Id = "Faq",
        Title = T("Section.Faq.Title"),
        Blocks =
        [
            new HelpFaqBlock
            {
                Items =
                [
                    new HelpFaqItem { Question = T("Faq.Q1"), Answer = T("Faq.A1") },
                    new HelpFaqItem { Question = T("Faq.Q2"), Answer = T("Faq.A2") },
                    new HelpFaqItem { Question = T("Faq.Q3"), Answer = T("Faq.A3") },
                    new HelpFaqItem { Question = T("Faq.Q4"), Answer = T("Faq.A4") },
                    new HelpFaqItem { Question = T("Faq.Q5"), Answer = T("Faq.A5") },
                    new HelpFaqItem { Question = T("Faq.Q6"), Answer = T("Faq.A6") },
                    new HelpFaqItem { Question = T("Faq.Q7"), Answer = T("Faq.A7") },
                    new HelpFaqItem { Question = T("Faq.Q8"), Answer = T("Faq.A8") },
                    new HelpFaqItem { Question = T("Faq.Q9"), Answer = T("Faq.A9") },
                    new HelpFaqItem { Question = T("Faq.Q10"), Answer = T("Faq.A10") },
                    new HelpFaqItem { Question = T("Faq.Q11"), Answer = T("Faq.A11") },
                    new HelpFaqItem { Question = T("Faq.Q12"), Answer = T("Faq.A12") },
                ],
            },
        ],
    };

    private static HelpSection BuildCustomDev() => new()
    {
        Id = "CustomDev",
        Title = T("Section.CustomDev.Title"),
        Blocks =
        [
            new HelpDescriptionBlock { Text = T("CustomDev.Description") },
            new HelpLinkBlock
            {
                Text = T("CustomDev.Contact"),
                ActionId = "CustomDevContact",
            },
        ],
    };

    private static HelpSection BuildPrivacy() => new()
    {
        Id = "Privacy",
        Title = T("Section.Privacy.Title"),
        Blocks =
        [
            new HelpHeadingBlock { Text = T("Privacy.Local.Heading") },
            new HelpDescriptionBlock { Text = T("Privacy.Local.Description") },
            new HelpHeadingBlock { Text = T("Privacy.Selected.Heading") },
            new HelpDescriptionBlock { Text = T("Privacy.Selected.Description") },
            new HelpHeadingBlock { Text = T("Privacy.Templates.Heading") },
            new HelpDescriptionBlock { Text = T("Privacy.Templates.Description") },
            new HelpHeadingBlock { Text = T("Privacy.Excel.Heading") },
            new HelpDescriptionBlock { Text = T("Privacy.Excel.Description") },
            new HelpTipBlock { Title = T("Privacy.Tip.Title"), Body = T("Privacy.Tip.Body") },
        ],
    };

    private static HelpSection BuildTips() => new()
    {
        Id = "Tips",
        Title = T("Section.Tips.Title"),
        Blocks =
        [
            new HelpTipBlock { Title = T("Tips.1.Title"), Body = T("Tips.1.Body") },
            new HelpTipBlock { Title = T("Tips.2.Title"), Body = T("Tips.2.Body") },
            new HelpTipBlock { Title = T("Tips.3.Title"), Body = T("Tips.3.Body") },
            new HelpTipBlock { Title = T("Tips.4.Title"), Body = T("Tips.4.Body") },
            new HelpTipBlock { Title = T("Tips.5.Title"), Body = T("Tips.5.Body") },
            new HelpTipBlock { Title = T("Tips.6.Title"), Body = T("Tips.6.Body") },
            new HelpTipBlock { Title = T("Tips.7.Title"), Body = T("Tips.7.Body") },
            new HelpTipBlock { Title = T("Tips.8.Title"), Body = T("Tips.8.Body") },
            new HelpTipBlock { Title = T("Tips.9.Title"), Body = T("Tips.9.Body") },
        ],
    };

    private static HelpSection BuildChangelog() => new()
    {
        Id = "Changelog",
        Title = T("Section.Changelog.Title"),
        Blocks =
        [
            new HelpVersionBlock
            {
                Items =
                [
                    new HelpVersionItem
                    {
                        Version = "1.0.0",
                        VersionLabel = string.Format(
                            System.Globalization.CultureInfo.CurrentUICulture,
                            T("Changelog.VersionFormat"),
                            "1.0.0"),
                        Title = T("Changelog.V100.Title"),
                        Notes =
                        [
                            T("Changelog.V100.N1"),
                            T("Changelog.V100.N2"),
                            T("Changelog.V100.N3"),
                            T("Changelog.V100.N4"),
                            T("Changelog.V100.N5"),
                            T("Changelog.V100.N6"),
                            T("Changelog.V100.N7"),
                            T("Changelog.V100.N8"),
                            T("Changelog.V100.N9"),
                            T("Changelog.V100.N10"),
                            T("Changelog.V100.N11"),
                            T("Changelog.V100.N12"),
                        ],
                    },
                ],
            },
        ],
    };
}
