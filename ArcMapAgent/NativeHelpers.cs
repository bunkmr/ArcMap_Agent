using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ArcMapAgent
{
    /// <summary>少量 Win32 辅助：给单行 TextBox 加灰字提示（.NET Framework 没有 PlaceholderText）。</summary>
    internal static class NativeHelpers
    {
        private const int EM_SETCUEBANNER = 0x1501;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        public static void SetCue(TextBox box, string text)
        {
            try
            {
                if (box.IsHandleCreated)
                    SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, text);
                else
                    box.HandleCreated += (s, e) => SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, text);
            }
            catch { }
        }
    }
}
