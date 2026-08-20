using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SoftPilot.Infrastructure.Security;

internal static class WindowsDataProtector
{
    private const int CryptProtectUiForbidden = 0x1;

    public static byte[] Protect(byte[] value) => Transform(value, protect: true);

    public static byte[] Unprotect(byte[] value) => Transform(value, protect: false);

    private static byte[] Transform(byte[] value, bool protect)
    {
        var input = CreateBlob(value);
        try
        {
            var success = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
            if (!success)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
        }
    }

    private static DataBlob CreateBlob(byte[] value)
    {
        var pointer = Marshal.AllocHGlobal(value.Length);
        Marshal.Copy(value, 0, pointer, value.Length);
        return new DataBlob(value.Length, pointer);
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DataBlob(int Size, IntPtr Data);
}
