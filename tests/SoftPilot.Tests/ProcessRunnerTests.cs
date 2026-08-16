using System.Diagnostics;
using SoftPilot.Infrastructure.IO;

namespace SoftPilot.Tests;

[TestClass]
public sealed class ProcessRunnerTests
{
    [TestMethod]
    public async Task RunAsync_WhenCancelled_TerminatesProcessTree()
    {
        using var sandbox = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pidPath = Path.Combine(sandbox.Path, "pid.txt");
        var environment = new Dictionary<string, string?>
        {
            ["SOFTPILOT_TEST_PID_PATH"] = pidPath,
        };
        var runner = new ProcessRunner();
        var runTask = runner.RunAsync(
            "powershell.exe",
            [
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "[IO.File]::WriteAllText($env:SOFTPILOT_TEST_PID_PATH, [string]$PID); Start-Sleep -Seconds 30",
            ],
            environment: environment,
            cancellationToken: cancellation.Token);

        while (!File.Exists(pidPath))
        {
            await Task.Delay(25, cancellation.Token);
        }

        var processId = int.Parse(await File.ReadAllTextAsync(pidPath, cancellation.Token));
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => runTask);
        await Task.Delay(100);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId));
    }
}
