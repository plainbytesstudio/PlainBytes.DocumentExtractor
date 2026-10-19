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

public partial class MainModule
{
    /// <summary>Validate first; only then pick a path and extract.</summary>
    private void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExtracting || _files.Count == 0)
            return;

        if (!EnumerateFields().Any())
        {
            ShowActionToast(Strings.Toast_NeedField, ActionToastKind.Warning, nearElement: ExtractButton);
            return;
        }

        var tableWithoutRuler = FindTableMissingRulerColumn();
        if (tableWithoutRuler is not null)
        {
            ShowActionToast(Strings.Toast_NeedRulerColumn, ActionToastKind.Warning, nearElement: ExtractButton);
            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                () => ExpandAnchor(tableWithoutRuler));
            return;
        }

        _ = RunExtractionAsync();
    }

    /// <summary>Pick output path, extract all PDFs, write Excel, show progress.</summary>
    private async Task RunExtractionAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = Strings.Dialog_ExportResults,
            Filter = Strings.Dialog_FilterExcel,
            DefaultExt = ".xlsx",
            AddExtension = true,
            FileName = "ExtractionResults.xlsx",
        };

        if (dialog.ShowDialog() != true)
            return;

        if (Window.GetWindow(this) is not ShellWindow shell)
            return;

        var outputPath = dialog.FileName;
        var filePaths = _files.Select(f => f.FilePath).ToArray();
        var anchors = _anchors
            .Select(a => new AnchorSpec(
                a.Id,
                a.Text,
                a.PageIndex,
                a.PdfArea,
                a.Kind,
                a.PhrasePdfArea.Width > 0.5 ? a.PhrasePdfArea : null))
            .ToArray();
        var fields = EnumerateFields()
            .Select(f => new FieldSpec(f.Name, f.PageIndex, f.PdfArea, f.AnchorId, f.IsRulerColumn))
            .ToArray();

        _isExtracting = true;
        SetExtractionUiEnabled(enabled: false);

        var overlay = new ExtractProgressOverlay();
        overlay.DismissRequested += () => shell.HideOverlay();
        shell.ShowOverlay(overlay);
        overlay.Show(filePaths.Length);

        var progress = new Progress<ExtractProgressReport>(report =>
        {
            var pct = report.Total == 0 ? 100 : report.Completed * 100d / report.Total;
            overlay.UpdateProgress(pct, report.Completed, report.Total, report.CurrentFile);
        });

        try
        {
            var results = await Task.Run(() =>
                ExtractionService.ExtractBatch(
                    filePaths,
                    anchors,
                    fields,
                    outputPath,
                    progress,
                    overlay.CancellationToken));
            if (!overlay.CancellationToken.IsCancellationRequested)
            {
                var successCount = results.Count(r => r.Success);
                var failed = results.Count - successCount;
                overlay.Complete(successCount, failed, results, outputPath);
            }
        }
        catch (OperationCanceledException)
        {
            // User cancelled via overlay.
        }
        catch (Exception ex)
        {
            shell.HideOverlay();
            ShowActionToast(ex.Message, ActionToastKind.Error, nearElement: ExtractButton);
        }
        finally
        {
            _isExtracting = false;
            SetExtractionUiEnabled(enabled: true);
        }
    }

    /// <summary>
    /// First table that has columns but no ruler (base) column set.
    /// Extract requires an explicit ruler; save does not.
    /// </summary>
    private AnchorItem? FindTableMissingRulerColumn()
    {
        foreach (var anchor in _anchors)
        {
            if (anchor.Kind != AnchorKind.Table || anchor.Fields.Count == 0)
                continue;
            if (!anchor.Fields.Any(f => f.IsRulerColumn))
                return anchor;
        }

        return null;
    }

    /// <summary>Disable chrome while extraction runs.</summary>
    private void SetExtractionUiEnabled(bool enabled)
    {
        ExtractButton.IsEnabled = enabled;
        SecondaryActionHost.IsEnabled = enabled;
        DropZone.IsEnabled = enabled;
        WorkspacePanel.IsEnabled = enabled;
    }

    /// <summary>Anchors/fields counts and empty hint.</summary>
    private void UpdateExtractionHeaders()
    {
        var fieldCount = EnumerateFields().Count();

        AnchorsHeaderText.Text =
            $"{Strings.Format(Strings.Panel_AnchorsCount, _anchors.Count)} · {Strings.Format(Strings.Panel_FieldsCount, fieldCount)}";
        AnchorsEmptyHint.Visibility = _anchors.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Coverage toast retired: fields always belong to an anchor.
        HidePageAnchorToast();
    }

    /// <summary>Hide legacy page-anchor toast host.</summary>
    private void HidePageAnchorToast()
    {
        PageAnchorToast.Visibility = Visibility.Collapsed;
    }

    /// <summary>Dismiss page-anchor toast.</summary>
    private void PageAnchorToastClose_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        HidePageAnchorToast();
    }

    /// <summary>Secondary save button label/visibility by template state.</summary>
    private void UpdateExtractionActions()
    {
        var option = GetSelectedTemplate();
        if (option is null)
            return;

        if (option.Kind == TemplateKind.Custom)
        {
            SecondaryActionHost.Visibility = Visibility.Visible;
            SecondaryActionText.Text = Strings.Extraction_SaveAsTemplate;
            SecondaryActionDivider.Visibility = Visibility.Collapsed;
            SecondaryActionMenuButton.Visibility = Visibility.Collapsed;
            Grid.SetColumn(ExtractButton, 2);
            Grid.SetColumnSpan(ExtractButton, 1);
            return;
        }

        if (_templateDirty)
        {
            SecondaryActionHost.Visibility = Visibility.Visible;
            SecondaryActionText.Text = Strings.Extraction_SaveChanges;
            SecondaryActionDivider.Visibility = Visibility.Visible;
            SecondaryActionMenuButton.Visibility = Visibility.Visible;
            Grid.SetColumn(ExtractButton, 2);
            Grid.SetColumnSpan(ExtractButton, 1);
            return;
        }
        CloseSaveTemplateFlyout();

        SecondaryActionHost.Visibility = Visibility.Collapsed;
        SecondaryActionDivider.Visibility = Visibility.Collapsed;
        SecondaryActionMenuButton.Visibility = Visibility.Collapsed;
        Grid.SetColumn(ExtractButton, 0);
        Grid.SetColumnSpan(ExtractButton, 3);
    }
}
