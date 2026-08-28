// SPDX-License-Identifier: Apache-2.0

using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Twinleaf.Models;

namespace Twinleaf.Controls;

/// <summary>What a <see cref="SidebarRow"/> represents.</summary>
public enum SidebarRowKind
{
    /// <summary>Device name, starting a section.</summary>
    DeviceHeader,
    /// <summary>Serial number under a device name.</summary>
    Subtitle,
    /// <summary>A stream ("sine · 1000.0 Hz") or the literal "Settings".</summary>
    SectionHeader,
    /// <summary>A plottable column, with a tick box and a live value.</summary>
    Channel,
    /// <summary>A device setting, with the control its type implies.</summary>
    Setting,
}

/// <summary>
/// One row of the sidebar. Channels and settings share a single flat list,
/// grouped per device, mirroring the GTK app's layout:
///
/// <code>
/// tio-test                 &lt;- DeviceHeader
/// SIM0001                  &lt;- Subtitle
///   sine · 1000.0 Hz       &lt;- SectionHeader
///     [x] sine (V)  -0.146 &lt;- Channel
///   Settings               &lt;- SectionHeader
///     test.enable  [ on ]  &lt;- Setting
/// </code>
///
/// One class and one data template rather than a type-per-row plus a template
/// selector: the list virtualises, and a selector rebuilds containers as rows
/// are recycled.
/// </summary>
public sealed partial class SidebarRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private SidebarRow(SidebarRowKind kind, string title)
    {
        Kind = kind;
        _title = title;
    }

    public SidebarRowKind Kind { get; }

    // -----------------------------------------------------------------
    // Construction
    // -----------------------------------------------------------------

    public static SidebarRow DeviceHeader(string name) => new(SidebarRowKind.DeviceHeader, name);

    public static SidebarRow Subtitle(string text) => new(SidebarRowKind.Subtitle, text);

    public static SidebarRow SectionHeader(string text) => new(SidebarRowKind.SectionHeader, text);

    public static SidebarRow Channel(ColumnInfo column, bool selected) =>
        new(SidebarRowKind.Channel, column.DisplayLabel)
        {
            Key = column.Key,
            _detail = column.Description,
            _isSelected = selected,
        };

    public static SidebarRow Setting(RpcInfo rpc, JsonValue? value)
    {
        var row = new SidebarRow(SidebarRowKind.Setting, rpc.Name)
        {
            Rpc = rpc,
            _detail = $"{rpc.Name}  ·  {rpc.DisplayType}",
        };

        row.ApplyValue(value);
        // ApplyValue only writes the field when not editing; seed it directly so
        // a freshly built row shows its value.
        row._valueText = row._readOnlyText;
        return row;
    }

    /// <summary>Set on channel rows.</summary>
    public ColumnKey? Key { get; private init; }

    /// <summary>Set on setting rows.</summary>
    public RpcInfo? Rpc { get; private init; }

    public string Id => Rpc?.Id ?? Key?.ToString() ?? $"{Kind}:{_title}";

    // -----------------------------------------------------------------
    // Which parts of the template this row shows
    // -----------------------------------------------------------------

    public Visibility DeviceHeaderVisibility => Vis(Kind == SidebarRowKind.DeviceHeader);

    public Visibility SubtitleVisibility => Vis(Kind == SidebarRowKind.Subtitle);

    public Visibility SectionHeaderVisibility => Vis(Kind == SidebarRowKind.SectionHeader);

    public Visibility ChannelVisibility => Vis(Kind == SidebarRowKind.Channel);

    public Visibility SettingVisibility => Vis(Kind == SidebarRowKind.Setting);

    public Visibility CaptureVisibility => Vis(Rpc is { IsCaptureRpc: true });

    public Visibility ActionVisibility => Vis(Rpc is { IsCaptureRpc: false, IsActionRpc: true });

    public Visibility SwitchVisibility =>
        Vis(Rpc is { IsCaptureRpc: false, IsActionRpc: false, IsEnableSwitchRpc: true });

    public Visibility FieldVisibility => Vis(Rpc is { IsEditableSetting: true });

    /// <summary>Readable but not writable — or typed too vaguely to edit safely.</summary>
    public Visibility ReadOnlyVisibility => Vis(
        Rpc is not null
        && !Rpc.IsCaptureRpc
        && !Rpc.IsActionRpc
        && !Rpc.IsEnableSwitchRpc
        && !Rpc.IsEditableSetting);

    private static Visibility Vis(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Last dot-segment, matching the macOS action button label.</summary>
    public string ActionTitle
    {
        get
        {
            var name = Rpc?.Name ?? string.Empty;
            var index = name.LastIndexOf('.');
            return index >= 0 && index < name.Length - 1 ? name[(index + 1)..] : name;
        }
    }

    public string Placeholder => Rpc?.DisplayType ?? string.Empty;

    public bool IsWritable => Rpc?.Writable ?? false;

    // -----------------------------------------------------------------
    // Mutable state
    // -----------------------------------------------------------------

    private string _title;
    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    private string _detail = string.Empty;
    public string Detail
    {
        get => _detail;
        set => Set(ref _detail, value);
    }

    /// <summary>Live channel reading.</summary>
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

    private string _valueText = string.Empty;
    public string ValueText
    {
        get => _valueText;
        set => Set(ref _valueText, value);
    }

    private string _readOnlyText = string.Empty;
    public string ReadOnlyText
    {
        get => _readOnlyText;
        set => Set(ref _readOnlyText, value);
    }

    private bool _switchValue;
    public bool SwitchValue
    {
        get => _switchValue;
        set => Set(ref _switchValue, value);
    }

    /// <summary>True while the field has focus, so a readback cannot overwrite typing.</summary>
    public bool IsEditing { get; set; }

    public void ApplyValue(JsonValue? value)
    {
        if (Rpc is null)
        {
            return;
        }

        ReadOnlyText = FormatValue(Rpc, value);
        SwitchValue = value is not null && ValueAsSwitch(value);

        if (!IsEditing)
        {
            ValueText = ReadOnlyText;
        }
    }

    private static bool ValueAsSwitch(JsonValue value) => value switch
    {
        JsonValue.Bool flag => flag.Value,
        JsonValue.Number number => number.Value != 0,
        JsonValue.Str text => text.Value.Trim().ToLowerInvariant() is "true" or "yes" or "on" or "1",
        _ => false,
    };

    /// <summary>
    /// Matches the macOS field formatting: floats at three decimals, with a dead
    /// zone around zero so a tiny residual does not read as "-0.000".
    /// </summary>
    private static string FormatValue(RpcInfo rpc, JsonValue? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is JsonValue.Number number && rpc.IsNumericRpc && !rpc.IsIntegerRpc)
        {
            if (!double.IsFinite(number.Value))
            {
                return number.Value.ToString(CultureInfo.InvariantCulture);
            }

            var normalized = Math.Abs(number.Value) < 0.0005 ? 0 : number.Value;
            return normalized.ToString("0.000", CultureInfo.InvariantCulture);
        }

        return value.EditableText;
    }

    /// <summary>
    /// The ListViewItem takes its automation name from the bound item, so
    /// without this a screen reader announces the type name for every row.
    /// </summary>
    public override string ToString() => Kind switch
    {
        SidebarRowKind.Channel => $"{Title} {Value}".TrimEnd(),
        SidebarRowKind.Setting => $"{Title} {ReadOnlyText}".TrimEnd(),
        _ => Title,
    };

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
