using SoftPilot.Application;
using SoftPilot.Infrastructure.Installation;
using SoftPilot.Infrastructure.State;

namespace SoftPilot.Tests;

[TestClass]
public sealed class JsonFormatterHistoryStoreTests
{
    [TestMethod]
    public async Task LoadAsync_WhenFileDoesNotExist_ReturnsEmptyHistory()
    {
        using var temporary = new TemporaryDirectory();
        var store = new JsonFormatterHistoryStore(new WindowsInstallationLayout(temporary.Path));

        var actual = await store.LoadAsync();

        Assert.IsEmpty(actual);
    }

    [TestMethod]
    public async Task SaveAsync_PersistsHistoryAndLeavesNoTemporaryFile()
    {
        using var temporary = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(temporary.Path);
        var store = new JsonFormatterHistoryStore(layout);
        JsonFormatterHistoryEntry[] expected =
        [
            new(Guid.NewGuid(), "API response", "{\"ok\":true}", JsonFormattingMode.Beautified, DateTimeOffset.UtcNow),
            new(Guid.NewGuid(), "Compact", "[1,2]", JsonFormattingMode.Minified, DateTimeOffset.UtcNow.AddMinutes(-1)),
        ];

        await store.SaveAsync(expected);
        var actual = await store.LoadAsync();

        CollectionAssert.AreEqual(expected, actual.ToArray());
        Assert.AreEqual(
            0,
            Directory.GetFiles(
                Path.Combine(layout.DataDirectory, "toolbox"),
                "*.tmp",
                SearchOption.TopDirectoryOnly).Length);
    }

    [TestMethod]
    public async Task LoadAsync_WhenJsonIsInvalid_ThrowsSafeHistoryError()
    {
        using var temporary = new TemporaryDirectory();
        var layout = new WindowsInstallationLayout(temporary.Path);
        var historyDirectory = Path.Combine(layout.DataDirectory, "toolbox");
        Directory.CreateDirectory(historyDirectory);
        await File.WriteAllTextAsync(Path.Combine(historyDirectory, "json-history.json"), "not-json");
        var store = new JsonFormatterHistoryStore(layout);

        var exception = await Assert.ThrowsAsync<SoftPilotException>(() => store.LoadAsync());

        StringAssert.Contains(exception.Message, "已损坏");
    }
}
