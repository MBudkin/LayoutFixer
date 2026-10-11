using System;

namespace LayoutFixer.Input;

[Flags]
internal enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8
}
