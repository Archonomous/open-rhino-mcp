using System;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;

namespace OpenRhinoMcp.Bridge
{
    /// <summary>
    /// Cancels whatever Rhino command is running, the same way a person pressing Esc would.
    /// Safe to call from any thread.
    /// </summary>
    public static class CommandCanceller
    {
        const uint WM_KEYDOWN = 0x0100;
        const uint WM_KEYUP = 0x0101;
        static readonly IntPtr VK_ESCAPE = (IntPtr)0x1B;

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        public static void RequestCancel()
        {
            // 1. An Esc keypress breaks long computations ("Press Esc to cancel") and prompts alike.
            try
            {
                if (Rhino.Runtime.HostUtils.RunningOnWindows)
                {
                    var hwnd = RhinoApp.MainWindowHandle();
                    if (hwnd != IntPtr.Zero)
                    {
                        PostMessage(hwnd, WM_KEYDOWN, VK_ESCAPE, IntPtr.Zero);
                        PostMessage(hwnd, WM_KEYUP, VK_ESCAPE, IntPtr.Zero);
                    }
                }
            }
            catch { }

            // 2. "!" on the command line cancels a command waiting at a prompt (works on macOS too).
            try
            {
                RhinoApp.InvokeOnUiThread(new Action(() =>
                {
                    if (Command.InCommand()) RhinoApp.SendKeystrokes("!", true);
                }));
            }
            catch { }
        }
    }
}
