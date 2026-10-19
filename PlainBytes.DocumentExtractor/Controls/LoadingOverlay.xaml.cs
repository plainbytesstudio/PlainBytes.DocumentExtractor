// SPDX-FileCopyrightText: 2026 PlainBytes Studio
// SPDX-License-Identifier: GPL-3.0-only
using System.Windows.Controls;

namespace PlainBytes.DocumentExtractor.Controls;

public partial class LoadingOverlay : UserControl
{
    public LoadingOverlay()
    {
        InitializeComponent();
    }

    public void SetMessage(string message) => MessageText.Text = message;
}
