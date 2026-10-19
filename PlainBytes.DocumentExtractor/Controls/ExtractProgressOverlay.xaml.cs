// SPDX-FileCopyrightText: 2026 PlainBytes Studio
// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using PlainBytes.DocumentExtractor.Resources;

namespace PlainBytes.DocumentExtractor.Controls;

internal sealed class OverlayFailItem
{
    public OverlayFailItem(string fileName, string reason, bool isLast = false)
    {
        FileName = fileName;
        Reason = reason;
        IsLast = isLast;
    }

    public string FileName { get; }
    public string Reason { get; }
    public bool IsLast { get; }
}

public partial class ExtractProgressOverlay : UserControl
{
    private const string SuccessIcon = "pack://application:,,,/PlainBytes.Core;component/Assets/Images/success.png";
    private const string WarningIcon = "pack://application:,,,/PlainBytes.Core;component/Assets/Images/warning.png";
    private const string ErrorIcon = "pack://application:,,,/PlainBytes.Core;component/Assets/Images/error.png";
    private const double CollapsedHeight = 86;
    private const double ExpandedMaxHeight = 260;
    private const double BottomGap = 10;
    private const double SeparatorBlock = 21;
    private const double FailRowHeight = 28;

    public event Action? DismissRequested;

    private int _total;
    private int _current;
    private bool _isDismissing;
    private bool _isComplete;
    private bool _failListVisible;
    private string? _outputExcelPath;
    private CancellationTokenSource? _cts;

    public ExtractProgressOverlay()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public CancellationToken CancellationToken =>
        _cts?.Token ?? CancellationToken.None;

    public void Show(int totalFiles)
    {
        _isDismissing = false;
        _isComplete = false;
        _failListVisible = false;
        _outputExcelPath = null;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        _total = Math.Max(totalFiles, 1);
        _current = 0;
        TitleText.Text = Strings.Overlay_Working;
        FileNameText.Text = string.Empty;
        ActionButton.Content = Strings.Overlay_Cancel;
        ActionButton.Visibility = Visibility.Visible;
        ViewReasonButton.Visibility = Visibility.Collapsed;
        ViewExcelButton.Visibility = Visibility.Collapsed;
        Ring.Visibility = Visibility.Visible;
        StatusImage.Visibility = Visibility.Collapsed;
        BarTrack.Visibility = Visibility.Visible;
        CountText.Visibility = Visibility.Visible;
        HideFailList();
        FailList.ItemsSource = null;
        ResetTopAccent();
        UpdateProgress(0, 0, _total);
        Visibility = Visibility.Visible;
        AnimateSheet(open: true);
        Dispatcher.BeginInvoke(new Action(() => ActionButton.Focus()),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    public void Hide()
    {
        BeginDismiss(cancel: true);
    }

    public void UpdateProgress(double percent, int current, int total, string? currentFile = null)
    {
        if (_isComplete)
            return;

        _current = current;
        _total = Math.Max(total, 1);
        var pct = Math.Clamp(percent, 0, 100);
        Ring.Progress = pct;
        CountText.Text = $"{_current} / {_total}";
        if (!string.IsNullOrWhiteSpace(currentFile))
            FileNameText.Text = System.IO.Path.GetFileName(currentFile);
        UpdateBarWidth(pct);
    }

    internal void Complete(
        int successCount,
        int failCount,
        IReadOnlyList<ExtractFileResult>? results = null,
        string? outputExcelPath = null)
    {
        if (_isDismissing)
            return;

        _isComplete = true;
        _outputExcelPath = outputExcelPath;
        successCount = Math.Max(successCount, 0);
        failCount = Math.Max(failCount, 0);

        TitleText.Text = Strings.Overlay_Complete;
        BarTrack.Visibility = Visibility.Collapsed;
        CountText.Visibility = Visibility.Collapsed;
        Ring.Visibility = Visibility.Collapsed;
        StatusImage.Visibility = Visibility.Visible;
        ActionButton.Content = Strings.Overlay_Done;
        HideFailList();

        var failItems = (results ?? [])
            .Where(r => !r.Success)
            .Select(r => new OverlayFailItem(
                Path.GetFileName(r.Path),
                FormatFailReason(r.Error)))
            .ToList();
        if (failItems.Count > 0)
            failItems[^1] = new OverlayFailItem(failItems[^1].FileName, failItems[^1].Reason, isLast: true);
        FailList.ItemsSource = failItems;

        ViewExcelButton.Visibility =
            !string.IsNullOrWhiteSpace(_outputExcelPath) && File.Exists(_outputExcelPath)
                ? Visibility.Visible
                : Visibility.Collapsed;

        if (failCount == 0)
        {
            FileNameText.Text = Strings.Format(Strings.Overlay_SuccessCount, successCount);
            StatusImage.Source = LoadIcon(SuccessIcon);
            ViewReasonButton.Visibility = Visibility.Collapsed;
            ActionButton.Visibility = Visibility.Visible;
            SetTopAccent("#20c997");
        }
        else if (successCount == 0)
        {
            TitleText.Text = Strings.Overlay_Failed;
            FileNameText.Text = Strings.Format(Strings.Overlay_FailCount, failCount);
            StatusImage.Source = LoadIcon(ErrorIcon);
            ViewReasonButton.Visibility = Visibility.Visible;
            ActionButton.Visibility = Visibility.Visible;
            SetTopAccent("#fa5252");
        }
        else
        {
            FileNameText.Text =
                $"{Strings.Format(Strings.Overlay_SuccessCount, successCount).TrimEnd('.')} ; {Strings.Format(Strings.Overlay_FailCount, failCount)}";
            StatusImage.Source = LoadIcon(WarningIcon);
            ViewReasonButton.Visibility = Visibility.Visible;
            ActionButton.Visibility = Visibility.Visible;
            SetTopAccent("#fcc419");
        }
    }

    private void SetTopAccent(string hex)
    {
        TopAccent.Background = (Brush)new BrushConverter().ConvertFrom(hex)!;
        TopAccent.Visibility = Visibility.Visible;
        SheetPanel.BorderThickness = new Thickness(1, 0, 1, 1);
    }

    private void ResetTopAccent()
    {
        TopAccent.Visibility = Visibility.Collapsed;
        SheetPanel.BorderThickness = new Thickness(1);
        SheetPanel.BorderBrush = (Brush)new BrushConverter().ConvertFrom("#E6E8EB")!;
    }

    private void ShowFailList()
    {
        if (_failListVisible)
            return;

        _failListVisible = true;
        FailSeparator.Visibility = Visibility.Visible;
        FailListScroll.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(ResizeFailList, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ResizeFailList()
    {
        var maxByWindow = ActualHeight > BottomGap
            ? ActualHeight - BottomGap
            : ExpandedMaxHeight;
        var maxPanel = Math.Min(ExpandedMaxHeight, maxByWindow);
        var maxList = Math.Max(FailRowHeight, maxPanel - CollapsedHeight - SeparatorBlock);

        FailListScroll.Height = double.NaN;
        FailListScroll.UpdateLayout();

        var contentWidth = FailListScroll.ViewportWidth > 0
            ? FailListScroll.ViewportWidth
            : FailListScroll.ActualWidth;
        var contentHeight = FailRowHeight;
        if (FailListScroll.Content is FrameworkElement content && contentWidth > 0)
        {
            content.Measure(new Size(contentWidth, double.PositiveInfinity));
            contentHeight = content.DesiredSize.Height;
        }

        var needsScroll = contentHeight > maxList;
        var listHeight = needsScroll ? maxList : contentHeight;

        FailListScroll.Height = listHeight;
        FailListScroll.VerticalScrollBarVisibility = needsScroll
            ? ScrollBarVisibility.Auto
            : ScrollBarVisibility.Hidden;
        SheetPanel.Height = CollapsedHeight + SeparatorBlock + listHeight;
        SheetPanel.Margin = new Thickness(0, 0, 0, BottomGap);
    }

    private void HideFailList()
    {
        _failListVisible = false;
        FailSeparator.Visibility = Visibility.Collapsed;
        FailListScroll.Visibility = Visibility.Collapsed;
        FailListScroll.Height = double.NaN;
        FailListScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        SheetPanel.Height = CollapsedHeight;
        SheetPanel.Margin = new Thickness(0, 0, 0, BottomGap);
    }

    private static string FormatFailReason(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return Strings.FailReason_Generic;

        var text = error.Trim();
        if (ContainsAny(text, "being used by another process", "file is in use"))
            return Strings.FailReason_InUse;
        if (ContainsAny(text, "FileNotFound", "could not find"))
            return Strings.FailReason_NotFound;
        if (ContainsAny(text, "Unauthorized", "Access is denied", "access denied"))
            return Strings.FailReason_AccessDenied;

        return text;
    }

    private static bool ContainsAny(string text, params string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (!string.IsNullOrEmpty(token)
                && text.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static BitmapImage LoadIcon(string packUri)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(packUri, UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void UpdateBarWidth(double percent)
    {
        var pct = Math.Clamp(percent, 0, 100);
        BarFillColumn.Width = new GridLength(pct, GridUnitType.Star);
        BarRestColumn.Width = new GridLength(100 - pct, GridUnitType.Star);
    }

    private void BeginDismiss(bool cancel)
    {
        if (_isDismissing)
            return;

        _isDismissing = true;
        if (cancel)
            _cts?.Cancel();
        AnimateSheet(open: false, onCompleted: () =>
        {
            Visibility = Visibility.Collapsed;
            DismissRequested?.Invoke();
        });
    }

    private void AnimateSheet(bool open, Action? onCompleted = null)
    {
        var from = open ? 80 : 0;
        var to = open ? 0 : 80;
        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new QuadraticEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn },
        };
        if (onCompleted != null)
            anim.Completed += (_, _) => onCompleted();
        SheetTranslate.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && !_isComplete)
            UpdateProgress(Ring.Progress, _current, _total);
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        BeginDismiss(cancel: !_isComplete);
    }

    private void ViewReasonButton_Click(object sender, RoutedEventArgs e)
    {
        ShowFailList();
    }

    private void ViewExcelButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (string.IsNullOrWhiteSpace(_outputExcelPath) || !File.Exists(_outputExcelPath))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _outputExcelPath,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Ignore launch failures (no association / access denied).
        }
    }

    private void Mask_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }
}
