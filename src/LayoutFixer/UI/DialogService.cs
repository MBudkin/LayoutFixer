using System.Windows.Forms;
using LayoutFixer.Localization;

namespace LayoutFixer.UI;

internal static class DialogService
{
    public static void ShowError(IWin32Window? owner, string messageKey)
    {
        MessageBox.Show(
            owner,
            Localizer.Get(messageKey),
            Localizer.Get("ErrorDialogTitle"),
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
