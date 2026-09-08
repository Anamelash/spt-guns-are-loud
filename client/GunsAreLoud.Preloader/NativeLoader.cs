using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;

// Early registration for the verified player build. Never writes game metadata.
public static class NativeLoader
{
    public static IEnumerable<string> TargetDLLs { get { return new string[0]; } }
    public static void Patch(AssemblyDefinition assembly) { }
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern uint GetModuleFileNameW(IntPtr module, System.Text.StringBuilder path, int size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void RegisterPlugin(IntPtr module);
    static string Hash(string path) { using (var sha = SHA256.Create()) using (var f = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", ""); }
    public static void Initialize()
    {
        var log = Logger.CreateLogSource("GAL Native Loader");
        try
        {
            string player = Path.Combine(Paths.GameRootPath, "UnityPlayer.dll");
            if (Hash(player) != "BF491512C0122395C4BA0316B936F22CB2D586BBF320C9417904DAC3C07CC9BE") throw new Exception("Unsupported UnityPlayer hash");
            string dll = Path.Combine(Path.GetDirectoryName(typeof(NativeLoader).Assembly.Location), "AudioPluginGalHeadphones.dll");
            if (Hash(dll) != "5B9CB1DF468A40689137D7AA39E049AC9CA4E6EB7AD2A428CEF4DF47A07CE6A1") throw new Exception("Unsupported DSP hash");
            IntPtr unity = GetModuleHandleW("UnityPlayer.dll");
            if (unity == IntPtr.Zero) throw new Exception("UnityPlayer not loaded");

            IntPtr registry = Marshal.ReadIntPtr(IntPtr.Add(unity, 0x1c00340));
            if (registry == IntPtr.Zero) throw new Exception("Unity native plugin registry not initialized");
            long before = Marshal.ReadInt64(registry, 0x10);
            if (before < 0 || before > 1000) throw new Exception("Invalid registry count");
            IntPtr module = GetModuleHandleW("AudioPluginGalHeadphones.dll");
            if (module != IntPtr.Zero)
            {
                var loadedPath = new System.Text.StringBuilder(32768);
                if (GetModuleFileNameW(module, loadedPath, loadedPath.Capacity) == 0 || Hash(loadedPath.ToString()) != Hash(dll))
                    throw new Exception("An incompatible native GAL DLL is already loaded");
            }
            else module = LoadLibraryW(dll);
            if (module == IntPtr.Zero) throw new Exception("LoadLibrary failed: " + Marshal.GetLastWin32Error());
            if (GetProcAddress(module, "UnityGetAudioEffectDefinitions") == IntPtr.Zero) throw new Exception("Missing definitions export");
            log.LogInfo("Calling Unity plugin registration; countBefore=" + before);
            var register = (RegisterPlugin)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(unity, 0x5c4720), typeof(RegisterPlugin));
            register(module);
            long after = Marshal.ReadInt64(registry, 0x10);
            bool found = false;
            if (after < before || after > before + 1 || after > 1000) throw new Exception("Invalid post-registration count");
            IntPtr entries = Marshal.ReadIntPtr(registry);
            for (int i = 0; i < after; i++)
                if (Marshal.ReadIntPtr(entries, i * 0x48) == module && Marshal.ReadIntPtr(entries, i * 0x48 + 0x18) == GetProcAddress(module, "UnityGetAudioEffectDefinitions")) found = true;
            if (!found) throw new Exception("Definitions not present in Unity registry");
            log.LogInfo("REGISTERED through BepInEx; countAfter=" + after + "; audio definitions verified");
        }
        catch (Exception e) { log.LogError(e); }
    }
}
