// SPDX-FileCopyrightText: 2026 PlainBytes Studio
// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace PlainBytes.DocumentExtractor.Help;

public partial class HelpContentView : UserControl
{
    public static readonly DependencyProperty SectionProperty =
        DependencyProperty.Register(
            nameof(Section),
            typeof(HelpSection),
            typeof(HelpContentView),
            new PropertyMetadata(null, OnSectionChanged));

    public HelpContentView()
    {
        InitializeComponent();
    }

    /// <summary>Raised when a help link block is activated; argument is <see cref="HelpLinkBlock.ActionId"/>.</summary>
    public event Action<string>? LinkActionRequested;

    public HelpSection? Section
    {
        get => (HelpSection?)GetValue(SectionProperty);
        set => SetValue(SectionProperty, value);
    }

    private static void OnSectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not HelpContentView view)
            return;

        var section = e.NewValue as HelpSection;
        view.TitleText.Text = section?.Title ?? "";
        view.BlocksList.ItemsSource = section?.Blocks;
        view.RootScroll.ScrollToHome();
    }

    private void HelpLink_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string actionId } || string.IsNullOrWhiteSpace(actionId))
            return;

        e.Handled = true;
        LinkActionRequested?.Invoke(actionId);
    }
}

public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var invert = parameter as string == "Invert";
        var empty = value is null || (value is string s && string.IsNullOrWhiteSpace(s));
        var visible = !empty;
        if (invert)
            visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
