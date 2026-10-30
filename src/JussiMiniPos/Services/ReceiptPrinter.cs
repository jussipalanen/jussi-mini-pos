using System.Collections.Generic;
using System.Linq;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using JussiMiniPos.Models;

namespace JussiMiniPos.Services;

/// <summary>
/// Prints receipts through an ordinary Windows printer: a thermal receipt
/// printer with its Windows driver, an office printer, or Microsoft Print to
/// PDF for trying it out. No ESC/POS, so there is no drawer kick; the content
/// comes from <see cref="Receipts"/> and only the layout lives here.
/// </summary>
public static class ReceiptPrinter
{
    private const double DipsPerMm = 96 / 25.4;

    /// <summary>The printers Windows knows about, local and shared, by the name printing uses.</summary>
    public static IReadOnlyList<string> InstalledPrinters()
    {
        using var server = new LocalPrintServer();
        return [.. server
            .GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections])
            .Select(queue => queue.FullName)
            .Order()];
    }

    /// <summary>
    /// Prints <paramref name="lines"/> on the configured printer, or asks for
    /// one when none is set or this computer does not have it. Returns false
    /// when the user cancelled. Runs on the UI thread, because a document is a
    /// UI object; spooling a receipt takes a moment, not a wait.
    /// </summary>
    /// <exception cref="PrintSystemException">The printer refused the job.</exception>
    public static bool Print(IReadOnlyList<ReceiptLine> lines, TillSettings settings, string jobName, Window owner)
    {
        var dialog = new PrintDialog();
        var queue = settings.HasPrinter ? FindQueue(settings.PrinterName) : null;
        if (queue is not null)
        {
            dialog.PrintQueue = queue;
        }
        else if (dialog.ShowDialog() != true)
        {
            return false;
        }

        var document = Render(lines, settings.ReceiptWidthMm);
        var width = document.PageWidth;
        document.PageHeight = dialog.PrintableAreaHeight > 0 ? dialog.PrintableAreaHeight : 1122;

        var paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
        paginator.PageSize = new Size(width, document.PageHeight);
        dialog.PrintDocument(paginator, jobName);
        return true;
    }

    /// <summary>
    /// Lays the lines out on a page as wide as the paper roll: one four-column
    /// table, with each kind of line spanning or splitting the columns.
    /// </summary>
    public static FlowDocument Render(IReadOnlyList<ReceiptLine> lines, int widthMm)
    {
        var pageWidth = widthMm * DipsPerMm;
        var document = new FlowDocument
        {
            PageWidth = pageWidth,
            ColumnWidth = pageWidth,
            PagePadding = new Thickness(3 * DipsPerMm, 2 * DipsPerMm, 3 * DipsPerMm, 6 * DipsPerMm),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = widthMm < 70 ? 10 : 11.5,
            Foreground = Brushes.Black,
            Background = Brushes.White,
        };

        var table = new Table { CellSpacing = 0 };
        table.Columns.Add(new TableColumn { Width = new GridLength(1.2, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(1.2, GridUnitType.Star) });

        var rows = new TableRowGroup();
        foreach (var line in lines)
        {
            rows.Rows.Add(Row(line, document.FontSize));
        }

        table.RowGroups.Add(rows);
        document.Blocks.Add(table);
        return document;
    }

    private static TableRow Row(ReceiptLine line, double fontSize)
    {
        var row = new TableRow();
        switch (line.Kind)
        {
            case ReceiptLineKind.Heading:
                row.Cells.Add(Cell(line.Left, 4, TextAlignment.Center, fontSize * 1.35, FontWeights.Bold));
                break;
            case ReceiptLineKind.Centered:
                row.Cells.Add(Cell(line.Left, 4, TextAlignment.Center, fontSize));
                break;
            case ReceiptLineKind.Banner:
                row.Cells.Add(Cell(line.Left, 4, TextAlignment.Center, fontSize * 1.1, FontWeights.Bold, top: 4));
                break;
            case ReceiptLineKind.Row:
                // An amount needs one column; a date and time needs two, or it wraps.
                var rightSpan = line.Right.Length > 10 ? 2 : 1;
                row.Cells.Add(Cell(line.Left, 4 - rightSpan, TextAlignment.Left, fontSize));
                row.Cells.Add(Cell(line.Right, rightSpan, TextAlignment.Right, fontSize));
                break;
            case ReceiptLineKind.Total:
                row.Cells.Add(Cell(line.Left, 2, TextAlignment.Left, fontSize * 1.25, FontWeights.Bold, top: 2));
                row.Cells.Add(Cell(line.Right, 2, TextAlignment.Right, fontSize * 1.25, FontWeights.Bold, top: 2));
                break;
            case ReceiptLineKind.Detail:
                var detail = Cell(line.Left, 4, TextAlignment.Left, fontSize * 0.85);
                detail.Padding = new Thickness(8, 0, 0, 1);
                row.Cells.Add(detail);
                break;
            case ReceiptLineKind.VatRow:
                for (var i = 0; i < 4; i++)
                {
                    row.Cells.Add(Cell(i < line.Cells.Length ? line.Cells[i] : string.Empty, 1,
                        i == 0 ? TextAlignment.Left : TextAlignment.Right, fontSize * 0.85));
                }

                break;
            case ReceiptLineKind.Separator:
                row.Cells.Add(new TableCell(new Paragraph { Margin = new Thickness(0), FontSize = 2 })
                {
                    ColumnSpan = 4,
                    BorderBrush = Brushes.Black,
                    BorderThickness = new Thickness(0, 0, 0, 0.75),
                    Padding = new Thickness(0, 2, 0, 3),
                });
                break;
        }

        return row;
    }

    private static TableCell Cell(
        string text, int span, TextAlignment alignment, double fontSize,
        FontWeight? weight = null, double top = 0) =>
        new(new Paragraph(new Run(text))
        {
            Margin = new Thickness(0),
            TextAlignment = alignment,
            FontSize = fontSize,
            FontWeight = weight ?? FontWeights.Normal,
        })
        {
            ColumnSpan = span,
            Padding = new Thickness(0, top, 0, 1),
        };

    /// <remarks>
    /// The server is not disposed: the queue returned keeps using it for the
    /// print job, and both are released with the dialog.
    /// </remarks>
    private static PrintQueue? FindQueue(string name)
    {
        var server = new LocalPrintServer();
        return server
            .GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections])
            .FirstOrDefault(queue => string.Equals(queue.FullName, name, System.StringComparison.OrdinalIgnoreCase));
    }
}
