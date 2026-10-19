// SPDX-FileCopyrightText: 2026 PlainBytes Studio
// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using PlainBytes.Core.Controls;
using PlainBytes.DocumentExtractor.Controls;
using PlainBytes.DocumentExtractor.Resources;
using UglyToad.PdfPig;

namespace PlainBytes.DocumentExtractor;

/// <summary>Extract home: drop zone + workspace (Files / PDF Preview / Extraction).</summary>
public partial class MainModule
{
    /// <summary>Discrete zoom steps for the PDF preview (25% … 200%).</summary>
    private static readonly double[] ZoomSteps =
    [
        0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0,
    ];

    /// <summary>PDF point → WPF DIP (96 DPI / 72 pt).</summary>
    private const double PointToDip = 96.0 / 72.0;

    /// <summary>Fallback page width in DIP when metrics are not yet known (A4-ish).</summary>
    private const double DefaultPageWidthDip = 595;

    /// <summary>Fallback page height in DIP when metrics are not yet known (A4-ish).</summary>
    private const double DefaultPageHeightDip = 842;

    /// <summary>Pdfium raster DPI used as the 100% zoom baseline.</summary>
    private const double BaseRenderDpi = 300.0;

    /// <summary>Size of each field-resize knob in preview DIP.</summary>
    private const double FieldResizeKnobSize = 8.0;

    /// <summary>Minimum field box size while resizing (preview DIP).</summary>
    private const double FieldResizeMinPreview = 4.0;

    /// <summary>Extra hit padding around resize knobs (preview DIP).</summary>
    private const double FieldResizeHitSlop = 2.0;

    /// <summary>Show full-window loading overlay when adding at least this many PDFs.</summary>
    private const int LoadingOverlayThreshold = 1;

    /// <summary>Corner radius of the three-column workspace chrome.</summary>
    private const double WorkspaceCornerRadius = 12;

    // --- UI collections / services ---

    /// <summary>Files list on the left (current batch).</summary>
    private readonly ObservableCollection<PdfFileItem> _files = [];

    /// <summary>Serializes AddFilesAsync so overlapping drops/picks do not race.</summary>
    private readonly SemaphoreSlim _fileLoadGate = new(1, 1);

    /// <summary>Anchors (and nested fields) for the current extraction rules.</summary>
    private readonly ObservableCollection<AnchorItem> _anchors = [];

    /// <summary>Off-UI-thread PDF page rasterizer (Pdfium).</summary>
    private readonly PdfPreviewRenderer _previewRenderer = new();

    /// <summary>Debounces zoom-driven re-renders.</summary>
    private readonly DispatcherTimer _zoomRenderTimer;

    /// <summary>Auto-hides the action toast after a short delay.</summary>
    private readonly DispatcherTimer _actionToastTimer;

    // --- Drag-over / template / leave / name-edit ---

    /// <summary>Empty-state drop zone is highlighting a PDF drag.</summary>
    private bool _dropZoneDragOver;

    /// <summary>Files column is highlighting a PDF drag.</summary>
    private bool _filesColumnDragOver;

    /// <summary>Current rules differ from the last applied/saved template.</summary>
    private bool _templateDirty;

    /// <summary>Ignore TemplateCombo SelectionChanged while rebuilding items.</summary>
    private bool _suppressTemplateChange;

    /// <summary>Ignore TemplateStore.Changed while we ourselves mutate the store.</summary>
    private bool _suppressStoreRefresh;

    /// <summary>Outside-click hook for closing the in-page template dropdown is attached.</summary>
    private bool _templateComboOutsideCloseHooked;

    /// <summary>Template option currently applied to the UI (Custom or Existing).</summary>
    private TemplateOption? _appliedTemplateOption;

    /// <summary>Unsaved-changes leave dialog is open (blocks re-entry).</summary>
    private bool _leavePromptOpen;

    /// <summary>Value/Table anchor-kind picker overlay is open.</summary>
    private bool _anchorKindPromptOpen;

    /// <summary>Completes when Save-as flyout finishes (leave/save path).</summary>
    private TaskCompletionSource<bool>? _saveAsCompletion;

    /// <summary>Field name before the current inline rename started.</summary>
    private string? _fieldNameEditOriginal;

    /// <summary>True when the rename session is for a just-created field (cancel may delete).</summary>
    private bool _fieldNameEditIsNew;

    /// <summary>Dirty flag snapshot before creating a new field that enters name edit.</summary>
    private bool _dirtyBeforeNewFieldEdit;

    // --- Sample PDF preview ---

    /// <summary>Currently selected sample PDF in the files list.</summary>
    private PdfFileItem? _selected;

    /// <summary>Page count of the selected PDF.</summary>
    private int _pageCount = 1;

    /// <summary>1-based page shown in the preview.</summary>
    private int _currentPage = 1;

    /// <summary>Current page width in PDF points.</summary>
    private double _pageWidthPt = DefaultPageWidthDip / PointToDip;

    /// <summary>Current page height in PDF points.</summary>
    private double _pageHeightPt = DefaultPageHeightDip / PointToDip;

    /// <summary>Preview zoom factor (1.0 = BaseRenderDpi fit scale).</summary>
    private double _zoom = 1.0;

    /// <summary>Monotonic id so stale raster completions are ignored.</summary>
    private int _renderRequestId;

    /// <summary>Cancels an in-flight page raster when page/zoom/file changes.</summary>
    private CancellationTokenSource? _renderCts;

    // --- Rubber-band create / field resize / table column dividers ---

    /// <summary>User is dragging a create rubber-band on the selection layer.</summary>
    private bool _isSelecting;

    /// <summary>Current rubber-band creates an anchor (Ctrl) rather than a field.</summary>
    private bool _selectionCreatesAnchor;

    /// <summary>Rubber-band start point in SelectionLayer coordinates.</summary>
    private Point _selectionStart;

    /// <summary>Batch extract is running (chrome locked).</summary>
    private bool _isExtracting;

    /// <summary>User is dragging a value-field resize handle.</summary>
    private bool _isResizingField;

    /// <summary>Field being resized.</summary>
    private FieldItem? _resizeField;

    /// <summary>Which resize handle is active.</summary>
    private FieldResizeHandleKind _resizeHandle;

    /// <summary>Field preview rect at resize start.</summary>
    private Rect _resizeStartPreview;

    /// <summary>Mouse position at resize start (SelectionLayer).</summary>
    private Point _resizeStartMouse;

    /// <summary>Cached PdfPig doc while resizing (avoids reopen each move).</summary>
    private PdfDocument? _resizePdfDocument;

    /// <summary>User is dragging a table header column divider.</summary>
    private bool _isDraggingTableDivider;

    /// <summary>Table anchor whose divider is being dragged.</summary>
    private AnchorItem? _dividerTableAnchor;

    /// <summary>0-based divider index between columns (after column i).</summary>
    private int _dividerIndex;

    /// <summary>Columns ordered left-to-right for the active divider drag.</summary>
    private List<FieldItem>? _dividerDragColumns;

    /// <summary>Live divider overlay element while dragging.</summary>
    private FrameworkElement? _dividerDragVisual;

    /// <summary>Live selected-column highlight while dragging a divider.</summary>
    private FrameworkElement? _dividerSelectedColVisual;

    /// <summary>Selected column index during divider drag (−1 if none).</summary>
    private int _dividerSelectedColIndex = -1;

    /// <summary>Hit-test slop for table dividers in preview DIP.</summary>
    private const double TableDividerHitSlopDip = 6.0;

    // --- Panel list reorder (anchors / fields) ---

    /// <summary>Mouse position when a list-row drag may start.</summary>
    private Point _listDragStart;

    /// <summary>AnchorItem or FieldItem being dragged for reorder.</summary>
    private object? _listDragPayload;

    /// <summary>Drag has moved past the threshold (real DnD, not a click).</summary>
    private bool _listDragMoved;

    /// <summary>Row currently showing the drop insert line.</summary>
    private object? _listDropAdornmentItem;

    /// <summary>Last rubber-band rect in SelectionLayer coords (toast placement).</summary>
    private Rect? _lastSelectionPreview;

    /// <summary>
    /// Per-anchor display offsets for the selected file (origin top-left), keyed by anchor id.
    /// Updated when overlay geometry is resolved.
    /// </summary>
    private Dictionary<string, (double Dx, double Dy)> _overlayOffsetsByAnchorId = new(StringComparer.Ordinal);

    /// <summary>Rubber-band stroke for field create.</summary>
    private static readonly SolidColorBrush FieldRubberStroke = FreezeBrush(Color.FromRgb(0xE5, 0x48, 0x4D));

    /// <summary>Rubber-band fill for field create.</summary>
    private static readonly SolidColorBrush FieldRubberFill = FreezeBrush(Color.FromArgb(0x28, 0xE5, 0x48, 0x4D));

    /// <summary>Rubber-band stroke for anchor create.</summary>
    private static readonly SolidColorBrush AnchorRubberStroke = FreezeBrush(Color.FromRgb(0xE5, 0x48, 0x4D));

    /// <summary>Rubber-band fill for anchor create.</summary>
    private static readonly SolidColorBrush AnchorRubberFill = FreezeBrush(Color.FromArgb(0x28, 0xE5, 0x48, 0x4D));

    /// <summary>Wires list sources, timers, template store, and initial chrome.</summary>
    public MainModule()
    {
        InitializeComponent();
        FileListItems.ItemsSource = _files;
        AnchorListItems.ItemsSource = _anchors;

        _zoomRenderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _zoomRenderTimer.Tick += (_, _) =>
        {
            _zoomRenderTimer.Stop();
            _ = RefreshPreviewAsync(force: true, showLoading: false);
        };
        _actionToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _actionToastTimer.Tick += (_, _) =>
        {
            _actionToastTimer.Stop();
            HideActionToast();
        };

        Unloaded += (_, _) =>
        {
            _zoomRenderTimer.Stop();
            _actionToastTimer.Stop();
            _renderCts?.Cancel();
            _previewRenderer.Dispose();
            DisposeResizePdfDocument();
        };

        InitTemplateOptions();
        TemplateStore.Changed += OnTemplateStoreChanged;
        UpdateExtractionHeaders();
        UpdateExtractionActions();
        UpdateWorkspaceVisibility();
        UpdatePreviewChrome();
        SyncTemplateComboText();
    }

    private enum ActionToastKind
    {
        Warning,
        Error,
        Success,
    }

    /// <summary>
    /// Bubble toast near a button (above) or selection rect (above).
    /// Defaults to Extract button when no placement is given.
    /// </summary>
    private void ShowActionToast(
        string message,
        ActionToastKind kind,
        FrameworkElement? nearElement = null,
        Rect? nearPreview = null)
    {
        Debug.WriteLine($"[ShowActionToast] start {DateTime.Now:HH:mm:ss.fff}");
        ActionToastText.Text = message;
        ActionToast.Background = FreezeBrush(Color.FromRgb(0xFE, 0xF3, 0xC7));
        ActionToast.BorderBrush = FreezeBrush(Color.FromRgb(0xFC, 0xD3, 0x4D));
        ActionToastCaret.Fill = ActionToast.Background;
        ActionToastCaret.Stroke = ActionToast.BorderBrush;
        ActionToastText.Foreground = FreezeBrush(Color.FromRgb(0x33, 0x41, 0x55));
        ActionToastText.FontWeight = FontWeights.Normal;
        ActionToastIcon.Foreground = FreezeBrush(Color.FromRgb(0x64, 0x74, 0x8B));

        if (kind == ActionToastKind.Success)
            ActionToastIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24;
        else if (kind == ActionToastKind.Error)
            ActionToastIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.ErrorCircle24;
        else
            ActionToastIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Info24;

        var preview = nearPreview ?? _lastSelectionPreview;
        if (nearElement is not null)
            PlaceToastAboveElement(nearElement);
        else if (preview is { Width: > 0, Height: > 0 } rect)
            PlaceToastAbovePreviewRect(rect);
        else
            PlaceToastAboveElement(ExtractButton);

        ActionToastPopup.IsOpen = true;
        _actionToastTimer.Stop();
        _actionToastTimer.Start();
        Debug.WriteLine($"[ShowActionToast] end   {DateTime.Now:HH:mm:ss.fff}");
    }

    /// <summary>Places the bubble above a control (e.g. Extract button).</summary>
    private void PlaceToastAboveElement(FrameworkElement target)
    {
        ActionToastPopup.PlacementTarget = target;
        ActionToastPopup.Placement = PlacementMode.Top;
        ActionToastPopup.HorizontalOffset = 0;
        ActionToastPopup.VerticalOffset = -6;
        ActionToastCaret.Visibility = Visibility.Visible;
        ActionToastCaret.RenderTransform = null;
    }

    /// <summary>Places the bubble above a rubber-band / header rect in SelectionLayer coords.</summary>
    private void PlaceToastAbovePreviewRect(Rect previewRect)
    {
        ActionToastPopup.PlacementTarget = SelectionLayer;
        ActionToastPopup.Placement = PlacementMode.Relative;
        ActionToastCaret.Visibility = Visibility.Visible;
        ActionToastCaret.RenderTransform = null;

        ActionToastBubble.Measure(new Size(320, double.PositiveInfinity));
        var size = ActionToastBubble.DesiredSize;
        if (size.Width < 8 || size.Height < 8)
            size = new Size(260, 44);

        var layerW = Math.Max(SelectionLayer.ActualWidth, 1);
        var x = previewRect.Left + previewRect.Width / 2 - size.Width / 2;
        var y = previewRect.Top - size.Height - 8;
        if (y < 4)
            y = previewRect.Bottom + 8;

        x = Math.Clamp(x, 8, Math.Max(8, layerW - size.Width - 8));
        ActionToastPopup.HorizontalOffset = x;
        ActionToastPopup.VerticalOffset = y;
    }

    /// <summary>Hide the action toast.</summary>
    private void HideActionToast()
    {
        _actionToastTimer.Stop();
        ActionToastPopup.IsOpen = false;
    }

    /// <summary>Nearest Button/ui:Button ancestor, if any.</summary>
    private static Button? FindAncestorButton(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is Button button)
                return button;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    /// <summary>Nearest TextBox ancestor, if any (ignore row click while renaming).</summary>
    private static TextBox? FindAncestorTextBox(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is TextBox box)
                return box;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    /// <summary>Idle drop zone vs three-column workspace.</summary>
    private void UpdateWorkspaceVisibility()
    {
        var hasFiles = _files.Count > 0;

        DropZone.Visibility = hasFiles ? Visibility.Collapsed : Visibility.Visible;
        WorkspacePanel.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        UpdateFilesHeader();
    }

    /// <summary>
    /// CornerRadius only paints a round border; clip column fills so square corners don't poke out.
    /// </summary>
    private void WorkspaceColumns_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (WorkspaceColumns.ActualWidth <= 0 || WorkspaceColumns.ActualHeight <= 0)
            return;

        WorkspaceColumns.Clip = new RectangleGeometry(
            new Rect(0, 0, WorkspaceColumns.ActualWidth, WorkspaceColumns.ActualHeight),
            WorkspaceCornerRadius,
            WorkspaceCornerRadius);
    }

    /// <summary>Human-readable file size for the files list meta line.</summary>
    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";

        double kb = bytes / 1024.0;
        if (kb < 1024)
            return string.Format(CultureInfo.InvariantCulture, "{0:0} KB", kb);

        double mb = kb / 1024.0;
        return string.Format(CultureInfo.InvariantCulture, "{0:0.##} MB", mb);
    }

    /// <summary>True if node is under ancestor in the visual tree.</summary>
    private static bool IsDescendantOf(DependencyObject? ancestor, DependencyObject? node)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor))
                return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }
}
