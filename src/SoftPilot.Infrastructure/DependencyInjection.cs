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
using SoftPilot.Infrastructure.Tools;

namespace SoftPilot.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddSoftPilot(this IServiceCollection services, string? root = null)
    {
        var registry = new WindowsRootRegistry();
        root ??= Environment.GetEnvironmentVariable("SOFTPILOT_ROOT");
        root ??= registry.ReadRoot();
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new SoftPilotException("尚未指定 SoftPilot 工作区。请先启动 SoftPilot.exe 完成首次设置。");
        }

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
        services.AddSingleton<WindowsTcpListenerProcessResolver>();
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
        services.AddSingleton<IGitService, GitService>();

        services.AddSingleton<RuntimeCatalogCache>();
        services.AddSingleton<PythonInstallManagerProvisioner>();
        services.AddSingleton<NodeRuntimeProvider>();
        services.AddSingleton<TemurinRuntimeProvider>();
        services.AddSingleton<PythonRuntimeProvider>();
        services.AddSingleton<RedisRuntimeProvider>();
        services.AddSingleton<RedisServiceManager>();
        services.AddSingleton<IRedisServiceManager>(provider => provider.GetRequiredService<RedisServiceManager>());
        services.AddSingleton<IRuntimeProvider>(provider => new CachedRuntimeProvider(
            provider.GetRequiredService<NodeRuntimeProvider>(),
            provider.GetRequiredService<RuntimeCatalogCache>()));
        services.AddSingleton<IRuntimeProvider>(provider => new CachedRuntimeProvider(
            provider.GetRequiredService<TemurinRuntimeProvider>(),
            provider.GetRequiredService<RuntimeCatalogCache>()));
        services.AddSingleton<IRuntimeProvider>(provider => new CachedRuntimeProvider(
            provider.GetRequiredService<PythonRuntimeProvider>(),
            provider.GetRequiredService<RuntimeCatalogCache>()));
        services.AddSingleton<IRuntimeProvider>(provider => new CachedRuntimeProvider(
            provider.GetRequiredService<RedisRuntimeProvider>(),
            provider.GetRequiredService<RuntimeCatalogCache>()));

        services.AddSingleton<IExternalRuntimeDetector>(provider =>
            new WindowsExternalRuntimeDetector(
                RuntimeKind.Node,
                provider.GetRequiredService<ProcessRunner>(),
                provider.GetRequiredService<IInstallationLayout>()));
        services.AddSingleton<IExternalRuntimeDetector>(provider =>
            new WindowsExternalRuntimeDetector(
                RuntimeKind.Java,
                provider.GetRequiredService<ProcessRunner>(),
                provider.GetRequiredService<IInstallationLayout>()));
        services.AddSingleton<IExternalRuntimeDetector>(provider =>
            new WindowsExternalRuntimeDetector(
                RuntimeKind.Python,
                provider.GetRequiredService<ProcessRunner>(),
                provider.GetRequiredService<IInstallationLayout>()));
        services.AddSingleton<IExternalRuntimeDetector>(provider =>
            new WindowsExternalRuntimeDetector(
                RuntimeKind.Redis,
                provider.GetRequiredService<ProcessRunner>(),
                provider.GetRequiredService<IInstallationLayout>()));
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
        await services.GetRequiredService<ICacheService>().CleanExpiredAsync(cancellationToken);
        await services.GetRequiredService<GlobalRuntimeService>()
            .ReconcileShellIntegrationAsync(cancellationToken);
    }
}
