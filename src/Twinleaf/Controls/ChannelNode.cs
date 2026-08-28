// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Twinleaf.Models;

namespace Twinleaf.Controls;

/// <summary>
/// A row in the channel sidebar. One class covers all three levels — device,
/// stream and column — because the tree binds a single template; only leaf
/// (column) rows carry a <see cref="Key"/> and show a checkbox.
/// </summary>
public sealed partial class ChannelNode : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public ChannelNode(string title, string? detail = null, ColumnKey? key = null)
    {
        _title = title;
        _detail = detail ?? string.Empty;
        Key = key;
    }

    /// <summary>Set only on column rows; null for device and stream rows.</summary>
    public ColumnKey? Key { get; }

    public bool IsColumn => Key is not null;

    public ObservableCollection<ChannelNode> Children { get; } = [];

    public Visibility CheckboxVisibility => IsColumn ? Visibility.Visible : Visibility.Collapsed;

    private string _title;
    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    private string _detail;
    public string Detail
    {
        get => _detail;
        set => Set(ref _detail, value);
    }

    /// <summary>Live scalar readout, formatted by <see cref="NumericDisplayPolicy"/>.</summary>
    private string _value = string.Empty;
    public string Value
    {
        get => _value;
        set => Set(ref _value, value);
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
