using System;
using System.Runtime.InteropServices;

namespace Inkveil
{
    internal static class NativeMethods
    {
        private const int GwlExStyle = -20;
        private const int WhMouseLl = 14;
        private const int WmMouseWheel = 0x020A;
        private const uint GwHwndNext = 2;
        private const long WsExTransparent = 0x00000020L;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpFrameChanged = 0x0020;

        private static IntPtr _mouseWheelHook;
        private static LowLevelMouseProc _mouseWheelHookProc;
        private static MouseWheelHandler _mouseWheelHandler;

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

        [StructLayout(LayoutKind.Sequential)]
        private struct LowLevelMouseInput
        {
            public Point Point;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

        internal delegate bool MouseWheelHandler(Point point, int delta);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr window, out Rect rect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetTopWindow(IntPtr parent);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);

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

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(
            int hookType,
            LowLevelMouseProc callback,
            IntPtr module,
            uint threadId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(
            IntPtr hook,
            int code,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        internal static bool TryGetCursorPosition(out Point point)
        {
            return GetCursorPos(out point);
        }

        internal static bool TryGetWindowRect(IntPtr window, out Rect rect)
        {
            return GetWindowRect(window, out rect);
        }

        internal static bool IsWindowTopAtPoint(IntPtr window, Point point)
        {
            IntPtr current = GetTopWindow(IntPtr.Zero);
            while (current != IntPtr.Zero)
            {
                if (current == window)
                {
                    return true;
                }

                Rect bounds;
                long style = GetWindowLong(current, GwlExStyle).ToInt64();
                if (IsWindowVisible(current)
                    && (style & WsExTransparent) == 0
                    && GetWindowRect(current, out bounds)
                    && point.X >= bounds.Left
                    && point.X < bounds.Right
                    && point.Y >= bounds.Top
                    && point.Y < bounds.Bottom)
                {
                    return false;
                }

                current = GetWindow(current, GwHwndNext);
            }

            return false;
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

        internal static bool TryInstallMouseWheelHook(MouseWheelHandler handler)
        {
            if (_mouseWheelHook != IntPtr.Zero)
            {
                return true;
            }
            if (handler == null)
            {
                return false;
            }

            _mouseWheelHandler = handler;
            _mouseWheelHookProc = MouseWheelHookCallback;
            _mouseWheelHook = SetWindowsHookEx(
                WhMouseLl,
                _mouseWheelHookProc,
                GetModuleHandle(null),
                0);

            if (_mouseWheelHook != IntPtr.Zero)
            {
                return true;
            }

            _mouseWheelHookProc = null;
            _mouseWheelHandler = null;
            return false;
        }

        internal static void UninstallMouseWheelHook()
        {
            if (_mouseWheelHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseWheelHook);
                _mouseWheelHook = IntPtr.Zero;
            }
            _mouseWheelHookProc = null;
            _mouseWheelHandler = null;
        }

        private static IntPtr MouseWheelHookCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0
                && wParam == new IntPtr(WmMouseWheel)
                && _mouseWheelHandler != null)
            {
                var input = (LowLevelMouseInput)Marshal.PtrToStructure(
                    lParam,
                    typeof(LowLevelMouseInput));
                int delta = unchecked((short)((input.MouseData >> 16) & 0xFFFF));
                if (delta != 0)
                {
                    try
                    {
                        if (_mouseWheelHandler(input.Point, delta))
                        {
                            return new IntPtr(1);
                        }
                    }
                    catch
                    {
                        // 全局钩子中的异常不应影响系统滚轮消息继续传递。
                    }
                }
            }

            return CallNextHookEx(_mouseWheelHook, code, wParam, lParam);
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
