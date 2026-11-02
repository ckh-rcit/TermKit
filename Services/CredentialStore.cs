using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace TermKit.Services
{
    /// <summary>
    /// Stores one username/password pair in Windows Credential Manager. The password is encrypted by Windows
    /// for the current user and never written to config.json or the logs.
    /// </summary>
    internal static class CredentialStore
    {
        private const uint TypeGeneric = 1;
        private const uint PersistLocalMachine = 2; // Stays on this PC; ENTERPRISE would roam with the domain profile.
        private const int ErrorNotFound = 1168;
        private const int MaxBlobBytes = 2560;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeCredential
        {
            public uint Flags;
            public uint Type;
            public string? TargetName;
            public string? Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string? UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref NativeCredential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr buffer);

        public static void Write(string target, string userName, string password)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(target);
            ArgumentException.ThrowIfNullOrWhiteSpace(userName);
            var blob = Encoding.Unicode.GetBytes(password);
            if (blob.Length > MaxBlobBytes) throw new ArgumentException("The password is too long to store.", nameof(password));
            var buffer = Marshal.AllocHGlobal(Math.Max(blob.Length, 1));
            try
            {
                Marshal.Copy(blob, 0, buffer, blob.Length);
                var credential = new NativeCredential
                {
                    Type = TypeGeneric,
                    TargetName = target,
                    UserName = userName,
                    CredentialBlobSize = (uint)blob.Length,
                    CredentialBlob = buffer,
                    Persist = PersistLocalMachine
                };
                if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                Marshal.Copy(new byte[blob.Length], 0, buffer, blob.Length);
                Marshal.FreeHGlobal(buffer);
                Array.Clear(blob);
            }
        }

        public static bool TryRead(string target, out string userName, out string password)
        {
            userName = string.Empty;
            password = string.Empty;
            if (!CredRead(target, TypeGeneric, 0, out var pointer))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorNotFound) return false;
                throw new Win32Exception(error);
            }

            try
            {
                var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
                var blob = new byte[credential.CredentialBlobSize];
                if (blob.Length > 0) Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
                try { password = Encoding.Unicode.GetString(blob); }
                finally { Array.Clear(blob); }
                userName = credential.UserName ?? string.Empty;
                return userName.Length > 0;
            }
            finally
            {
                CredFree(pointer);
            }
        }

        public static bool Exists(string target)
        {
            if (!CredRead(target, TypeGeneric, 0, out var pointer)) return false;
            CredFree(pointer);
            return true;
        }

        public static void Delete(string target)
        {
            if (!CredDelete(target, TypeGeneric, 0))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorNotFound) throw new Win32Exception(error);
            }
        }
    }
}
