using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snip.Infrastructure;

namespace Snip.App.Views;

public sealed class WeChatShareWindow : Window
{
    private readonly CancellationTokenSource cancellation = new();
    public WeChatShareWindow(BitmapSource image, IImageExportService export)
    {
        Ui.ApplyWindow(this);
        Title = "自用截图 · 微信分享"; Width = 720; Height = 760; MinWidth = 600; MinHeight = 620; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var automation = new WeChatAutomation(); var root = new DockPanel { Margin = new Thickness(24) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(Ui.Title("分享给微信联系人"));
        var description = Ui.Subtitle("先选择收件人，再核对截图并确认发送。请保持微信 4.1.15.12 已登录。"); description.Margin = new Thickness(0, 6, 0, 8); top.Children.Add(description);
        var row = new DockPanel { Margin = new Thickness(0, 12, 0, 8) }; top.Children.Add(row);
        var search = new Button { Content = "搜索联系人", Margin = new Thickness(10, 0, 0, 0) }; Ui.Style(search, "PrimaryButton"); DockPanel.SetDock(search, Dock.Right); row.Children.Add(search);
        var query = new TextBox { ToolTip = "输入联系人或群名称，搜索后核对结果" }; row.Children.Add(query);
        var contacts = new ListBox { Height = 110, DisplayMemberPath = nameof(WeChatContact.DisplayName) }; top.Children.Add(contacts);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var status = Ui.Subtitle("先搜索、选择收件人，再确认发送下方图片。"); status.Margin = new Thickness(0, 14, 0, 10); bottom.Children.Add(status);
        var send = new Button { Content = "确认收件人和图片，发送", IsEnabled = false, Height = 44, Margin = new Thickness(0) }; Ui.Style(send, "PrimaryButton"); bottom.Children.Add(send);
        var preview = new DockPanel(); var caption = new TextBlock { Text = "即将发送的截图", Margin = new Thickness(0, 0, 0, 12) }; Ui.Style(caption, "SectionTitle"); DockPanel.SetDock(caption, Dock.Top); preview.Children.Add(caption);
        preview.Children.Add(new Border { Background = Ui.Brush("CanvasBrush"), CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Child = new Image { Source = image, Stretch = Stretch.Uniform } });
        var card = Ui.Card(preview, new Thickness(16)); card.Margin = new Thickness(0, 16, 0, 0); root.Children.Add(card);
        bool busy = false, closeRequested = false;
        void Restore() { busy = false; if (closeRequested) { Close(); return; } Show(); Activate(); search.IsEnabled = true; }
        search.Click += async (_, _) =>
        {
            if (busy) return; busy = true; send.IsEnabled = false; search.IsEnabled = false;
            try { status.Text = "正在微信中搜索…"; Hide(); contacts.ItemsSource = await automation.SearchAsync(query.Text.Trim(), cancellation.Token); status.Text = "请选择确切的收件人，核对预览后点击发送。"; }
            catch (Exception e) { status.Text = "搜索失败：" + e.Message; }
            finally { Restore(); }
        };
        contacts.SelectionChanged += (_, _) => send.IsEnabled = !busy && contacts.SelectedItem is WeChatContact;
        send.Click += async (_, _) =>
        {
            if (busy || contacts.SelectedItem is not WeChatContact contact) return;
            if (MessageBox.Show(this, $"向“{contact.DisplayName}”发送当前预览图片？", "确认微信发送", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            busy = true; send.IsEnabled = false; search.IsEnabled = false;
            try
            {
                Hide(); await automation.PrepareConversationAsync(contact, cancellation.Token);
                await export.CopyAsync(image); await automation.FocusComposerAsync(contact, cancellation.Token);
                await automation.PasteAndSendAsync(contact, cancellation.Token); status.Text = "已执行微信图片发送操作；不代表对方已收到或已读。";
            }
            catch (Exception e) { status.Text = "发送流程未完成：" + e.Message; }
            finally { Restore(); send.IsEnabled = contacts.SelectedItem is not null; }
        };
        Closing += (_, e) => { if (busy) { closeRequested = true; cancellation.Cancel(); e.Cancel = true; status.Text = "正在取消操作，请等待后关闭。"; } };
        Closed += (_, _) => cancellation.Dispose(); Content = root;
    }
}
