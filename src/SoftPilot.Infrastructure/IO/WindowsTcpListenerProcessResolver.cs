using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace SoftPilot.Infrastructure.IO;

public sealed class WindowsTcpListenerProcessResolver
{
    private const int AddressFamilyInet = 2;
    private const uint ErrorInsufficientBuffer = 122;
    private const int OwnerPidRowSize = 24;

    public IReadOnlySet<int> GetListenerProcessIds(int port)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, IPEndPoint.MinPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, IPEndPoint.MaxPort);

        var size = 0;
        var result = GetExtendedTcpTable(
            IntPtr.Zero,
            ref size,
            order: false,
            AddressFamilyInet,
            TcpTableClass.OwnerPidListener,
            reserved: 0);
        if (result is not ErrorInsufficientBuffer and not 0)
        {
            throw new Win32Exception((int)result, "无法读取 Windows TCP 监听表。");
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedTcpTable(
                buffer,
                ref size,
                order: false,
                AddressFamilyInet,
                TcpTableClass.OwnerPidListener,
                reserved: 0);
            if (result != 0)
            {
                throw new Win32Exception((int)result, "无法读取 Windows TCP 监听表。");
            }

            return ParseListenerProcessIds(buffer, port);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IReadOnlySet<int> ParseListenerProcessIds(IntPtr table, int expectedPort)
    {
        var count = Marshal.ReadInt32(table);
        var processIds = new HashSet<int>();
        for (var index = 0; index < count; index++)
        {
            var row = IntPtr.Add(table, sizeof(uint) + index * OwnerPidRowSize);
            var localPort = (ushort)IPAddress.NetworkToHostOrder(
                (short)(Marshal.ReadInt32(row, sizeof(uint) * 2) & ushort.MaxValue));
            if (localPort == expectedPort)
            {
                processIds.Add(Marshal.ReadInt32(row, sizeof(uint) * 5));
            }
        }

        return processIds;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        TcpTableClass tableClass,
        uint reserved);

    private enum TcpTableClass
    {
        OwnerPidListener = 3,
    }
}
