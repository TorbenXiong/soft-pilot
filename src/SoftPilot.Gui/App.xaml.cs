using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SoftPilot.Application;
using SoftPilot.Application.Abstractions;
using SoftPilot.Gui.ViewModels;
using SoftPilot.Infrastructure;
using SoftPilot.Infrastructure.Installation;
using SoftPilot.Infrastructure.State;

namespace SoftPilot.Gui;

public partial class App : Microsoft.UI.Xaml.Application
{
    private IHost? _host;
    private Window? _window;

    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLineArguments = Environment.GetCommandLineArgs();
        var elevatedOperationRequest = ElevatedOperationBroker.ReadRequestPath(commandLineArguments);
        if (elevatedOperationRequest is not null)
        {
            await ElevatedOperationBroker.ProcessRequestAsync(elevatedOperationRequest);
            Exit();
            return;
        }

        string? root = null;
        Exception? cleanupFailure = null;
        WorkspaceSetupWindow? setupWindow = null;
        var createDesktopShortcut = false;
        try
        {
            var migrator = new PortableAppMigrator();
            var sourceExecutable = PortableAppMigrator.GetCurrentApplicationPath();
            var sourceRoot = PortableAppMigrator.GetCurrentApplicationRoot();
            cleanupFailure = await TryCleanupSourceAsync(
                migrator,
                sourceRoot,
                commandLineArguments);

            var registry = new WindowsRootRegistry();
            var sourceIsInitialized = IsInitializedPortableRoot(sourceRoot);
            root = ResolveConfiguredRoot(registry) ?? (sourceIsInitialized ? sourceRoot : null);
            if (root is not null && !PortableAppMigrator.PathsEqual(sourceRoot, root))
            {
                var registeredExecutable = Path.Combine(root, "SoftPilot.exe");
                if (File.Exists(registeredExecutable) && HasValidWorkspaceMarker(root))
                {
                    Launch(registeredExecutable, root);
                    Exit();
                    return;
                }

                root = sourceIsInitialized ? sourceRoot : null;
            }

            if (root is null)
            {
                setupWindow = new WorkspaceSetupWindow(new WindowsInstallationPathService());
                _window = setupWindow;
                setupWindow.Activate();
                root = await setupWindow.WaitForSelectionAsync();
                createDesktopShortcut = setupWindow.CreateDesktopShortcut;
                if (root is null)
                {
                    _window = null;
                    Exit();
                    return;
                }

                await InitializePreferencesAsync(root, setupWindow.SelectedLanguageCode);

                var targetExecutable = Path.Combine(root, "SoftPilot.exe");
                if (!PortableAppMigrator.PathsEqual(sourceExecutable, targetExecutable))
                {
                    await migrator.MigrateAsync(sourceExecutable, targetExecutable);
                    registry.WriteRoot(root);
                    TryCreateDesktopShortcut(targetExecutable, root);
                    Launch(
                        Path.Combine(root, "SoftPilot.exe"),
                        root,
                        "--softpilot-cleanup-source",
                        sourceExecutable,
                        "--softpilot-parent-pid",
                        Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    setupWindow.CloseAfterSubmission();
                    _window = null;
                    Exit();
                    return;
                }
            }

            var layout = new WindowsInstallationLayout(root);
            layout.EnsureWorkspace();
            await ProvisionPortableToolsAsync(layout);
            if (createDesktopShortcut)
            {
                TryCreateDesktopShortcut(Path.Combine(root, "SoftPilot.exe"), root);
            }

            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddSoftPilot(root);
            builder.Services.AddSingleton<MainViewModel>();
            _host = builder.Build();
            await _host.Services.InitializeSoftPilotAsync();
            _window = new MainWindow(_host.Services.GetRequiredService<MainViewModel>());
            _window.Activate();
            setupWindow?.CloseAfterSubmission();

            if (cleanupFailure is not null)
            {
                WriteStartupFailure(cleanupFailure);
            }
        }
        catch (Exception exception)
        {
            WriteStartupFailure(exception, root);
            var errorWindow = new Window
            {
                Title = "SoftPilot",
                Content = new TextBlock
                {
                    Text = $"SoftPilot could not start: {exception.Message}",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(32),
                },
            };
            _window = errorWindow;
            errorWindow.Activate();
            setupWindow?.CloseAfterSubmission();
        }
    }

    private static string? ResolveConfiguredRoot(IRootRegistry registry)
    {
        var root = Environment.GetEnvironmentVariable("SOFTPILOT_ROOT");
        root ??= registry.ReadRoot();
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private void TryCreateDesktopShortcut(string targetExecutable, string root)
    {
        try
        {
            DesktopShortcutService.Create(targetExecutable);
        }
        catch (Exception exception)
        {
            WriteStartupFailure(
                new SoftPilotException($"SoftPilot 已启动，但创建桌面快捷方式失败：{exception.Message}", exception),
                root);
        }
    }

    private static async Task ProvisionPortableToolsAsync(
        IInstallationLayout layout,
        CancellationToken cancellationToken = default)
    {
        const string resourceName = "SoftPilot.Gui.PortableTools.zip";
        var archive = typeof(App).Assembly.GetManifestResourceStream(resourceName);
        if (archive is null)
        {
            return;
        }

        await new PortableToolsProvisioner()
            .ProvisionAsync(archive, layout.ToolsDirectory, cancellationToken);
    }

    private static async Task InitializePreferencesAsync(string root, string language)
    {
        var layout = new WindowsInstallationLayout(root);
        layout.EnsureWorkspace();
        if (File.Exists(Path.Combine(layout.DataDirectory, "ui-preferences.json")))
        {
            return;
        }

        var preferences = RuntimeModulePreferences.Default with { Language = language };
        await new JsonRuntimeModulePreferencesStore(layout).SaveAsync(preferences);
    }

    private static bool IsInitializedPortableRoot(string root) =>
        File.Exists(Path.Combine(root, "SoftPilot.exe")) && HasValidWorkspaceMarker(root);

    private static bool HasValidWorkspaceMarker(string root)
    {
        try
        {
            var marker = Path.Combine(
                root,
                WindowsInstallationLayout.ManagementDirectoryName,
                WindowsInstallationLayout.WorkspaceMarkerName);
            return File.Exists(marker)
                && string.Equals(
                    File.ReadAllText(marker).Trim(),
                    "SoftPilot workspace",
                    StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<Exception?> TryCleanupSourceAsync(
        PortableAppMigrator migrator,
        string targetRoot,
        IReadOnlyList<string> args)
    {
        var sourceExecutable = ReadArgument(args, "--softpilot-cleanup-source");
        if (sourceExecutable is null)
        {
            return null;
        }

        try
        {
            var parentIdText = ReadArgument(args, "--softpilot-parent-pid");
            if (int.TryParse(parentIdText, out var parentId))
            {
                try
                {
                    using var parent = Process.GetProcessById(parentId);
                    await parent.WaitForExitAsync();
                }
                catch (ArgumentException)
                {
                    // The source process already exited.
                }
            }

            await migrator.CleanupSourceExecutableAsync(
                sourceExecutable,
                Path.Combine(targetRoot, "SoftPilot.exe"));
            return null;
        }
        catch (Exception exception)
        {
            return new SoftPilotException($"应用已迁移，但源目录清理失败：{exception.Message}", exception);
        }
    }

    private static string? ReadArgument(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static void Launch(string executable, string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new SoftPilotException($"无法启动迁移后的应用：{executable}");
        }
    }

    private void WriteStartupFailure(Exception exception, string? root = null)
    {
        try
        {
            var logs = _host is null
                ? root is null ? null : new WindowsInstallationLayout(root).LogsDirectory
                : _host.Services.GetRequiredService<IInstallationLayout>().LogsDirectory;
            if (logs is null)
            {
                return;
            }

            Directory.CreateDirectory(logs);
            File.AppendAllText(
                Path.Combine(logs, "gui-startup.log"),
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Startup reporting must never replace the original UI error.
        }
    }
}
