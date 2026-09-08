using System;
using System.IO;
using System.Windows;
using Microsoft.Data.Sqlite;

namespace JussiMiniPos.Views;

/// <summary>
/// Runs a database write from a click handler and reports failures instead of
/// letting them escape. An exception thrown out of a WPF event handler is
/// unhandled and takes the whole application down, which would lose the till's
/// current cart along with it.
/// </summary>
public static class ViewErrors
{
    /// <summary>Runs <paramref name="write"/>, returning false if it failed.</summary>
    public static bool Try(DependencyObject owner, string whatFailed, Action write)
    {
        try
        {
            write();
            return true;
        }
        // UnauthorizedAccessException is not an IOException, so it needs
        // naming separately: a read-only file or a folder without write
        // permission throws it, and file writes reach here from the product
        // editor's images and from the AI assistant's key file.
        catch (Exception ex) when (ex is SqliteException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            MessageBox.Show(
                Window.GetWindow(owner),
                $"{whatFailed}\n\n{ex.Message}",
                "JussiMiniPos",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            return false;
        }
    }
}
