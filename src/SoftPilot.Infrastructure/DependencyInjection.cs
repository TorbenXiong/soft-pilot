using Microsoft.Extensions.DependencyInjection;
using SoftPilot.Infrastructure.Detection;
using SoftPilot.Infrastructure.Diagnostics;
using SoftPilot.Infrastructure.Installation;
using SoftPilot.Infrastructure.IO;
using SoftPilot.Infrastructure.Providers;
using SoftPilot.Infrastructure.Runtime;
using SoftPilot.Infrastructure.Security;
using SoftPilot.Infrastructure.Shell;
using SoftPilot.Infrastructure.State;

namespace SoftPilot.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddSoftPilot(this IServiceCollection services, string? root = null)
    {
        var registry = new WindowsRootRegistry();
        root ??= Environment.GetEnvironmentVariable("SOFTPILOT_ROOT");
        root ??= registry.ReadRoot();
        root ??= InstallationRootResolver.Resolve(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"));

        services.AddSingleton<IRootRegistry>(registry);
        services.AddSingleton<IInstallationLayout>(new WindowsInstallationLayout(root));
        services.AddSingleton<IInstallationPathService, WindowsInstallationPathService>();
        services.AddSingleton(new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        }));
        services.AddSingleton<ProcessRunner>();
        services.AddSingleton<IDownloadService, HttpDownloadService>();
        services.AddSingleton<ISignatureVerificationService, BouncyCastleSignatureVerificationService>();
        services.AddSingleton<IStateStore, SqliteStateStore>();
        services.AddSingleton<IRuntimeModulePreferencesStore, JsonRuntimeModulePreferencesStore>();
        services.AddSingleton<WindowsDirectoryLinkService>();
        services.AddSingleton<GlobalRuntimeService>();
        services.AddSingleton<IGlobalRuntimeService>(provider => provider.GetRequiredService<GlobalRuntimeService>());
        services.AddSingleton<IShellIntegrationService, WindowsShellIntegrationService>();
        services.AddSingleton<IOperationCoordinator, OperationCoordinator>();
        services.AddSingleton<ICacheService, CacheService>();
        services.AddSingleton<IDoctorService, DoctorService>();

        services.AddSingleton<IRuntimeProvider, NodeRuntimeProvider>();
        services.AddSingleton<IRuntimeProvider, TemurinRuntimeProvider>();
        services.AddSingleton<IRuntimeProvider, PythonRuntimeProvider>();

        services.AddSingleton<IExternalRuntimeDetector>(provider =>
            new WindowsExternalRuntimeDetector(RuntimeKind.Node, provider.GetRequiredService<ProcessRunner>()));
        services.AddSingleton<IExternalRuntimeDetector>(provider =>
            new WindowsExternalRuntimeDetector(RuntimeKind.Java, provider.GetRequiredService<ProcessRunner>()));
        services.AddSingleton<IExternalRuntimeDetector>(provider =>
            new WindowsExternalRuntimeDetector(RuntimeKind.Python, provider.GetRequiredService<ProcessRunner>()));
        return services;
    }

    public static async Task InitializeSoftPilotAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var layout = services.GetRequiredService<IInstallationLayout>();
        layout.EnsureWorkspace();
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SOFTPILOT_ROOT")))
        {
            services.GetRequiredService<IRootRegistry>().WriteRoot(layout.Root);
        }

        await services.GetRequiredService<IStateStore>().InitializeAsync(cancellationToken);
    }
}
