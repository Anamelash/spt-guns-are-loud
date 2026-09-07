using System;
using System.Runtime.InteropServices;

namespace GunsAreLoud.Client.Audio
{
    internal static class HeadphoneNativePlugin
    {
        private const string ModuleName = "AudioPluginGalHeadphones.dll";
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ReadInt();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr GetModuleHandleW(string moduleName);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        internal static bool IsPreloaded(out string reason)
        {
            try
            {
                // Deliberately never load the DLL here. Unity must have registered
                // its effect definitions during player startup, before mixer load.
                if (!TryRead("GAL_HeadphonesAbiVersion", out int abi) || abi != 1)
                { reason = "native headphone effect was not preloaded with the supported ABI"; return false; }
                reason = "";
                return true;
            }
            catch (Exception error) { reason = "native headphone registration: " + error.Message; return false; }
        }

        internal static int InstanceCount => TryRead("GAL_HeadphonesInstanceCount", out int count) ? count : 0;

        private static bool TryRead(string symbol, out int value)
        {
            value = 0;
            IntPtr module = GetModuleHandleW(ModuleName);
            if (module == IntPtr.Zero) return false;
            IntPtr pointer = GetProcAddress(module, symbol);
            if (pointer == IntPtr.Zero) return false;
            value = ((ReadInt)Marshal.GetDelegateForFunctionPointer(pointer, typeof(ReadInt)))();
            return true;
        }
    }
}
