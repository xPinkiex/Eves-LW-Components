using System;
using System.IO;
using System.Runtime.InteropServices;
using LogicLog;

namespace EVE.Components.Server
{
    /// The LW32 emulator core (native/liblw32core.so, built from the lw-compiler
    /// repo by emu/build-native.sh; its interface is emu/core/lw32core.h there).
    /// Loaded once by path from the mod's folder, its functions looked up by name.
    /// Linux x64 only for now.
    public static class Lw32Native
    {
        public const int ExpectedVersion = 1;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int VersionFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int AddFn(int a, int b);

        public static VersionFn Version;
        public static AddFn Add;

        /// Why loading failed, or null once it worked.
        public static string Error { get; private set; } = "not loaded";
        public static bool Loaded => Error == null;

        public static void Load(string modPath, ILogicLogger log)
        {
            string path = Path.Combine(modPath, "native", "liblw32core.so");
            try
            {
                if (!File.Exists(path))
                    throw new FileNotFoundException("missing: " + path);
                IntPtr lib = NativeLibrary.Load(path);
                Version = Get<VersionFn>(lib, "lw32core_version");
                Add = Get<AddFn>(lib, "lw32core_add");
                int v = Version();
                if (v != ExpectedVersion)
                    throw new Exception($"version {v}, this mod needs {ExpectedVersion}: run emu/build-native.sh again");
                int sum = Add(2, 3);
                if (sum != 5)
                    throw new Exception($"lw32core_add(2, 3) returned {sum}");
                Error = null;
                log.Info($"EVE_Components: lw32core loaded from {path} (version {v}, 2 + 3 = {sum})");
            }
            catch (Exception e)
            {
                Error = e.Message;
                log.Error($"EVE_Components: lw32core not loaded ({RuntimeInformation.OSDescription}, " +
                          $"{RuntimeInformation.ProcessArchitecture}): {e.GetType().Name}: {e.Message}");
            }
        }

        private static T Get<T>(IntPtr lib, string name) where T : Delegate
        {
            return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
        }
    }
}
