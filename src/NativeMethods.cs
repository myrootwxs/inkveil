using System;
using System.Runtime.InteropServices;

namespace Inkveil
{
    internal static class NativeMethods
    {
        private const int GwlExStyle = -20;
        private const long WsExTransparent = 0x00000020L;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpFrameChanged = 0x0020;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr window, out Rect rect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern IntPtr GetWindowLongPtr32(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern IntPtr SetWindowLongPtr32(IntPtr window, int index, int value);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        internal static bool TryGetCursorPosition(out Point point)
        {
            return GetCursorPos(out point);
        }

        internal static bool TryGetWindowRect(IntPtr window, out Rect rect)
        {
            return GetWindowRect(window, out rect);
        }

        internal static bool IsForegroundWindowFromCurrentProcess()
        {
            IntPtr foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero)
            {
                return false;
            }

            uint processId;
            return GetWindowThreadProcessId(foregroundWindow, out processId) != 0
                && processId == GetCurrentProcessId();
        }

        internal static void SetClickThrough(IntPtr window, bool enabled)
        {
            if (window == IntPtr.Zero)
            {
                return;
            }

            long style = GetWindowLong(window, GwlExStyle).ToInt64();
            if (enabled)
            {
                style |= WsExTransparent;
            }
            else
            {
                style &= ~WsExTransparent;
            }

            SetWindowLong(window, GwlExStyle, new IntPtr(style));
            SetWindowPos(
                window,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }

        private static IntPtr GetWindowLong(IntPtr window, int index)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(window, index)
                : GetWindowLongPtr32(window, index);
        }

        private static IntPtr SetWindowLong(IntPtr window, int index, IntPtr value)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(window, index, value)
                : SetWindowLongPtr32(window, index, value.ToInt32());
        }
    }
}
