using System.Net;
using System.Net.Sockets;
using SoftPilot.Infrastructure.IO;

namespace SoftPilot.Tests;

[TestClass]
public sealed class WindowsTcpListenerProcessResolverTests
{
    [TestMethod]
    public void GetListenerProcessIds_ReturnsOwningWindowsProcess()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var processIds = new WindowsTcpListenerProcessResolver().GetListenerProcessIds(port);

            CollectionAssert.Contains(processIds.ToArray(), Environment.ProcessId);
        }
        finally
        {
            listener.Stop();
        }
    }
}
