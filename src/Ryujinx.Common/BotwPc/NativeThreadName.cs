using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Common.BotwPc
{
    /// <summary>
    /// Gives the current thread's managed name to the OS thread (NativeAOT on iOS does not), so per-thread CPU time can be
    /// attributed (the iPad app's BOTWPC_THREADLOG, Instruments).
    /// </summary>
    public static partial class NativeThreadName
    {
        [LibraryImport("libc", EntryPoint = "pthread_setname_np", StringMarshalling = StringMarshalling.Utf8)]
        private static partial int SetName(string name);

        public static void Set()
        {
            if ((OperatingSystem.IsIOS() || OperatingSystem.IsMacOS()) && Thread.CurrentThread.Name is string name)
            {
                SetName(name.Length > 63 ? name[..63] : name);
            }
        }
    }
}
