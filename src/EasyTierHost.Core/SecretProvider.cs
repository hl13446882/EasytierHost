using System.Runtime.InteropServices;
using System.Text;

namespace EasyTierHost.Core;

public static class SecretProvider
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob data, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob data, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    private static byte[] Transform(byte[] value, bool encrypt)
    {
        var data = new Blob { Size = value.Length, Data = Marshal.AllocHGlobal(value.Length) };
        try
        {
            Marshal.Copy(value, 0, data.Data, value.Length);
            // Machine scope permits the service account to read; the enclosing directory must be ACL protected.
            var ok = encrypt ? CryptProtectData(ref data, "EasyTierHost", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 5, out var result) : CryptUnprotectData(ref data, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out result);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try { byte[] output = new byte[result.Size]; Marshal.Copy(result.Data, output, 0, result.Size); return output; }
            finally { LocalFree(result.Data); }
        }
        finally { Marshal.FreeHGlobal(data.Data); System.Security.Cryptography.CryptographicOperations.ZeroMemory(value); }
    }
    public static async Task WriteAsync(string path, string secret, CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
        {
            // Encrypt in a private staging directory so machine-scope ciphertext is never public.
            var staging = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, ".secret-" + Guid.NewGuid().ToString("N"));
            await SecureDirectoryAsync(staging, ct);
            var temporary = Path.Combine(staging, "secret");
            try
            {
                var encrypted = Transform(Encoding.UTF8.GetBytes(secret), true);
                await File.WriteAllBytesAsync(temporary, encrypted, ct);
                File.Move(temporary, path, false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); Directory.Delete(staging); }
            return;
        }
        var data = Encoding.UTF8.GetBytes(secret);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(data, ct);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(data);
    }
    public static async Task<string> ReadAsync(string path, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0) throw new IOException("Secret file must have mode 0600");
        var data = await File.ReadAllBytesAsync(path, ct);
        if (OperatingSystem.IsWindows()) data = Transform(data, false);
        try { return Encoding.UTF8.GetString(data); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(data); }
    }
    public static async Task SecureDirectoryAsync(string path, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(path, mode);
            File.SetUnixFileMode(path, mode);
        }
        else
        {
            Directory.CreateDirectory(path);
            var security = new System.Security.AccessControl.DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            var current = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
            foreach (var sid in new[] { current, new System.Security.Principal.SecurityIdentifier("S-1-5-18"), new System.Security.Principal.SecurityIdentifier("S-1-5-32-544") }.Distinct())
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask;
    }
}
