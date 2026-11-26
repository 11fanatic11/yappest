using System.Windows;

namespace Yappest.UI;

/// <summary>Small themed replacement for MessageBox / InputBox.</summary>
public partial class PromptDialog : Window
{
    public string? Result { get; private set; }

    PromptDialog(string title, string? body, string? label, string? initial, string ok, bool showCancel)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        Heading.Text = title;
        Body.Text = body ?? "";
        Body.Visibility = string.IsNullOrEmpty(body) ? Visibility.Collapsed : Visibility.Visible;
        if (label == null) InputRow.Visibility = Visibility.Collapsed;
        else { Label.Text = label.ToUpperInvariant(); Input.Text = initial ?? ""; }
        OkBtn.Content = ok;
        CancelBtn.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => { if (label != null) { Input.Focus(); Input.SelectAll(); } };
    }

    void Ok_Click(object sender, RoutedEventArgs e) { Result = Input.Text; DialogResult = true; }
    void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; }

    public static string? Ask(Window? owner, string title, string label, string initial = "")
    {
        var d = new PromptDialog(title, null, label, initial, "Save", true) { Owner = owner };
        return d.ShowDialog() == true ? d.Result : null;
    }

    public static bool Confirm(Window? owner, string title, string body, string ok = "OK")
    {
        var d = new PromptDialog(title, body, null, null, ok, true) { Owner = owner };
        return d.ShowDialog() == true;
    }

    public static void Info(Window? owner, string title, string body)
    {
        new PromptDialog(title, body, null, null, "OK", false) { Owner = owner }.ShowDialog();
    }
}
