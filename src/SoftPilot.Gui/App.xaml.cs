using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SoftPilot.Application.Abstractions;
using SoftPilot.Gui.ViewModels;
using SoftPilot.Infrastructure;

namespace SoftPilot.Gui;

public partial class App : Microsoft.UI.Xaml.Application
{
    private readonly IHost _host;
    private Window? _window;

    public App()
    {
        InitializeComponent();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSoftPilot();
        builder.Services.AddSingleton<MainViewModel>();
        _host = builder.Build();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            await _host.Services.InitializeSoftPilotAsync();
            _window = new MainWindow(_host.Services.GetRequiredService<MainViewModel>());
            _window.Activate();
        }
        catch (Exception exception)
        {
            WriteStartupFailure(exception);
            _window = new Window
            {
                Title = "SoftPilot",
                Content = new TextBlock
                {
                    Text = $"SoftPilot 无法启动：{exception.Message}",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(32),
                },
            };
            _window.Activate();
        }
    }

    private void WriteStartupFailure(Exception exception)
    {
        try
        {
            var layout = _host.Services.GetRequiredService<IInstallationLayout>();
            Directory.CreateDirectory(layout.LogsDirectory);
            File.AppendAllText(
                Path.Combine(layout.LogsDirectory, "gui-startup.log"),
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Startup reporting must never replace the original UI error.
        }
    }
}
