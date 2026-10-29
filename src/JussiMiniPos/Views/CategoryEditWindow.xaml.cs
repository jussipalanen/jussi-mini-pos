using System.Collections.Generic;
using System.Linq;
using System.Windows;
using JussiMiniPos.Models;

namespace JussiMiniPos.Views;

/// <summary>
/// Modal editor for one category, used for both adding and editing.
/// </summary>
public partial class CategoryEditWindow : Window
{
    /// <summary>Stands in for "no parent" in the parent chip list.</summary>
    private static readonly Category NoParent = new(0, "Ei yläkategoriaa", null);

    public CategoryEditWindow(Category? category, IReadOnlyList<Category> all)
    {
        InitializeComponent();

        IsNew = category is null;
        WindowTitle = IsNew ? "Uusi kategoria" : "Muokkaa kategoriaa";
        HeaderText = IsNew ? "Uusi kategoria" : $"{category!.Title} ({category.Id})";

        DataContext = this;

        TitleBox.Text = category?.Title ?? string.Empty;
        PublicBox.IsChecked = category?.IsPublic ?? true;

        // A category cannot be its own parent, nor sit under one of its own
        // descendants — that would make a cycle the tree queries never escape.
        var forbidden = category is null ? [] : DescendantsOf(category.Id, all).Append(category.Id).ToHashSet();

        var choices = new List<Category> { NoParent };
        choices.AddRange(all.Where(c => !forbidden.Contains(c.Id)));
        ParentList.ItemsSource = choices;

        ParentList.SelectedItem = choices.FirstOrDefault(c => c.Id == category?.ParentId) ?? NoParent;

        TitleBox.Focus();
        TitleBox.SelectAll();
    }

    public bool IsNew { get; }

    public string WindowTitle { get; }

    public string HeaderText { get; }

    public string CategoryTitle { get; private set; } = string.Empty;

    public int? ParentId { get; private set; }

    public bool IsPublic { get; private set; }

    /// <summary>
    /// Walks down the tree iteratively with a seen-set. A ParentId cycle should
    /// not exist, but CategoriesView deliberately lists rows caught in one so
    /// they can be fixed — and reaching this from there must not recurse until
    /// the stack gives out.
    /// </summary>
    private static HashSet<int> DescendantsOf(int id, IReadOnlyList<Category> all)
    {
        var found = new HashSet<int>();
        var queue = new Queue<int>([id]);

        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();

            foreach (var child in all.Where(c => c.ParentId == parent))
            {
                if (found.Add(child.Id))
                {
                    queue.Enqueue(child.Id);
                }
            }
        }

        return found;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text.Trim();
        if (title.Length == 0)
        {
            ErrorText.Text = "Nimi ei voi olla tyhjä.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        CategoryTitle = title;
        ParentId = ParentList.SelectedItem is Category { Id: > 0 } parent ? parent.Id : null;
        IsPublic = PublicBox.IsChecked == true;

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
