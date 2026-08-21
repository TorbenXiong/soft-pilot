using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using SoftPilot.Application;
using SoftPilot.Infrastructure.Installation;
using SoftPilot.Infrastructure.Tools;

namespace SoftPilot.Gui;

internal static class ElevatedOperationBroker
{
    internal const string RequestArgument = "--softpilot-elevated-operation-request";
    private const string RequestFilePrefix = "elevated-operation-";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static Task SaveHostsAsync(string content, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            new ElevatedOperationRequest(
                Guid.NewGuid(),
                ElevatedOperationKind.SaveHosts,
                Name: null,
                Value: content,
                Scope: null),
            "Hosts",
            cancellationToken);

    internal static Task SetEnvironmentVariableAsync(
        string name,
        string value,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            new ElevatedOperationRequest(
                Guid.NewGuid(),
                ElevatedOperationKind.SetMachineEnvironmentVariable,
                name,
                value,
                EnvironmentVariableScope.Machine),
            $"系统环境变量 {name}",
            cancellationToken);

    internal static Task DeleteEnvironmentVariableAsync(
        string name,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            new ElevatedOperationRequest(
                Guid.NewGuid(),
                ElevatedOperationKind.DeleteMachineEnvironmentVariable,
                name,
                Value: null,
                Scope: EnvironmentVariableScope.Machine),
            $"系统环境变量 {name}",
            cancellationToken);

    internal static string? ReadRequestPath(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], RequestArgument, StringComparison.Ordinal))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    internal static async Task ProcessRequestAsync(
        string requestPath,
        CancellationToken cancellationToken = default)
    {
        var root = PortableAppMigrator.GetCurrentApplicationRoot();
        var layout = new WindowsInstallationLayout(root);
        requestPath = ValidateRequestPath(layout, requestPath);
        var resultPath = GetResultPath(requestPath);
        ElevatedOperationResult result;
        try
        {
            var request = await ReadRequestAsync(requestPath, cancellationToken);
            await ExecuteRequestAsync(layout, request, cancellationToken);
            result = new ElevatedOperationResult(request.RequestId, Succeeded: true, ErrorMessage: null);
        }
        catch (Exception exception)
        {
            var requestId = await TryReadRequestIdAsync(requestPath, cancellationToken);
            result = new ElevatedOperationResult(requestId, Succeeded: false, exception.Message);
        }

        await File.WriteAllTextAsync(
            resultPath,
            JsonSerializer.Serialize(result, JsonOptions),
            cancellationToken);
        DeleteIfPresent(requestPath);
    }

    private static async Task ExecuteAsync(
        ElevatedOperationRequest request,
        string operationName,
        CancellationToken cancellationToken)
    {
        var executable = PortableAppMigrator.GetCurrentApplicationPath();
        var root = PortableAppMigrator.GetCurrentApplicationRoot();
        var layout = new WindowsInstallationLayout(root);
        layout.EnsureWorkspace();

        var requestPath = Path.Combine(
            layout.StagingDirectory,
            $"{RequestFilePrefix}{request.RequestId:N}.json");
        var resultPath = GetResultPath(requestPath);
        try
        {
            await File.WriteAllTextAsync(
                requestPath,
                JsonSerializer.Serialize(request, JsonOptions),
                cancellationToken);

            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = root,
            };
            startInfo.ArgumentList.Add(RequestArgument);
            startInfo.ArgumentList.Add(requestPath);

            Process? process;
            try
            {
                process = Process.Start(startInfo);
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                throw new SoftPilotException($"已取消管理员授权，{operationName} 未保存。", exception);
            }

            using (process ?? throw new SoftPilotException("无法启动管理员操作进程。"))
            {
                await process.WaitForExitAsync(cancellationToken);
            }

            if (!File.Exists(resultPath))
            {
                throw new SoftPilotException($"管理员操作进程未返回结果，{operationName} 未保存。");
            }

            var result = JsonSerializer.Deserialize<ElevatedOperationResult>(
                await File.ReadAllTextAsync(resultPath, cancellationToken),
                JsonOptions);
            if (result is null || result.RequestId != request.RequestId)
            {
                throw new SoftPilotException("管理员操作进程返回了无效结果。");
            }

            if (!result.Succeeded)
            {
                throw new SoftPilotException(result.ErrorMessage ?? $"管理员保存 {operationName} 失败。");
            }
        }
        finally
        {
            DeleteIfPresent(requestPath);
            DeleteIfPresent(resultPath);
        }
    }

    private static async Task<ElevatedOperationRequest> ReadRequestAsync(
        string requestPath,
        CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<ElevatedOperationRequest>(
            await File.ReadAllTextAsync(requestPath, cancellationToken),
            JsonOptions)
            ?? throw new SoftPilotException("管理员操作请求内容无效。");
        if (request.RequestId == Guid.Empty)
        {
            throw new SoftPilotException("管理员操作请求标识无效。");
        }

        return request;
    }

    private static async Task ExecuteRequestAsync(
        WindowsInstallationLayout layout,
        ElevatedOperationRequest request,
        CancellationToken cancellationToken)
    {
        switch (request.Kind)
        {
            case ElevatedOperationKind.SaveHosts when request.Value is not null:
                await new WindowsHostsFileService(layout).SaveAsync(request.Value, cancellationToken);
                return;
            case ElevatedOperationKind.SetMachineEnvironmentVariable
                when request.Scope == EnvironmentVariableScope.Machine
                     && !string.IsNullOrWhiteSpace(request.Name)
                     && request.Value is not null:
                await new WindowsEnvironmentVariableService().SetAsync(
                    request.Name,
                    request.Value,
                    EnvironmentVariableScope.Machine,
                    cancellationToken);
                return;
            case ElevatedOperationKind.DeleteMachineEnvironmentVariable
                when request.Scope == EnvironmentVariableScope.Machine
                     && !string.IsNullOrWhiteSpace(request.Name):
                await new WindowsEnvironmentVariableService().DeleteAsync(
                    request.Name,
                    EnvironmentVariableScope.Machine,
                    cancellationToken);
                return;
            default:
                throw new SoftPilotException("管理员操作请求类型或参数无效。");
        }
    }

    private static string ValidateRequestPath(WindowsInstallationLayout layout, string requestPath)
    {
        var stagingDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(layout.StagingDirectory));
        var fullPath = Path.GetFullPath(requestPath);
        if (!fullPath.StartsWith(
                stagingDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullPath).StartsWith(RequestFilePrefix, StringComparison.Ordinal)
            || !string.Equals(Path.GetExtension(fullPath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new SoftPilotException("管理员操作请求路径无效。");
        }

        return fullPath;
    }

    private static async Task<Guid> TryReadRequestIdAsync(
        string requestPath,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await ReadRequestAsync(requestPath, cancellationToken)).RequestId;
        }
        catch
        {
            return Guid.Empty;
        }
    }

    private static string GetResultPath(string requestPath) => requestPath + ".result";

    private static void DeleteIfPresent(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A later staging cleanup can remove a transient elevation file.
        }
    }

    private enum ElevatedOperationKind
    {
        SaveHosts,
        SetMachineEnvironmentVariable,
        DeleteMachineEnvironmentVariable,
    }

    private sealed record ElevatedOperationRequest(
        Guid RequestId,
        ElevatedOperationKind Kind,
        string? Name,
        string? Value,
        EnvironmentVariableScope? Scope);

    private sealed record ElevatedOperationResult(Guid RequestId, bool Succeeded, string? ErrorMessage);
}
