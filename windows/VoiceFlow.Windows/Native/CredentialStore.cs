using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using VoiceFlow.Core;
using static VoiceFlow.Windows.Native.NativeMethods;

namespace VoiceFlow.Windows.Native;

/// <summary>
/// API keys in Windows Credential Manager (generic credentials, protected by DPAPI for this user).
/// Keys never go to settings files or logs.
/// </summary>
internal sealed class CredentialStore(string prefix = "VoiceFlow/")
{
    public string Target(SecretKey key) => prefix + EnumText.Raw(key);

    public void Save(SecretKey key, string value) => Write(Target(key), value.Trim());

    public string? Read(SecretKey key) => Read(Target(key));

    public void Delete(SecretKey key) => Delete(Target(key));

    public bool Contains(SecretKey key) => Read(key) is { Length: > 0 };

    public static void Write(string target, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        var blobPtr = Marshal.AllocCoTaskMem(Math.Max(1, blob.Length));
        var targetPtr = Marshal.StringToCoTaskMemUni(target);
        var userPtr = Marshal.StringToCoTaskMemUni("VoiceFlow");
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var credential = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = targetPtr,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = userPtr,
            };
            if (!CredWriteW(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not save the key in Windows Credential Manager.");
        }
        finally
        {
            // Wipe the copies of the secret this code made.
            Marshal.Copy(new byte[blob.Length], 0, blobPtr, blob.Length);
            Array.Clear(blob);
            Marshal.FreeCoTaskMem(blobPtr);
            Marshal.FreeCoTaskMem(targetPtr);
            Marshal.FreeCoTaskMem(userPtr);
        }
    }

    public static string? Read(string target)
    {
        if (!CredReadW(target, CRED_TYPE_GENERIC, 0, out var ptr))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ERROR_NOT_FOUND) return null;
            throw new Win32Exception(error, "Could not read the key from Windows Credential Manager.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var value = Encoding.Unicode.GetString(bytes);
            Array.Clear(bytes);
            return value;
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public static void Delete(string target)
    {
        if (!CredDeleteW(target, CRED_TYPE_GENERIC, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ERROR_NOT_FOUND) throw new Win32Exception(error, "Could not remove the key from Windows Credential Manager.");
        }
    }
}
