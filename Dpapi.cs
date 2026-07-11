using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace TinyRDP;

/// <summary>
/// Wraps DPAPI so a password can be stored in an .rdp file the way mstsc expects:
/// the "password 51:b:" field is the CryptProtectData blob of the UTF-16LE
/// password, hex-encoded. It's encrypted under the current user, so only that
/// user on this machine can use the file — which is exactly who launches it.
/// </summary>
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    public static string ProtectRdpPassword(string password)
    {
        byte[] data = Encoding.Unicode.GetBytes(password);
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        var outBlob = new DATA_BLOB();
        try
        {
            var inBlob = new DATA_BLOB { cbData = data.Length, pbData = pin.AddrOfPinnedObject() };
            if (!CryptProtectData(ref inBlob, "psw", IntPtr.Zero, IntPtr.Zero,
                                  IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            byte[] outBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, outBytes, 0, outBlob.cbData);
            return Convert.ToHexString(outBytes);
        }
        finally
        {
            pin.Free();
            // CryptProtectData allocates via LocalAlloc; FreeHGlobal calls LocalFree.
            if (outBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(outBlob.pbData);
        }
    }
}
