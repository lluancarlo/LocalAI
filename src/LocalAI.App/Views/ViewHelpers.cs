using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Threading;
using LocalAI.App.ViewModels;

namespace LocalAI.App.Views;

public sealed class StatusBrushConverter : IValueConverter
{
    private static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#9ECE6A"));
    private static readonly IBrush Busy = new SolidColorBrush(Color.Parse("#7AA2F7"));
    private static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#E0AF68"));
    private static readonly IBrush Error = new SolidColorBrush(Color.Parse("#F7768E"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        StatusLevel.Ok => Ok,
        StatusLevel.Warning => Warning,
        StatusLevel.Error => Error,
        _ => Busy,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Focuses and selects a TextBox when it becomes visible (inline rename).</summary>
public static class FocusOnVisible
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("Enabled", typeof(FocusOnVisible));

    static FocusOnVisible()
    {
        Visual.IsVisibleProperty.Changed.AddClassHandler<TextBox>((box, e) =>
        {
            if (GetEnabled(box) && e.NewValue is true)
                Dispatcher.UIThread.Post(() => { box.Focus(); box.SelectAll(); }, DispatcherPriority.Input);
        });
    }

    public static bool GetEnabled(TextBox element) => element.GetValue(EnabledProperty);
    public static void SetEnabled(TextBox element, bool value) => element.SetValue(EnabledProperty, value);
}
