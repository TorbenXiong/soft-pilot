using SoftPilot.Infrastructure.Installation;
using SoftPilot.Infrastructure.Runtime;

namespace SoftPilot.Tests;

[TestClass]
public sealed class WorkspaceOperationLockTests
{
    [TestMethod]
    public async Task AcquireAsync_BlocksAnotherOwnerUntilTheLeaseIsReleased()
    {
        using var sandbox = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(sandbox.Path);
        var firstOwner = new WorkspaceOperationLock(layout);
        var secondOwner = new WorkspaceOperationLock(layout);

        await using (await firstOwner.AcquireAsync(CancellationToken.None))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
                await secondOwner.AcquireAsync(cancellation.Token));
        }

        await using var secondLease = await secondOwner.AcquireAsync(CancellationToken.None);
        Assert.IsNotNull(secondLease);
    }
}
