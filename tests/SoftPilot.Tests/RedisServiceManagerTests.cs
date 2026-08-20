using SoftPilot.Infrastructure.Runtime;

namespace SoftPilot.Tests;

[TestClass]
public sealed class RedisServiceManagerTests
{
    [TestMethod]
    public void BuildDefaultConfig_BindsLocallyAndUsesVersionSpecificPaths()
    {
        var dataPath = @"D:\Soft Pilot\SoftPilotData\data\redis\8.2.9";
        var logPath = @"D:\Soft Pilot\SoftPilotData\logs\redis\8.2.9\redis.log";

        var config = RedisServiceManager.BuildDefaultConfig(dataPath, logPath);

        StringAssert.Contains(config, "bind 127.0.0.1");
        StringAssert.Contains(config, "protected-mode yes");
        StringAssert.Contains(config, "port 6379");
        StringAssert.Contains(config, "daemonize no");
        StringAssert.Contains(config, "dir \"D:/Soft Pilot/SoftPilotData/data/redis/8.2.9\"");
        StringAssert.Contains(config, "logfile \"D:/Soft Pilot/SoftPilotData/logs/redis/8.2.9/redis.log\"");
    }

    [TestMethod]
    public void IsExpectedServerInfo_UsesRedisVersionAndIgnoresMsysPosixPid()
    {
        const string info = "# Server\r\nredis_version:8.2.9\r\nprocess_id:1234\r\n";

        Assert.IsTrue(RedisServiceManager.IsExpectedServerInfo(info, "8.2.9"));
        Assert.IsFalse(RedisServiceManager.IsExpectedServerInfo(info, "8.2.8"));
        Assert.IsFalse(RedisServiceManager.IsExpectedServerInfo("process_id:1234\r\n", "8.2.9"));
    }
}
