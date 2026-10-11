namespace LayoutFixer.Input;

internal sealed record HotkeyRegistrationError(HotkeyAction Action, string Hotkey, bool InvalidFormat);
