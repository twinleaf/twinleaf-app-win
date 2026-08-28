// SPDX-License-Identifier: Apache-2.0

using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Twinleaf.Models;

namespace Twinleaf.Controls;

/// <summary>
/// A row in the settings sidebar. One class covers both device headers and
/// setting rows, and the control a setting row shows — value field, switch,
/// action button or capture button — is chosen by the RPC's declared type.
///
/// The list binds a single template and switches parts on with the
/// <c>*Visibility</c> properties, which keeps virtualisation working; a
/// template selector would rebuild containers as rows are recycled.
/// </summary>
public sealed partial class SettingRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>A device grouping header, not a setting.</summary>
    public static SettingRow Header(string title) => new(title);

    private SettingRow(string title)
    {
        _title = title;
        Rpc = null;
    }

    public SettingRow(RpcInfo rpc, JsonValue? value)
    {
        Rpc = rpc;
        // Show the last dot-segment: a sidebar 300 px wide cannot fit
        // "data.rate.decimation" and the prefix repeats down the whole group.
        _title = rpc.Name;
        _detail = $"{rpc.Name}  ·  {rpc.DisplayType}";
        _valueText = FormatValue(rpc, value);
        _switchValue = value is not null ? ValueAsSwitch(value) : rpc.SwitchValue;
        _readOnlyText = _valueText;
    }

    public RpcInfo? Rpc { get; }

    public bool IsHeader => Rpc is null;

    public string Id => Rpc?.Id ?? $"header:{_title}";

    // -----------------------------------------------------------------
    // Which control this row shows
    // -----------------------------------------------------------------

    public Visibility HeaderVisibility => IsHeader ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SettingVisibility => IsHeader ? Visibility.Collapsed : Visibility.Visible;

    public Visibility CaptureVisibility =>
        Rpc is { IsCaptureRpc: true } ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ActionVisibility =>
        Rpc is { IsCaptureRpc: false, IsActionRpc: true } ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SwitchVisibility =>
        Rpc is { IsCaptureRpc: false, IsActionRpc: false, IsEnableSwitchRpc: true }
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>An editable field, for a writable setting with a usable type.</summary>
    public Visibility FieldVisibility =>
        Rpc is { IsEditableSetting: true } ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Read-only text, for a setting that can be read but not written (or whose
    /// type the device did not describe well enough to edit safely).
    /// </summary>
    public Visibility ReadOnlyVisibility =>
        Rpc is not null
        && !Rpc.IsCaptureRpc
        && !Rpc.IsActionRpc
        && !Rpc.IsEnableSwitchRpc
        && !Rpc.IsEditableSetting
            ? Visibility.Visible
            : Visibility.Collapsed;

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

    /// <summary>Text in the editable field. Two-way bound, so it also holds user edits.</summary>
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

    /// <summary>
    /// True while the field has focus, so an incoming readback does not
    /// overwrite half-typed input.
    /// </summary>
    public bool IsEditing { get; set; }

    /// <summary>Applies a value that arrived from the device.</summary>
    public void ApplyValue(JsonValue? value)
    {
        if (Rpc is null)
        {
            return;
        }

        var text = FormatValue(Rpc, value);
        ReadOnlyText = text;
        SwitchValue = value is not null ? ValueAsSwitch(value) : false;

        if (!IsEditing)
        {
            ValueText = text;
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
    /// Display text for a value, matching the macOS field formatting: floats
    /// render at three decimals (with a dead zone around zero so a tiny
    /// residual does not read as "-0.000"), everything else as-is.
    /// </summary>
    private static string FormatValue(RpcInfo rpc, JsonValue? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is JsonValue.Number number && rpc.IsNumericRpc && !rpc.IsIntegerRpc)
        {
            var magnitude = Math.Abs(number.Value);
            if (!double.IsFinite(number.Value))
            {
                return number.Value.ToString(CultureInfo.InvariantCulture);
            }

            var normalized = magnitude < 0.0005 ? 0 : number.Value;
            return normalized.ToString("0.000", CultureInfo.InvariantCulture);
        }

        return value.EditableText;
    }

    /// <summary>
    /// The ListViewItem takes its automation name from the bound item, so
    /// without this a screen reader announces every row as
    /// "Twinleaf.Controls.SettingRow".
    /// </summary>
    public override string ToString() => IsHeader ? Title : $"{Title} {ReadOnlyText}".TrimEnd();

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
