using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace PandanciClone
{
    internal sealed class SelectedTextService
    {
        private const byte VkControl = 0x11;
        private const byte VkC = 0x43;
        private const byte VkA = 0x41;
        private const byte VkMenu = 0x12;
        private const uint KeyeventfKeyup = 0x0002;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        public string CaptureSelectedText()
        {
            IDataObject backup = null;
            bool hadClipboard = false;
            try
            {
                hadClipboard = Clipboard.ContainsData(DataFormats.Text) || Clipboard.ContainsImage() || Clipboard.ContainsFileDropList();
                if (hadClipboard) backup = Clipboard.GetDataObject();
            }
            catch (ExternalException)
            {
                backup = null;
                hadClipboard = false;
            }

            try
            {
                WaitForHotkeyRelease();
                TryClearClipboard();
                SendCopyShortcut();
                string captured = WaitForText();
                return CleanText(captured);
            }
            finally
            {
                if (hadClipboard && backup != null)
                {
                    TryRestoreClipboard(backup);
                }
            }
        }

        private static string WaitForText()
        {
            for (int i = 0; i < 30; i++)
            {
                Thread.Sleep(50);
                string current = TryGetClipboardText();
                if (!string.IsNullOrWhiteSpace(current)) return current;
            }

            string fallback = TryGetClipboardText();
            return string.IsNullOrWhiteSpace(fallback) ? "" : fallback;
        }

        private static void WaitForHotkeyRelease()
        {
            for (int i = 0; i < 40; i++)
            {
                bool altDown = IsKeyDown(VkMenu);
                bool aDown = IsKeyDown(VkA);
                if (!altDown && !aDown) return;
                Thread.Sleep(25);
            }
        }

        private static bool IsKeyDown(int key)
        {
            return (GetAsyncKeyState(key) & unchecked((short)0x8000)) != 0;
        }

        private static void SendCopyShortcut()
        {
            keybd_event(VkControl, 0, 0, UIntPtr.Zero);
            Thread.Sleep(20);
            keybd_event(VkC, 0, 0, UIntPtr.Zero);
            Thread.Sleep(20);
            keybd_event(VkC, 0, KeyeventfKeyup, UIntPtr.Zero);
            Thread.Sleep(20);
            keybd_event(VkControl, 0, KeyeventfKeyup, UIntPtr.Zero);
        }

        private static void TryClearClipboard()
        {
            try
            {
                Clipboard.Clear();
            }
            catch (ExternalException)
            {
            }
        }

        private static string TryGetClipboardText()
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : "";
            }
            catch (ExternalException)
            {
                return "";
            }
        }

        private static void TryRestoreClipboard(IDataObject data)
        {
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    Clipboard.SetDataObject(data, true);
                    return;
                }
                catch (ExternalException)
                {
                    Thread.Sleep(30);
                }
            }
        }

        private static string CleanText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string value = text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
            while (value.IndexOf("  ", StringComparison.Ordinal) >= 0) value = value.Replace("  ", " ");
            return value.Length > 800 ? value.Substring(0, 800) : value;
        }
    }
}
