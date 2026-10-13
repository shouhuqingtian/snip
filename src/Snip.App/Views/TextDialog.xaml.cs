using System.Windows;
using System.Windows.Media;

namespace Snip.App.Views;

public partial class TextDialog : Window
{
    public string TextValue => Input.Text;
    public string ColorValue => ColorBox.Text;
    public double FontSizeValue => (double)SizeBox.SelectedItem;
    public TextDialog(string text, string color, double fontSize)
    {
        Ui.EnsureTheme(); InitializeComponent(); Input.Text = text; ColorBox.Text = color;
        SizeBox.ItemsSource = new double[] { 16, 20, 24, 32, 48, 64 }; SizeBox.SelectedItem = fontSize;
        Loaded += (_, _) => { Input.Focus(); Input.CaretIndex = Input.Text.Length; };
    }
    private void ConfirmClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Input.Text)) { ErrorText.Text = "请输入文字，或点击取消。"; return; }
        try { _ = ColorConverter.ConvertFromString(ColorValue); }
        catch (Exception) { ErrorText.Text = "请输入有效颜色，例如 #FF3B30。"; return; }
        DialogResult = true;
    }
}
