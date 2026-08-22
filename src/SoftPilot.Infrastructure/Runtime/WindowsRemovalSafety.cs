namespace SoftPilot.Infrastructure.Runtime;

internal static class WindowsRemovalSafety
{
    public static void EnsurePathsAreDeletable(
        IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        var files = new List<string>();
        var pending = new Stack<string>(paths
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase));
        while (pending.TryPop(out var path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
                using var handle = CreateFile(
                    path,
                    DeleteAccess,
                    FileShare.ReadWrite | FileShare.Delete,
                    nint.Zero,
                    FileMode.Open,
                    attributes.HasFlag(FileAttributes.Directory) ? BackupSemantics : 0,
                    nint.Zero);
                if (handle.IsInvalid)
                {
                    ThrowDeletionPreflightFailure(path);
                }
            }
            catch (SoftPilotException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new SoftPilotException(
                    $"无法确认卸载目标可安全删除：{path}。请检查文件权限后重试。",
                    exception);
            }

            if (!attributes.HasFlag(FileAttributes.Directory)
                || attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (!attributes.HasFlag(FileAttributes.Directory))
                {
                    files.Add(path);
                }

                continue;
            }

            try
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(path))
                {
                    pending.Push(child);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new SoftPilotException(
                    $"无法检查卸载目标：{path}。请检查文件权限后重试。",
                    exception);
            }
        }

        EnsureFilesAreNotInUse(files, cancellationToken);
    }

    private static void EnsureFilesAreNotInUse(
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        const int batchSize = 256;
        for (var offset = 0; offset < files.Count; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = files.Skip(offset).Take(batchSize).ToArray();
            if (!HasRestartManagerLocks(batch))
            {
                continue;
            }

            foreach (var file in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (HasRestartManagerLocks([file]))
                {
                    throw new SoftPilotException(
                        $"卸载目标正在使用：{file}。请先关闭使用此版本的终端、开发工具和进程，然后重试卸载。");
                }
            }

            throw new SoftPilotException(
                "卸载目标正在使用。请先关闭使用此版本的终端、开发工具和进程，然后重试卸载。");
        }
    }

    private static bool HasRestartManagerLocks(string[] files)
    {
        var sessionKey = new System.Text.StringBuilder(33);
        var result = RmStartSession(out var session, 0, sessionKey);
        if (result != 0)
        {
            throw CreateRestartManagerException("启动文件占用检查失败", result);
        }

        try
        {
            result = RmRegisterResources(
                session,
                (uint)files.Length,
                files,
                0,
                nint.Zero,
                0,
                nint.Zero);
            if (result != 0)
            {
                throw CreateRestartManagerException("注册卸载目标失败", result);
            }

            uint affectedApplicationCount = 0;
            uint rebootReasons = 0;
            result = RmGetList(
                session,
                out var affectedApplicationCountNeeded,
                ref affectedApplicationCount,
                nint.Zero,
                ref rebootReasons);
            return result switch
            {
                0 => affectedApplicationCountNeeded > 0,
                MoreData => true,
                _ => throw CreateRestartManagerException("查询文件占用状态失败", result),
            };
        }
        finally
        {
            _ = RmEndSession(session);
        }
    }

    private static SoftPilotException CreateRestartManagerException(string operation, int error) =>
        new(
            $"{operation}。为避免卸载不完整，已停止操作。",
            new System.ComponentModel.Win32Exception(error));

    private static void ThrowDeletionPreflightFailure(string path)
    {
        var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        var exception = new System.ComponentModel.Win32Exception(error);
        if (error is SharingViolation or LockViolation)
        {
            throw new SoftPilotException(
                $"卸载目标正在使用：{path}。请先关闭使用此版本的终端、开发工具和进程，然后重试卸载。",
                exception);
        }

        throw new SoftPilotException(
            $"无法确认卸载目标可安全删除：{path}。请检查文件权限后重试。",
            exception);
    }

    private const int SharingViolation = 32;
    private const int LockViolation = 33;
    private const int MoreData = 234;
    private const uint DeleteAccess = 0x00010000;
    private const uint BackupSemantics = 0x02000000;

    [System.Runtime.InteropServices.DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        nint securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [System.Runtime.InteropServices.DllImport(
        "rstrtmgr.dll",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RmStartSession(
        out uint sessionHandle,
        int sessionFlags,
        System.Text.StringBuilder sessionKey);

    [System.Runtime.InteropServices.DllImport(
        "rstrtmgr.dll",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint sessionHandle,
        uint fileCount,
        string[] fileNames,
        uint applicationCount,
        nint applications,
        uint serviceCount,
        nint serviceNames);

    [System.Runtime.InteropServices.DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint sessionHandle,
        out uint affectedApplicationCountNeeded,
        ref uint affectedApplicationCount,
        nint affectedApplications,
        ref uint rebootReasons);

    [System.Runtime.InteropServices.DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);
}
