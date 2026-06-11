using System;
using System.Runtime.InteropServices;

namespace PandanciClone
{
    internal sealed class DpiAwarenessScope : IDisposable
    {
        private static readonly IntPtr PerMonitorAwareV2 = new IntPtr(-4);
        private static readonly IntPtr PerMonitorAware = new IntPtr(-3);
        private static readonly IntPtr SystemAware = new IntPtr(-2);

        private readonly IntPtr _oldContext;

        private DpiAwarenessScope(IntPtr oldContext)
        {
            _oldContext = oldContext;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

        public static DpiAwarenessScope BeginPerMonitorAware()
        {
            IntPtr oldContext = TrySetThreadDpiAwarenessContext(PerMonitorAwareV2);
            if (oldContext == IntPtr.Zero) oldContext = TrySetThreadDpiAwarenessContext(PerMonitorAware);
            if (oldContext == IntPtr.Zero) oldContext = TrySetThreadDpiAwarenessContext(SystemAware);
            return new DpiAwarenessScope(oldContext);
        }

        public void Dispose()
        {
            if (_oldContext == IntPtr.Zero) return;
            TrySetThreadDpiAwarenessContext(_oldContext);
        }

        private static IntPtr TrySetThreadDpiAwarenessContext(IntPtr context)
        {
            try
            {
                return SetThreadDpiAwarenessContext(context);
            }
            catch
            {
                return IntPtr.Zero;
            }
        }
    }
}
