using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Printing;
using System.Windows;
using JussiMiniPos.Models;
using JussiMiniPos.Services;

namespace JussiMiniPos.Views;

/// <summary>
/// Prints a receipt from a click handler and reports a failure instead of
/// letting it escape: a printer that is off, out of paper or gone must never
/// take the till down with the sale that was just paid for.
/// </summary>
public static class ReceiptPrinting
{
    /// <summary>Prints, returning false if it was cancelled or failed; a failure has been shown.</summary>
    public static bool TryPrint(DependencyObject owner, IReadOnlyList<ReceiptLine> lines, TillSettings settings, string jobName)
    {
        var window = Window.GetWindow(owner);
        try
        {
            return ReceiptPrinter.Print(lines, settings, jobName, window);
        }
        catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException or Win32Exception)
        {
            MessageBox.Show(
                window,
                $"Kuittia ei voitu tulostaa.\n\n{ex.Message}\n\nTallennettu myynti ei muutu. Tarkista tulostin ja tulosta kuitti uudelleen.",
                "JussiMiniPos",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
    }
}
