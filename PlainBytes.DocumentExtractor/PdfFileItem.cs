// SPDX-FileCopyrightText: 2026 PlainBytes Studio
// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PlainBytes.DocumentExtractor;

internal sealed class PdfFileItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public PdfFileItem(string filePath, string fileName, string metaLine, long sizeBytes)
    {
        FilePath = filePath;
        FileName = fileName;
        MetaLine = metaLine;
        SizeBytes = sizeBytes;
    }

    public string FilePath { get; }
    public string FileName { get; }
    public string MetaLine { get; }
    public long SizeBytes { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
