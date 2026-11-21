using System.Runtime.InteropServices;
using System.Text;

namespace Shft.Hme;

public sealed class HmeSecretException(string message) : Exception(message);

/// <summary>
/// Secrets on disk (Tuya Access Secret, Positivo password and session) go through Windows' DPAPI, as Controle Casa
/// did: "dpapi:&lt;base64&gt;" opens only for the same user on the same PC. "b64:" and plain text are legacy, read only.
/// </summary>
public static class Secrets
{
    const string Prefix = "dpapi:";

    public static bool IsProtected(string? value) => value != null && value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Protect(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        try
        {
            return Prefix + Convert.ToBase64String(Dpapi(Encoding.UTF8.GetBytes(text), encrypt: true));
        }
        catch (Exception ex) when (ex is not HmeSecretException)
        {
            throw new HmeSecretException($"O Windows não conseguiu proteger o segredo ({ex.Message}). Nada foi salvo.");
        }
    }

    public static string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            try
            {
                return Encoding.UTF8.GetString(Dpapi(Convert.FromBase64String(value[Prefix.Length..]), encrypt: false));
            }
            catch (Exception)
            {
                throw new HmeSecretException("Não consegui abrir o segredo salvo (ele foi protegido em outra conta " +
                                             "ou outro computador). Cole de novo nos ajustes.");
            }
        }
        if (value.StartsWith("b64:", StringComparison.Ordinal))
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(value[4..])); }
            catch (FormatException) { throw new HmeSecretException("Segredo salvo em formato inválido. Cole de novo nos ajustes."); }
        }
        return value;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Blob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);

    static byte[] Dpapi(byte[] data, bool encrypt)
    {
        const int UiForbidden = 0x1;
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new Blob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
            bool ok = encrypt
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new InvalidOperationException($"código {Marshal.GetLastWin32Error()}");
            try
            {
                var bytes = new byte[output.Size];
                Marshal.Copy(output.Data, bytes, 0, output.Size);
                return bytes;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            handle.Free();
        }
    }
}
