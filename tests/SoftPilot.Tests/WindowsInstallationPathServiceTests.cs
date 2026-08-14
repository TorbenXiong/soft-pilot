using SoftPilot.Infrastructure.Installation;

namespace SoftPilot.Tests;

[TestClass]
public sealed class WindowsInstallationPathServiceTests
{
    [TestMethod]
    public void OrderDriveRootCandidates_StartsAtCAndUsesDriveLetterOrder()
    {
        var candidates = WindowsInstallationPathService.OrderDriveRootCandidates(
            [@"F:\", @"A:\", @"D:\", @"Z:\", @"C:\", @"D:\"]);

        CollectionAssert.AreEqual(new[] { @"C:\", @"D:\", @"F:\", @"Z:\" }, candidates.ToArray());
    }

    [TestMethod]
    public void FindFirstValidParentDirectory_SkipsUnavailableCandidates()
    {
        using var sandbox = new TemporaryDirectory();
        var occupiedParent = Path.Combine(sandbox.Path, "occupied");
        var occupiedRoot = Path.Combine(occupiedParent, "SoftPilot");
        var availableParent = Path.Combine(sandbox.Path, "available");
        Directory.CreateDirectory(occupiedRoot);
        File.WriteAllText(Path.Combine(occupiedRoot, "foreign.txt"), "occupied");

        var service = new WindowsInstallationPathService();
        var selected = service.FindFirstValidParentDirectory(
            [occupiedParent, availableParent],
            Path.Combine(sandbox.Path, "fallback"));

        Assert.AreEqual(availableParent, selected);
    }

    [TestMethod]
    public void Validate_AcceptsMarkedWorkspaceWithoutRegistryEntry()
    {
        using var sandbox = new TemporaryDirectory();
        var parent = Path.Combine(sandbox.Path, "parent");
        var root = Path.Combine(parent, "SoftPilot");
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        File.WriteAllText(Path.Combine(root, ".softpilot-root"), "SoftPilot workspace\n");

        var result = new WindowsInstallationPathService().Validate(parent);

        Assert.IsTrue(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.AreEqual(root, result.FinalRoot);
    }

    [TestMethod]
    public void Validate_RejectsDirectoryWithInvalidWorkspaceMarker()
    {
        using var sandbox = new TemporaryDirectory();
        var parent = Path.Combine(sandbox.Path, "parent");
        var root = Path.Combine(parent, "SoftPilot");
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        File.WriteAllText(Path.Combine(root, ".softpilot-root"), "not-softpilot");

        var result = new WindowsInstallationPathService().Validate(parent);

        Assert.IsFalse(result.IsValid);
    }
}
