using System;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace LayoutFixer.Input;

/// <summary>
/// Detects whether the focused control currently has a real (non-empty) text
/// selection, using UI Automation's TextPattern.
/// </summary>
/// <remarks>
/// This is used to decide between the "convert active selection" and "convert
/// last typed word" paths. Sending Ctrl+C to detect a selection is unreliable
/// because some editors copy the current line/word even when nothing is selected,
/// which would make the selection path corrupt the just-typed text. Asking UI
/// Automation directly avoids that ambiguity. TextPattern is only supported by
/// some controls; when it is unavailable, this returns false and callers fall
/// back to the keyboard-buffer path.
/// </remarks>
internal static class SelectionProbe
{
    private const int MaxSelectionTextLength = 1_000_000;
    private const int MaxAncestorDepth = 12;

    public static bool TryReadSelectedText(bool allowClipboardFallback, out string selection)
    {
        selection = TryGetSelectedText() ?? string.Empty;
        if (!string.IsNullOrEmpty(selection))
        {
            return true;
        }

        return allowClipboardFallback &&
               SelectionReader.TryGetSelectionSafely(out selection);
    }

    /// <summary>
    /// Returns the text of the focused control's current selection, or null when
    /// there is no non-empty selection or UI Automation/TextPattern is unavailable.
    /// </summary>
    public static string? TryGetSelectedText()
    {
        try
        {
            AutomationElement? focused = AutomationElement.FocusedElement;
            if (focused is null)
            {
                return null;
            }

            AutomationElement? element = focused;
            for (int depth = 0; element is not null && depth < MaxAncestorDepth; depth++)
            {
                string? selectedText = TryGetSelectionFromElement(element);
                if (!string.IsNullOrWhiteSpace(selectedText))
                {
                    return selectedText;
                }

                element = TreeWalker.RawViewWalker.GetParent(element);
            }

            return null;
        }
        catch
        {
            // UI Automation is best-effort. The caller can still try the guarded
            // clipboard fallback, which avoids Ctrl+C when selection is unknown.
            return null;
        }
    }

    private static string? TryGetSelectionFromElement(AutomationElement element)
    {
        try
        {
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out object? patternObj) ||
                patternObj is not TextPattern pattern)
            {
                return null;
            }

            TextPatternRange[] selections = pattern.GetSelection();
            foreach (TextPatternRange range in selections)
            {
                // Degenerate (caret-only) ranges yield empty text; only a real,
                // non-empty selection counts.
                string text = range.GetText(MaxSelectionTextLength);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            return null;
        }
        catch
        {
            // One inaccessible ancestor must not abort probing the remaining
            // focus chain.
            return null;
        }
    }
}
