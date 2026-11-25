using System;
using System.Text;
using System.Windows.Forms;

namespace BlackScreenLock
{
    /// <summary>
    /// 虚拟键与字符/可读名称之间的转换。
    /// 注意：锁屏时物理按键会被钩子吞掉，系统的键盘状态不会更新，
    /// 所以 Shift 状态由钩子自行追踪并传入，不用 GetKeyState。
    /// </summary>
    internal static class KeyMapper
    {
        /// <summary>把按键转成可输入字符，非可打印键返回 '\0'。</summary>
        public static char ToChar(Keys key, bool shift)
        {
            int vk = (int)key;

            if (vk >= (int)Keys.A && vk <= (int)Keys.Z)
            {
                int idx = vk - (int)Keys.A;
                return shift ? (char)('A' + idx) : (char)('a' + idx);
            }
            if (vk >= (int)Keys.D0 && vk <= (int)Keys.D9)
            {
                int idx = vk - (int)Keys.D0;
                if (!shift) return (char)('0' + idx);
                return ")!@#$%^&*("[idx];
            }
            if (vk >= (int)Keys.NumPad0 && vk <= (int)Keys.NumPad9)
                return (char)('0' + (vk - (int)Keys.NumPad0));

            switch (key)
            {
                case Keys.OemMinus: return shift ? '_' : '-';
                case Keys.Oemplus: return shift ? '+' : '=';
                case Keys.OemOpenBrackets: return shift ? '{' : '[';
                case Keys.OemCloseBrackets: return shift ? '}' : ']';
                case Keys.OemPipe: return shift ? '|' : '\\';
                case Keys.OemSemicolon: return shift ? ':' : ';';
                case Keys.OemQuotes: return shift ? '"' : '\'';
                case Keys.Oemcomma: return shift ? '<' : ',';
                case Keys.OemPeriod: return shift ? '>' : '.';
                case Keys.OemQuestion: return shift ? '?' : '/';
                case Keys.Oemtilde: return shift ? '~' : '`';
                case Keys.Space: return ' ';
            }
            return '\0';
        }

        public static string KeyName(int vk)
        {
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
            if (vk >= 0x60 && vk <= 0x69) return "Num" + (char)('0' + (vk - 0x60));
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F + 1);

            switch (vk)
            {
                case 0x08: return "Backspace";
                case 0x09: return "Tab";
                case 0x0D: return "Enter";
                case 0x13: return "Pause";
                case 0x1B: return "Esc";
                case 0x20: return "Space";
                case 0x21: return "PageUp";
                case 0x22: return "PageDown";
                case 0x23: return "End";
                case 0x24: return "Home";
                case 0x25: return "Left";
                case 0x26: return "Up";
                case 0x27: return "Right";
                case 0x28: return "Down";
                case 0x2C: return "PrintScreen";
                case 0x2D: return "Insert";
                case 0x2E: return "Delete";
                case 0x5B: return "LWin";
                case 0x5C: return "RWin";
                case 0x6A: return "Num*";
                case 0x6B: return "Num+";
                case 0x6D: return "Num-";
                case 0x6E: return "Num.";
                case 0x6F: return "Num/";
                case 0xBA: return ";";
                case 0xBB: return "=";
                case 0xBC: return ",";
                case 0xBD: return "-";
                case 0xBE: return ".";
                case 0xBF: return "/";
                case 0xC0: return "~";
                case 0xDB: return "[";
                case 0xDC: return "\\";
                case 0xDD: return "]";
                case 0xDE: return "'";
            }
            return "VK" + vk.ToString("X2");
        }

        public static string HotkeyText(int modifiers, int vk)
        {
            StringBuilder sb = new StringBuilder();
            if ((modifiers & NativeMethods.MOD_CONTROL) != 0) sb.Append("Ctrl + ");
            if ((modifiers & NativeMethods.MOD_ALT) != 0) sb.Append("Alt + ");
            if ((modifiers & NativeMethods.MOD_SHIFT) != 0) sb.Append("Shift + ");
            sb.Append(KeyName(vk));
            return sb.ToString();
        }

        /// <summary>把 WinForms 的按键数据拆成（修饰键位, 虚拟键码）。</summary>
        public static void Split(Keys keyData, out int modifiers, out int vk)
        {
            modifiers = 0;
            if ((keyData & Keys.Control) == Keys.Control) modifiers |= NativeMethods.MOD_CONTROL;
            if ((keyData & Keys.Alt) == Keys.Alt) modifiers |= NativeMethods.MOD_ALT;
            if ((keyData & Keys.Shift) == Keys.Shift) modifiers |= NativeMethods.MOD_SHIFT;
            vk = (int)(keyData & Keys.KeyCode);
        }

        /// <summary>判断该组合是否允许注册为全局热键（至少 Ctrl/Alt 之一，或 F 键、PrintScreen 这类独立键）。</summary>
        public static bool IsAcceptableHotkey(int modifiers, int vk)
        {
            if (vk == 0x0D || vk == 0x08 || vk == 0x1B || vk == 0x09 || vk == 0x20)
                return false; // 回车/退格/Esc/Tab/空格 不适合做全局热键
            if (modifiers == 0)
                return vk >= 0x70 && vk <= 0x87; // 无修饰键时仅允许 F1~F24
            return (modifiers & (NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT)) != 0;
        }
    }
}
