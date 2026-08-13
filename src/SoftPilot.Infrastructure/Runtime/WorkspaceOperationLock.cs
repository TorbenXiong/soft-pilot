namespace SoftPilot.Infrastructure.Runtime;

internal sealed class WorkspaceOperationLock
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);
    private readonly string _lockPath;

    public WorkspaceOperationLock(IInstallationLayout layout)
    {
        _lockPath = Path.Combine(layout.DataDirectory, "workspace-operation.lock");
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_lockPath)!);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    _lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous);
                return new Lease(stream);
            }
            catch (IOException)
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
    }

    private sealed class Lease : IAsyncDisposable
    {
        private FileStream? _stream;

        public Lease(FileStream stream)
        {
            _stream = stream;
        }

        public ValueTask DisposeAsync()
        {
            _stream?.Dispose();
            _stream = null;
            return ValueTask.CompletedTask;
        }
    }
}
