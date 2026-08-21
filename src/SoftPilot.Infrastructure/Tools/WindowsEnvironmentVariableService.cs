using System.Collections;
using System.Runtime.InteropServices;

namespace SoftPilot.Infrastructure.Tools;

public sealed class WindowsEnvironmentVariableService : IEnvironmentVariableService
{
    private const uint WmSettingChange = 0x001A;
    private static readonly nint HwndBroadcast = new(0xffff);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<EnvironmentVariableEntry>> GetAllAsync(
        EnvironmentVariableScope scope,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Environment.GetEnvironmentVariables(ToTarget(scope))
                .Cast<DictionaryEntry>()
                .Select(item => new EnvironmentVariableEntry(
                    item.Key?.ToString() ?? string.Empty,
                    item.Value?.ToString() ?? string.Empty,
                    scope))
                .Where(item => item.Name.Length > 0)
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(
        string name,
        string value,
        EnvironmentVariableScope scope,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = ValidateName(name);
        ArgumentNullException.ThrowIfNull(value);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Environment.SetEnvironmentVariable(normalizedName, value, ToTarget(scope));
            BroadcastEnvironmentChange();
        }
        catch (UnauthorizedAccessException exception)
        {
            throw CreatePermissionException(scope, exception);
        }
        catch (System.Security.SecurityException exception)
        {
            throw CreatePermissionException(scope, exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(
        string name,
        EnvironmentVariableScope scope,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = ValidateName(name);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Environment.SetEnvironmentVariable(normalizedName, null, ToTarget(scope));
            BroadcastEnvironmentChange();
        }
        catch (UnauthorizedAccessException exception)
        {
            throw CreatePermissionException(scope, exception);
        }
        catch (System.Security.SecurityException exception)
        {
            throw CreatePermissionException(scope, exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string ValidateName(string name)
    {
        var normalized = name?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new SoftPilotException("环境变量名称不能为空。");
        }

        if (normalized.Length > 255)
        {
            throw new SoftPilotException("环境变量名称不能超过 255 个字符。");
        }

        if (normalized.Contains('=') || normalized.Contains('\0'))
        {
            throw new SoftPilotException("环境变量名称不能包含等号或空字符。");
        }

        return normalized;
    }

    private static EnvironmentVariableTarget ToTarget(EnvironmentVariableScope scope) => scope switch
    {
        EnvironmentVariableScope.User => EnvironmentVariableTarget.User,
        EnvironmentVariableScope.Machine => EnvironmentVariableTarget.Machine,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
    };

    private static SoftPilotException CreatePermissionException(
        EnvironmentVariableScope scope,
        Exception innerException) =>
        scope == EnvironmentVariableScope.Machine
            ? new AdministratorPrivilegesRequiredException(
                "修改系统环境变量需要管理员权限。",
                innerException)
            : new SoftPilotException(
                "当前 Windows 用户无权修改用户环境变量。",
                innerException);

    private static void BroadcastEnvironmentChange()
    {
        _ = SendMessageTimeout(
            HwndBroadcast,
            WmSettingChange,
            0,
            "Environment",
            0x0002,
            5000,
            out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeout(
        nint hWnd,
        uint msg,
        nuint wParam,
        string lParam,
        uint flags,
        uint timeout,
        out nuint result);
}
