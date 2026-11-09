using System;
using System.IO;
using System.Runtime.InteropServices;
using LogicLog;

namespace EVE.Components.Server
{
    /// The LW32 CPU's core (native/liblw32core.so, built from the lw-compiler
    /// repo by emu/build-native.sh; its interface is emu/core/lw32core.h there).
    /// Loaded once by path from the mod's folder, its functions looked up by name.
    /// Linux x64 only for now.
    public static class Lw32Native
    {
        public const int ExpectedVersion = 2;   // LW32CORE_VERSION

        [StructLayout(LayoutKind.Sequential)]
        public struct In
        {
            public uint Run, Reset, Ready, Data;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Out
        {
            public uint Address, Data, Read, Write, Size, Halted;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int VersionFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int AddFn(int a, int b);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr NewFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void FreeFn(IntPtr core);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void TickFn(IntPtr core, ref In input, out Out output);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int BusyFn(IntPtr core);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int SaveFn(IntPtr core, byte[] buf, int cap);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int LoadFn(IntPtr core, byte[] buf, int len);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void RegsFn(IntPtr core, [Out] uint[] regs, out ulong instructions);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int ErrorFn(IntPtr core, byte[] buf, int cap);

        public static VersionFn Version;
        public static AddFn Add;
        public static NewFn New;
        public static FreeFn Free;
        public static TickFn Tick;
        public static BusyFn Busy;
        public static SaveFn SaveFnPtr;
        public static LoadFn LoadFnPtr;
        public static RegsFn Regs;
        public static ErrorFn ErrorFnPtr;

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
                int v = Version();
                if (v != ExpectedVersion)
                    throw new Exception($"version {v}, this mod needs {ExpectedVersion}: run emu/build-native.sh again");
                Add = Get<AddFn>(lib, "lw32core_add");
                New = Get<NewFn>(lib, "lw32core_new");
                Free = Get<FreeFn>(lib, "lw32core_free");
                Tick = Get<TickFn>(lib, "lw32core_tick");
                Busy = Get<BusyFn>(lib, "lw32core_busy");
                SaveFnPtr = Get<SaveFn>(lib, "lw32core_save");
                LoadFnPtr = Get<LoadFn>(lib, "lw32core_load");
                Regs = Get<RegsFn>(lib, "lw32core_regs");
                ErrorFnPtr = Get<ErrorFn>(lib, "lw32core_error");
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

        /// The core's whole state, for the world save.
        public static byte[] Save(IntPtr core)
        {
            int n = SaveFnPtr(core, null, 0);
            var buf = new byte[n];
            SaveFnPtr(core, buf, n);
            return buf;
        }

        /// Restores a state from Save; false (core unchanged) when the bytes aren't one.
        public static bool Restore(IntPtr core, byte[] state)
        {
            return state != null && LoadFnPtr(core, state, state.Length) == 0;
        }

        /// Why the core stopped other than by Halt, or "".
        public static string CoreError(IntPtr core)
        {
            int n = ErrorFnPtr(core, null, 0);
            if (n <= 0) return "";
            var buf = new byte[n + 1];
            ErrorFnPtr(core, buf, buf.Length);
            return System.Text.Encoding.UTF8.GetString(buf, 0, n);
        }

        private static T Get<T>(IntPtr lib, string name) where T : Delegate
        {
            return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
        }
    }
}
