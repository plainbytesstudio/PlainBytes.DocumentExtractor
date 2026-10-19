// SPDX-FileCopyrightText: 2026 PlainBytes Studio
// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PlainBytes.DocumentExtractor.Resources;

namespace PlainBytes.DocumentExtractor.Controls;

/// <summary>
/// Modal: choose Value vs Table when creating an anchor.
/// Table requires a column count (dividers are placed on the left for manual drag).
/// </summary>
internal partial class AnchorKindPickerOverlay : UserControl
{
    public const int DefaultTableColumnCount = 3;
    public const int MinTableColumnCount = 2;
    public const int MaxTableColumnCount = 30;

    /// <summary>Kind + column count (count used only for <see cref="AnchorKind.Table"/>).</summary>
    public event Action<AnchorKind, int>? KindChosen;
    public event Action? Cancelled;

    public AnchorKindPickerOverlay()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ColumnCountBox.Value = DefaultTableColumnCount;
            ValueButton.Focus();
        };
    }

    private void ValueButton_Click(object sender, RoutedEventArgs e) =>
        KindChosen?.Invoke(AnchorKind.Value, 0);

    private void TableButton_Click(object sender, RoutedEventArgs e)
    {
        TableColumnPanel.Visibility = Visibility.Visible;
        ValueButton.BorderBrush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xE2, 0xE8, 0xF0));
        ValueButton.BorderThickness = new Thickness(1);
        TableButton.BorderBrush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x25, 0x63, 0xEB));
        TableButton.BorderThickness = new Thickness(1.5);
        ColumnCountBox.Focus();
    }

    private void ConfirmTableButton_Click(object sender, RoutedEventArgs e) =>
        ConfirmTable();

    private void ConfirmTable()
    {
        var raw = ColumnCountBox.Value ?? DefaultTableColumnCount;
        var n = (int)Math.Round(raw);
        n = Math.Clamp(n, MinTableColumnCount, MaxTableColumnCount);
        KindChosen?.Invoke(AnchorKind.Table, n);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        Cancelled?.Invoke();

    private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancelled?.Invoke();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter)
            return;

        if (TableColumnPanel.Visibility == Visibility.Visible)
            ConfirmTable();
        else
            KindChosen?.Invoke(AnchorKind.Value, 0);
        e.Handled = true;
    }
}
