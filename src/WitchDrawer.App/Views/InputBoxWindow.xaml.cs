using System.Windows;
using System.Windows.Input;

namespace WitchDrawer.App.Views;

/// <summary>
/// 轻量单行输入对话框，返回用户输入的文本（取消返回 null）。
/// 用于「合并到其他盒子」时让用户输入目标盒序号。
/// </summary>
public partial class InputBoxWindow : Window
{
    private InputBoxWindow(string prompt, string title, string defaultValue)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        EntryBox.Text = defaultValue;
        EntryBox.SelectAll();
        Loaded += (_, _) => Keyboard.Focus(EntryBox);
    }

    /// <summary>
    /// 显示模态输入框，返回用户输入的文本；用户取消或关闭窗口返回 null。
    /// </summary>
    public static string? Show(
        string prompt,
        string title,
        string defaultValue = "")
    {
        var dialog = new InputBoxWindow(prompt, title, defaultValue)
        {
            Owner = Application.Current.MainWindow
        };
        return dialog.ShowDialog() == true
            ? dialog.EntryBox.Text.Trim()
            : null;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnEntryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DialogResult = true;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }
}