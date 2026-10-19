// SPDX-FileCopyrightText: 2026 PlainBytes Studio
// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Imaging;
using PlainBytes.Core.Controls;
using PlainBytes.Core.Store;
using PlainBytes.DocumentExtractor.Resources;
using CoreStrings = PlainBytes.Core.Resources.Strings;

namespace PlainBytes.DocumentExtractor;

/// <summary>
/// DocumentExtractor entry: shared ShellWindow, edition/purchase UI, Extract / Templates / Help / About navigation.
/// </summary>
public partial class App : Application
{
    public const string NavIdHome = "home";
    public const string NavIdTemplates = "templates";
    public const string NavIdHelp = "help";
    public const string NavIdAbout = "about";

    private const string ProductId = "DocumentExtractor";
    private const string ProductIconPackUri =
        "pack://application:,,,/PlainBytes.Core;component/Assets/Icons/logo_DocumentExtractor.ico";

    private MainModule? _mainModule;
    private TemplatesView? _templatesView;
    private HelpCenterView? _helpView;
    private AboutView? _aboutView;
    private ShellWindow? _shell;
    private DocumentExtractorModule? _toolModule;
    private ProductEditionUiHost? _editionHost;
    private bool _forceClose;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _toolModule = new DocumentExtractorModule();
        _toolModule.Initialize();
        _shell = new ShellWindow();
        _editionHost = ProductEditionUiHost.TryCreate(
            _shell,
            ProductId,
            () => Shutdown(),
            () => _toolModule?.ToolName);

        RecreateToolViews();
        ConfigureShellNavigation();
        WireLeaveGuards();

        _shell.SetContent(_mainModule!);
        _shell.SetToolName(_toolModule.ToolName);
        _shell.SetCurrentProductId(ProductId);
        // BitmapImage defaults to the smallest ICO frame (16×16); pick the largest for taskbar.
        _shell.Icon = LoadBestIconFrame(ProductIconPackUri);
        _shell.SelectNav(NavIdHome);

        _shell.CultureChanged += OnShellCultureChanged;

        MainWindow = _shell;
        _shell.Show();

        if (_editionHost is not null)
        {
            _editionHost.AttachWindow();
            _ = _editionHost.InitializeAsync();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _editionHost?.Detach();
        base.OnExit(e);
    }

    /// <summary>Loads the largest frame from a multi-resolution .ico pack URI.</summary>
    private static BitmapFrame LoadBestIconFrame(string packUri)
    {
        var decoder = BitmapDecoder.Create(
            new Uri(packUri, UriKind.Absolute),
            BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad);

        return decoder.Frames
            .OrderByDescending(f => f.PixelWidth * f.PixelHeight)
            .First();
    }

    private void RecreateToolViews()
    {
        _mainModule = (MainModule)_toolModule!.GetMainControl();
        _templatesView = new TemplatesView();
        _helpView = new HelpCenterView();
        _aboutView = new AboutView();
    }

    private void WireLeaveGuards()
    {
        if (_shell is null)
            return;

        _shell.ConfirmBeforeContentChangeAsync = ConfirmLeaveExtractAsync;
        _shell.ContentChangeCancelled += (_, _) => RestoreNavToCurrentContent();
        _shell.Closing += OnShellClosing;
    }

    private async Task<bool> ConfirmLeaveExtractAsync(FrameworkElement nextContent)
    {
        if (_mainModule is null || _shell is null)
            return true;

        if (!ReferenceEquals(_shell.CurrentContent, _mainModule))
            return true;

        if (ReferenceEquals(nextContent, _mainModule))
            return true;

        if (!_mainModule.HasUnsavedChanges)
            return true;

        return await _mainModule.TryConfirmLeaveAsync().ConfigureAwait(true);
    }

    private void RestoreNavToCurrentContent()
    {
        if (_shell is null)
            return;

        var hint = GetSelectedNavHint();
        if (hint is not null)
            _shell.SelectNav(hint);
    }

    private void OnShellClosing(object? sender, CancelEventArgs e)
    {
        if (_forceClose || _shell is null || _mainModule is null)
            return;

        if (!ReferenceEquals(_shell.CurrentContent, _mainModule) || !_mainModule.HasUnsavedChanges)
            return;

        e.Cancel = true;
        _ = CloseAfterConfirmAsync();
    }

    private async Task CloseAfterConfirmAsync()
    {
        if (_shell is null || _mainModule is null)
            return;

        if (!await _mainModule.TryConfirmLeaveAsync().ConfigureAwait(true))
            return;

        _forceClose = true;
        _shell.Close();
    }

    private void ConfigureShellNavigation()
    {
        if (_shell is null)
            return;

        _shell.ConfigureTopNavigation(
        [
            new ShellNavItem(
                NavIdHome,
                Strings.Nav_Home,
                ShellWindow.LoadCoreNavIcon("extractor-w.png"),
                () =>
                {
                    _shell.SelectNav(NavIdHome);
                    _shell.SetContent(_mainModule!);
                }),
            new ShellNavItem(
                NavIdTemplates,
                Strings.Nav_Templates,
                ShellWindow.LoadCoreNavIcon("extractor-template-w.png"),
                () =>
                {
                    _shell.SelectNav(NavIdTemplates);
                    _shell.SetContent(_templatesView!);
                }),
            new ShellNavItem(
                NavIdHelp,
                CoreStrings.Nav_Help,
                ShellWindow.LoadCoreNavIcon("help-w.png"),
                () =>
                {
                    _shell.SelectNav(NavIdHelp);
                    _shell.SetContent(_helpView!);
                }),
            new ShellNavItem(
                NavIdAbout,
                CoreStrings.Nav_About,
                ShellWindow.LoadCoreNavIcon("about-us-w.png"),
                () =>
                {
                    _shell.SelectNav(NavIdAbout);
                    _shell.SetContent(_aboutView!);
                }),
        ]);
    }

    private void OnShellCultureChanged()
    {
        if (_shell is null || _toolModule is null)
            return;

        var selected = GetSelectedNavHint();

        RecreateToolViews();
        ConfigureShellNavigation();
        _shell.SetToolName(_toolModule.ToolName);
        _editionHost?.ApplyCurrentEditionToShell();
        _editionHost?.RefreshOverlayStrings();

        switch (selected)
        {
            case NavIdTemplates:
                _shell.SelectNav(NavIdTemplates);
                _shell.SetContent(_templatesView!);
                break;
            case NavIdHelp:
                _shell.SelectNav(NavIdHelp);
                _shell.SetContent(_helpView!);
                break;
            case NavIdAbout:
                _shell.SelectNav(NavIdAbout);
                _shell.SetContent(_aboutView!);
                break;
            case ShellWindow.NavIdMatrix:
            case ShellWindow.NavIdSupport:
                break;
            default:
                _shell.SelectNav(NavIdHome);
                _shell.SetContent(_mainModule!);
                break;
        }
    }

    private string? GetSelectedNavHint()
    {
        if (_shell?.CurrentContent is TemplatesView)
            return NavIdTemplates;
        if (_shell?.CurrentContent is HelpCenterView)
            return NavIdHelp;
        if (_shell?.CurrentContent is AboutView)
            return NavIdAbout;
        if (_shell?.CurrentContent is SuiteProductsView)
            return ShellWindow.NavIdMatrix;
        if (_shell?.CurrentContent is SupportView)
            return ShellWindow.NavIdSupport;
        if (_shell?.CurrentContent is MainModule)
            return NavIdHome;
        return null;
    }
}
