// SPDX-FileCopyrightText: 2026 PlainBytes Studio
// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PlainBytes.Core.Controls;
using PlainBytes.DocumentExtractor.Controls;
using PlainBytes.DocumentExtractor.Help;

namespace PlainBytes.DocumentExtractor;

public partial class HelpCenterView : UserControl
{
    private const double BodyCornerRadius = 12;
    private const string CustomDevContactAction = "CustomDevContact";

    private readonly IReadOnlyList<HelpSection> _sections;

    public HelpCenterView()
    {
        InitializeComponent();

        PageTitleText.Text = HelpText.Get("Page.Title");
        PageSubtitleText.Text = HelpText.Get("Page.Subtitle");
        CatalogLabelText.Text = HelpText.Get("Nav.Catalog");

        _sections = HelpCatalog.GetSections();
        TopicList.ItemsSource = _sections;
        if (_sections.Count > 0)
            TopicList.SelectedIndex = 0;

        ContentHost.LinkActionRequested += OnHelpLinkActionRequested;
    }

    private void OnHelpLinkActionRequested(string actionId)
    {
        if (actionId != CustomDevContactAction)
            return;
        if (Window.GetWindow(this) is not ShellWindow shell)
            return;

        var overlay = new CustomDevelopmentOverlay();
        overlay.Closed += () => shell.HideOverlay();
        shell.ShowOverlay(overlay);
    }

    private void BodyContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (BodyContent.ActualWidth <= 0 || BodyContent.ActualHeight <= 0)
            return;

        BodyContent.Clip = new RectangleGeometry(
            new Rect(0, 0, BodyContent.ActualWidth, BodyContent.ActualHeight),
            BodyCornerRadius,
            BodyCornerRadius);
    }

    private void TopicList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContentHost == null)
            return;

        if (TopicList.SelectedItem is HelpSection section)
            ContentHost.Section = section;
    }
}
