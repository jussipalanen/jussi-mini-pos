using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using JussiMiniPos.Models;
using JussiMiniPos.Services;

namespace JussiMiniPos.Views;

/// <summary>
/// Category management: the whole tree as a flat list, with add, edit and
/// delete. Hidden categories are listed too, since this is where they get
/// hidden.
/// </summary>
public partial class CategoriesView : UserControl, INotifyPropertyChanged
{
    private readonly CatalogRepository _catalog;

    private IReadOnlyList<Category> _all = [];

    public CategoriesView(CatalogRepository catalog)
    {
        InitializeComponent();

        _catalog = catalog;
        Reload();

        DataContext = this;
    }

    /// <summary>Raised when the user wants to leave the categories view.</summary>
    public event Action? Back;

    public ObservableCollection<Row> Rows { get; } = [];

    public string ResultText => Rows.Count == 1 ? "1 kategoria" : $"{Rows.Count} kategoriaa";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>One line of the tree, flattened for display.</summary>
    public sealed record Row(Category Category, string ParentTitle, int ProductCount, int Depth)
    {
        public bool IsHidden => !Category.IsPublic;

        /// <summary>Left margin that shows the nesting.</summary>
        public Thickness Indent => new(Depth * 22, 0, 0, 0);
    }

    private void Reload()
    {
        _all = _catalog.GetCategories(publicOnly: false);

        Rows.Clear();
        foreach (var row in Flatten(parentId: null, depth: 0, parentTitle: "—"))
        {
            Rows.Add(row);
        }

        // Walking down from the roots misses anything caught in a ParentId
        // cycle. The editor will not make one, but a hand-edited database can,
        // and a category nobody can see is a category nobody can fix. List the
        // strays flat so they stay reachable.
        foreach (var stray in _all.Where(c => Rows.All(r => r.Category.Id != c.Id)))
        {
            Rows.Add(new Row(stray, "?", _catalog.CountProductsInCategory(stray.Id), 0));
        }

        OnPropertyChanged(nameof(ResultText));
    }

    /// <summary>Walks the tree so children appear under their parent, indented.</summary>
    private IEnumerable<Row> Flatten(int? parentId, int depth, string parentTitle)
    {
        foreach (var category in _all.Where(c => c.ParentId == parentId))
        {
            yield return new Row(
                category,
                parentTitle,
                _catalog.CountProductsInCategory(category.Id),
                depth);

            foreach (var child in Flatten(category.Id, depth + 1, category.Title))
            {
                yield return child;
            }
        }
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var window = new CategoryEditWindow(null, _all) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true)
        {
            return;
        }

        _catalog.InsertCategory(window.CategoryTitle, window.ParentId, window.IsPublic);
        Reload();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Row row)
        {
            return;
        }

        var window = new CategoryEditWindow(row.Category, _all) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true)
        {
            return;
        }

        _catalog.UpdateCategory(row.Category.Id, window.CategoryTitle, window.ParentId, window.IsPublic);
        Reload();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Row row)
        {
            return;
        }

        var owner = Window.GetWindow(this);

        // ParentId is ON DELETE RESTRICT, so SQLite would refuse anyway. Say so
        // up front rather than letting it fail on save.
        var children = _catalog.CountChildCategories(row.Category.Id);
        if (children > 0)
        {
            MessageBox.Show(
                owner,
                $"Kategorialla \"{row.Category.Title}\" on {children} alakategoriaa. " +
                "Siirrä tai poista ne ensin.",
                "Poista kategoria",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var products = row.ProductCount;
        var warning = products > 0
            ? $"\n\n{products} tuotetta menettää tämän kategorian. Tuotteet itse säilyvät."
            : string.Empty;

        var answer = MessageBox.Show(
            owner,
            $"Poistetaanko kategoria \"{row.Category.Title}\" ({row.Category.Id})?{warning}",
            "Poista kategoria",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _catalog.DeleteCategory(row.Category.Id);
        Reload();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Back?.Invoke();

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
