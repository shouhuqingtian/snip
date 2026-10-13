using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Snip.App.Views;

public static class Ui
{
    public static void EnsureTheme()
    {
        if (Application.Current is not { } app || app.TryFindResource("Card") is not null) return;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Snip;component/Styles/Theme.xaml", UriKind.Relative) });
    }
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    public static void ApplyWindow(Window window)
    {
        EnsureTheme();
        // WPF implicit styles use the runtime type; derived Window classes need the base style explicitly.
        window.SetResourceReference(FrameworkElement.StyleProperty, typeof(Window));
    }
    public static void Style(FrameworkElement element, string key) => element.SetResourceReference(FrameworkElement.StyleProperty, key);
    public static TextBlock Title(string text) { var value = new TextBlock { Text = text }; Style(value, "PageTitle"); return value; }
    public static TextBlock Subtitle(string text) { var value = new TextBlock { Text = text }; Style(value, "Subtitle"); return value; }
    public static Border Card(UIElement content, Thickness? padding = null)
    {
        var card = new Border { Child = content }; Style(card, "Card");
        if (padding is { } value) card.Padding = value;
        return card;
    }
}
