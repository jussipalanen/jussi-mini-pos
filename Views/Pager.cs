using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace JussiMiniPos.Views;

/// <summary>
/// Paging state for a list view: the page size, which page is showing, and the
/// buttons for jumping around. Owners keep their own filtered collection and
/// refill the visible page from <see cref="Skip"/> and <see cref="Take"/>
/// whenever <see cref="Changed"/> fires.
///
/// Shared rather than copied, so the "keep the top row on screen" arithmetic
/// lives in exactly one place.
/// </summary>
public sealed class Pager(string singular, string plural) : INotifyPropertyChanged
{
    /// <summary>How many page buttons to show before falling back to ellipsis.</summary>
    private const int MaxPageButtons = 7;

    private int _pageSize = 10;
    private int _currentPage = 1;
    private int _count;

    /// <summary>Raised when the visible window moves, so the owner can refill it.</summary>
    public event Action? Changed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<int> PageSizes { get; } = [10, 25, 50, 100];

    public ObservableCollection<PageButton> PageNumbers { get; } = [];

    public int PageSize
    {
        get => _pageSize;
        set
        {
            if (value <= 0 || _pageSize == value)
            {
                return;
            }

            // Which row is at the top right now — worked out with the size that
            // produced the current page, before it changes.
            var firstRow = ((_currentPage - 1) * _pageSize) + 1;

            _pageSize = value;
            OnPropertyChanged();

            // Keep that row on screen rather than jumping back to the start.
            GoTo(((firstRow - 1) / _pageSize) + 1);
        }
    }

    public int TotalPages => Math.Max(1, (int)Math.Ceiling(_count / (double)_pageSize));

    public bool HasPreviousPage => _currentPage > 1;

    public bool HasNextPage => _currentPage < TotalPages;

    public bool HasResults => _count > 0;

    public bool HasNoResults => _count == 0;

    /// <summary>Items to skip to reach the current page.</summary>
    public int Skip => (_currentPage - 1) * _pageSize;

    /// <summary>Items on a full page.</summary>
    public int Take => _pageSize;

    /// <summary>"Näytetään 1–10 / 20 tuotetta".</summary>
    public string ResultText
    {
        get
        {
            if (_count == 0)
            {
                return "Ei tuloksia";
            }

            var first = Skip + 1;
            var last = Math.Min(first + _pageSize - 1, _count);
            var noun = _count == 1 ? singular : plural;
            return $"Näytetään {first}–{last} / {_count} {noun}";
        }
    }

    public string PageText => $"Sivu {_currentPage} / {TotalPages}";

    /// <summary>One page button. Ellipsis entries are not clickable.</summary>
    public sealed record PageButton(string Number, bool IsCurrent, int Target)
    {
        public bool IsEllipsis => Target == 0;

        public string AutomationName => IsEllipsis ? "…" : $"Sivu {Number}";
    }

    /// <summary>
    /// Tells the pager how many items the current filter leaves.
    /// </summary>
    /// <param name="keepPage">
    /// True keeps the page the user was on, clamped to the new last page;
    /// false goes back to the first page, which is what a new filter wants.
    /// </param>
    public void SetCount(int count, bool keepPage)
    {
        _count = count;
        GoTo(keepPage ? Math.Min(_currentPage, TotalPages) : 1);
    }

    public void GoTo(int page)
    {
        _currentPage = Math.Clamp(page, 1, TotalPages);

        BuildPageButtons();

        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasNoResults));
        OnPropertyChanged(nameof(ResultText));
        OnPropertyChanged(nameof(PageText));

        Changed?.Invoke();
    }

    public void Next() => GoTo(_currentPage + 1);

    public void Previous() => GoTo(_currentPage - 1);

    /// <summary>
    /// Builds the page buttons. Beyond <see cref="MaxPageButtons"/> pages it
    /// shows the first and last with a window around the current page, so the
    /// bar does not grow without limit.
    /// </summary>
    private void BuildPageButtons()
    {
        PageNumbers.Clear();

        var total = TotalPages;
        var pages = new List<int>();

        if (total <= MaxPageButtons)
        {
            pages.AddRange(Enumerable.Range(1, total));
        }
        else
        {
            pages.Add(1);

            var from = Math.Max(2, _currentPage - 1);
            var to = Math.Min(total - 1, _currentPage + 1);

            // Keep the window the same width when it sits at either end.
            if (_currentPage <= 3)
            {
                to = 4;
            }
            else if (_currentPage >= total - 2)
            {
                from = total - 3;
            }

            pages.AddRange(Enumerable.Range(from, to - from + 1));
            pages.Add(total);
        }

        var previous = 0;
        foreach (var page in pages.Distinct())
        {
            if (previous != 0 && page - previous > 1)
            {
                PageNumbers.Add(new PageButton("…", false, 0));
            }

            PageNumbers.Add(new PageButton(page.ToString(), page == _currentPage, page));
            previous = page;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
