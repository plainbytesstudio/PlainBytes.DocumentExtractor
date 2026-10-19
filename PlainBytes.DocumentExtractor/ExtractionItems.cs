// SPDX-FileCopyrightText: 2026 PlainBytes Studio
// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace PlainBytes.DocumentExtractor;

internal enum TemplateKind
{
    Custom,
    Existing,
}

/// <summary>
/// What an anchor's child items mean: value fields vs table column headers.
/// </summary>
internal enum AnchorKind
{
    /// <summary>Children are extractable value fields.</summary>
    Value,

    /// <summary>Children are table column headers (column widths).</summary>
    Table,
}

internal sealed class TemplateOption
{
    public TemplateOption(string displayName, TemplateKind kind, string? templateId = null)
    {
        DisplayName = displayName;
        Kind = kind;
        TemplateId = templateId;
    }

    public string DisplayName { get; set; }
    public TemplateKind Kind { get; }
    public string? TemplateId { get; }

    public override string ToString() => DisplayName;
}

/// <summary>Row model for the Templates management list.</summary>
internal sealed class TemplateListItem : INotifyPropertyChanged
{
    private string _name;
    private int _fieldCount;
    private int _anchorCount;
    private DateTime _lastModified;
    private bool _isSelected;
    private bool _isEditing;
    private string? _editError;

    public TemplateListItem(
        string id,
        string name,
        int fieldCount,
        int anchorCount,
        DateTime lastModified)
    {
        Id = id;
        _name = name;
        _fieldCount = fieldCount;
        _anchorCount = anchorCount;
        _lastModified = lastModified;
    }

    public string Id { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
                return;
            _name = value;
            OnPropertyChanged();
        }
    }

    public int FieldCount
    {
        get => _fieldCount;
        set
        {
            if (_fieldCount == value)
                return;
            _fieldCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FieldsText));
        }
    }

    public int AnchorCount
    {
        get => _anchorCount;
        set
        {
            if (_anchorCount == value)
                return;
            _anchorCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AnchorsText));
        }
    }

    public DateTime LastModified
    {
        get => _lastModified;
        set
        {
            if (_lastModified == value)
                return;
            _lastModified = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LastModifiedText));
        }
    }

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

    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value)
                return;
            _isEditing = value;
            if (!_isEditing)
                EditError = null;
            OnPropertyChanged();
        }
    }

    public string? EditError
    {
        get => _editError;
        set
        {
            if (_editError == value)
                return;
            _editError = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasEditError));
        }
    }

    public bool HasEditError => !string.IsNullOrEmpty(_editError);

    public string FieldsText => FieldCount.ToString();
    public string AnchorsText => AnchorCount.ToString();
    public string LastModifiedText => LastModified.ToString("MMM d, yyyy h:mm tt");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Template anchor: stable phrase used to locate a fixed-structure content region.
/// Fields belonging to this region live in <see cref="Fields"/>.
/// </summary>
internal sealed class AnchorItem : INotifyPropertyChanged
{
    private int _index;
    private string _text;
    private bool _isSelected;
    private bool _isExpanded;
    private bool _isListDragging;
    private bool _showDropLineBefore;
    private bool _showDropLineAfter;
    private AnchorKind _kind = AnchorKind.Value;

    public AnchorItem(string id, int index, string text, int pageIndex, Rect pdfArea)
    {
        Id = id;
        _index = index;
        _text = text;
        PageIndex = pageIndex;
        PdfArea = pdfArea;
        Fields = [];
    }

    /// <summary>Stable id persisted in the template and referenced by fields.</summary>
    public string Id { get; }

    public int Index
    {
        get => _index;
        set
        {
            if (_index == value)
                return;
            _index = value;
            OnPropertyChanged();
        }
    }

    /// <summary>0-based PDF page index.</summary>
    public int PageIndex { get; }

    /// <summary>PDF region with origin top-left (Y = distance from page top).</summary>
    /// <remarks>
    /// Value: text-union box (same as <see cref="PhrasePdfArea"/>).
    /// Table: user rubber-band outer header box (not text-fitted); keep in sync with <see cref="HeaderPdfArea"/>.
    /// </remarks>
    public Rect PdfArea { get; set; }

    /// <summary>
    /// Inner phrase box (text-union), same rule for Value and Table.
    /// Locate offset = foundInner.TopLeft − PhrasePdfArea.TopLeft; then applied to fields (Value)
    /// or to the table header outer box / columns (Table).
    /// </summary>
    public Rect PhrasePdfArea { get; set; }

    /// <summary>
    /// Table only: user-drawn header outer box in template space (same as <see cref="PdfArea"/>).
    /// Empty when columns have not been created yet.
    /// </summary>
    public Rect HeaderPdfArea { get; set; }

    /// <summary>Value region (default) or table column definitions.</summary>
    public AnchorKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value)
                return;
            _kind = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsValueKind));
            OnPropertyChanged(nameof(IsTableKind));
            OnPropertyChanged(nameof(TableBadgeVisibility));
        }
    }

    public bool IsValueKind => Kind == AnchorKind.Value;
    public bool IsTableKind => Kind == AnchorKind.Table;

    public Visibility TableBadgeVisibility =>
        Kind == AnchorKind.Table ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Full selected phrase used for matching in other PDFs.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value)
                return;
            _text = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Name));
        }
    }

    /// <summary>Display alias for the selected text (list binding).</summary>
    public string Name => Text;

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

    /// <summary>Only one anchor panel may be expanded; new fields attach to the expanded anchor.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FieldsPanelVisibility));
        }
    }

    public Visibility FieldsPanelVisibility =>
        IsExpanded ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>True while this row is the active drag source (dim during reorder).</summary>
    public bool IsListDragging
    {
        get => _isListDragging;
        set
        {
            if (_isListDragging == value)
                return;
            _isListDragging = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Show insertion line above this row during list reorder.</summary>
    public bool ShowDropLineBefore
    {
        get => _showDropLineBefore;
        set
        {
            if (_showDropLineBefore == value)
                return;
            _showDropLineBefore = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Show insertion line below this row during list reorder.</summary>
    public bool ShowDropLineAfter
    {
        get => _showDropLineAfter;
        set
        {
            if (_showDropLineAfter == value)
                return;
            _showDropLineAfter = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Fields bound to this anchor (fixed structure relative to it).</summary>
    public ObservableCollection<FieldItem> Fields { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Extraction field bound to exactly one <see cref="AnchorItem"/>.</summary>
internal sealed class FieldItem : INotifyPropertyChanged
{
    private int _index;
    private string _name;
    private string _value;
    private bool _isSelected;
    private bool _isEditing;
    private bool _isRulerColumn;
    private bool _isListDragging;
    private bool _showDropLineBefore;
    private bool _showDropLineAfter;

    public FieldItem(
        int index,
        string name,
        string value,
        int pageIndex,
        Rect pdfArea,
        string anchorId)
    {
        _index = index;
        _name = name;
        _value = value;
        PageIndex = pageIndex;
        PdfArea = pdfArea;
        AnchorId = anchorId;
    }

    /// <summary>Owning anchor id (required).</summary>
    public string AnchorId { get; }

    public int Index
    {
        get => _index;
        set
        {
            if (_index == value)
                return;
            _index = value;
            OnPropertyChanged();
        }
    }

    /// <summary>0-based PDF page index.</summary>
    public int PageIndex { get; }

    /// <summary>PDF region with origin top-left (Y = distance from page top).</summary>
    public Rect PdfArea { get; set; }

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
                return;
            _name = value;
            OnPropertyChanged();
        }
    }

    public string Value
    {
        get => _value;
        set
        {
            if (_value == value)
                return;
            _value = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Table-anchor only: this column is the ruler (base) column for row banding.
    /// At most one field per anchor should be true.
    /// </summary>
    public bool IsRulerColumn
    {
        get => _isRulerColumn;
        set
        {
            if (_isRulerColumn == value)
                return;
            _isRulerColumn = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RulerBadgeVisibility));
        }
    }

    public Visibility RulerBadgeVisibility =>
        IsRulerColumn ? Visibility.Visible : Visibility.Collapsed;

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

    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value)
                return;
            _isEditing = value;
            OnPropertyChanged();
        }
    }

    /// <summary>True while this row is the active drag source (dim during reorder).</summary>
    public bool IsListDragging
    {
        get => _isListDragging;
        set
        {
            if (_isListDragging == value)
                return;
            _isListDragging = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Show insertion line above this row during list reorder.</summary>
    public bool ShowDropLineBefore
    {
        get => _showDropLineBefore;
        set
        {
            if (_showDropLineBefore == value)
                return;
            _showDropLineBefore = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Show insertion line below this row during list reorder.</summary>
    public bool ShowDropLineAfter
    {
        get => _showDropLineAfter;
        set
        {
            if (_showDropLineAfter == value)
                return;
            _showDropLineAfter = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
