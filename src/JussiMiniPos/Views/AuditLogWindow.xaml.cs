using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using JussiMiniPos.Models;
using JussiMiniPos.Services;

namespace JussiMiniPos.Views;

/// <summary>
/// The audit log, newest first, read-only. Opened from Admin, so only an
/// administrator sees who did what.
/// </summary>
public partial class AuditLogWindow : Window, INotifyPropertyChanged
{
    private readonly AuditLog _audit;

    public AuditLogWindow(AuditLog audit)
    {
        InitializeComponent();
        _audit = audit;
        DataContext = this;
        Load();
    }

    public IReadOnlyList<AuditEntry> Entries { get; private set; } = [];

    public string SummaryText => Entries.Count >= AuditLog.DefaultLimit
        ? $"Uusimmat {AuditLog.DefaultLimit} tapahtumaa. Vanhemmat säilyvät tietokannassa; kaikki tulostuu komennolla --audit."
        : $"{Entries.Count} tapahtumaa, uusin ensin. Lokia ei voi muokata eikä tyhjentää sovelluksesta.";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Load()
    {
        IReadOnlyList<AuditEntry> entries = [];
        if (ViewErrors.Try(this, "Tapahtumalokia ei voitu lukea.", () => entries = _audit.GetEntries()))
        {
            Entries = entries;
            OnPropertyChanged(nameof(Entries));
            OnPropertyChanged(nameof(SummaryText));
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Load();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
