using System;
using System.Runtime.InteropServices;

namespace GunsAreLoud.Client.Audio
{
    internal static class HeadphoneNativePlugin
    {
        private const string ModuleName = "AudioPluginGalHeadphones.dll";
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ReadInt();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ulong ReadUInt64();
        private static bool? _knownPlayer;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetModuleFileNameW(IntPtr module, System.Text.StringBuilder path, int size);
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

        internal static bool IsRegistered(out string reason)
        {
            reason = "native effect registration unverified";
            IntPtr module = GetModuleHandleW(ModuleName);
            IntPtr unity = GetModuleHandleW("UnityPlayer.dll");
            if (module == IntPtr.Zero || unity == IntPtr.Zero) return false;
            if (!_knownPlayer.HasValue)
            {
                var path = new System.Text.StringBuilder(32768);
                if (GetModuleFileNameW(unity, path, path.Capacity) == 0) return false;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var file = System.IO.File.OpenRead(path.ToString()))
                    _knownPlayer = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "") ==
                        "BF491512C0122395C4BA0316B936F22CB2D586BBF320C9417904DAC3C07CC9BE";
            }
            if (_knownPlayer != true || IntPtr.Size != 8)
            { reason = "registration inspection unsupported for this UnityPlayer"; return false; }
            // Verified layout of this exact player; read only on the main thread.
            IntPtr registry = Marshal.ReadIntPtr(IntPtr.Add(unity, 0x1c00340));
            if (registry == IntPtr.Zero) return false;
            long count = Marshal.ReadInt64(registry, 0x10);
            if (count < 0 || count > 1000) return false;
            IntPtr entries = Marshal.ReadIntPtr(registry);
            IntPtr definitions = GetProcAddress(module, "UnityGetAudioEffectDefinitions");
            if (entries == IntPtr.Zero || definitions == IntPtr.Zero) return false;
            for (int i = 0; i < count; i++)
                if (Marshal.ReadIntPtr(entries, i * 0x48) == module &&
                    Marshal.ReadIntPtr(entries, i * 0x48 + 0x18) == definitions)
                { reason = "Unity registry contains this DLL and its audio definitions"; return true; }
            reason = "DLL loaded but audio definitions absent from Unity registry";
            return false;
        }

        internal static string LoadedPath
        {
            get
            {
                IntPtr module = GetModuleHandleW(ModuleName);
                if (module == IntPtr.Zero) return "not loaded";
                var path = new System.Text.StringBuilder(32768);
                return GetModuleFileNameW(module, path, path.Capacity) > 0 ? path.ToString() : "path unavailable";
            }
        }

        internal static bool TryGetProcessedFrames(out ulong frames)
        {
            frames = 0;
            IntPtr module = GetModuleHandleW(ModuleName);
            if (module == IntPtr.Zero) return false;
            IntPtr pointer = GetProcAddress(module, "GAL_HeadphonesProcessedFrames");
            if (pointer == IntPtr.Zero) return false;
            frames = ((ReadUInt64)Marshal.GetDelegateForFunctionPointer(pointer, typeof(ReadUInt64)))();
            return true;
        }

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
