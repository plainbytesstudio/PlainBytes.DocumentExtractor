// SPDX-FileCopyrightText: 2026 PlainBytes Studio
// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PlainBytes.DocumentExtractor.Controls;

internal enum UnsavedChangesChoice
{
    Cancel,
    Discard,
    Save,
}

/// <summary>Modal: save / discard / cancel when leaving with dirty template rules.</summary>
internal partial class UnsavedChangesOverlay : UserControl
{
    public event Action<UnsavedChangesChoice>? ChoiceMade;

    public UnsavedChangesOverlay()
    {
        InitializeComponent();
        Loaded += (_, _) => SaveButton.Focus();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) =>
        ChoiceMade?.Invoke(UnsavedChangesChoice.Save);

    private void DiscardButton_Click(object sender, RoutedEventArgs e) =>
        ChoiceMade?.Invoke(UnsavedChangesChoice.Discard);

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        ChoiceMade?.Invoke(UnsavedChangesChoice.Cancel);

    private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ChoiceMade?.Invoke(UnsavedChangesChoice.Cancel);
            e.Handled = true;
        }
    }
}
