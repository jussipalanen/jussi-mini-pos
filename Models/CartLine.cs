using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace JussiMiniPos.Models;

/// <summary>
/// One product row in the shopping cart, together with its quantity.
/// </summary>
public sealed class CartLine : INotifyPropertyChanged
{
    private int _quantity;

    public CartLine(Product product, int quantity = 1)
    {
        Product = product;
        _quantity = quantity;
    }

    public Product Product { get; }

    public int Quantity
    {
        get => _quantity;
        set
        {
            if (_quantity == value)
            {
                return;
            }

            _quantity = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LineTotal));
        }
    }

    /// <summary>What this product is charged at, offer price included.</summary>
    public decimal UnitPrice => Product.EffectivePrice;

    /// <summary>Unit price multiplied by the quantity.</summary>
    public decimal LineTotal => UnitPrice * Quantity;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
