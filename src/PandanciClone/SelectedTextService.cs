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

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        public string CaptureSelectedText()
        {
            if (!WaitForHotkeyRelease()) throw new InvalidOperationException("请先松开 Alt+A 后再取词。");

            IntPtr targetWindow = GetForegroundWindow();
            string oldText = "";
            bool hadTextClipboard = TryReadClipboardText(out oldText);
            uint sequenceBefore = GetClipboardSequenceNumber();

            if (targetWindow != IntPtr.Zero)
            {
                SetForegroundWindow(targetWindow);
                Thread.Sleep(60);
            }

            SendCopyShortcut();
            string captured = WaitForCopiedText(sequenceBefore);

            if (hadTextClipboard)
            {
                TryRestoreClipboardText(oldText);
            }

            return CleanText(captured);
        }

        private static string WaitForCopiedText(uint sequenceBefore)
        {
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(50);
                if (GetClipboardSequenceNumber() == sequenceBefore) continue;

                string current;
                if (TryReadClipboardText(out current) && !string.IsNullOrWhiteSpace(current)) return current;
            }

            return "";
        }

        private static bool WaitForHotkeyRelease()
        {
            for (int i = 0; i < 80; i++)
            {
                bool altDown = IsKeyDown(VkMenu);
                bool aDown = IsKeyDown(VkA);
                if (!altDown && !aDown) return true;
                Thread.Sleep(25);
            }
            return false;
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

        private static bool TryReadClipboardText(out string text)
        {
            text = "";
            try
            {
                if (!Clipboard.ContainsText(TextDataFormat.UnicodeText)) return false;
                text = Clipboard.GetText(TextDataFormat.UnicodeText);
                return true;
            }
            catch (ExternalException)
            {
                return false;
            }
        }

        private static void TryRestoreClipboardText(string text)
        {
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    Clipboard.SetText(text ?? "", TextDataFormat.UnicodeText);
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
