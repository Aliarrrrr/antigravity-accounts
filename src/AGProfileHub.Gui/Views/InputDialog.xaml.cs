using System.Windows;

namespace AGProfileHub.Gui.Views;

public partial class InputDialog : Window
{
    public InputDialog(string title, string message, string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        InputBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    public string Value => InputBox.Text;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    public static string? Ask(Window owner, string title, string message, string initialValue = "")
    {
        var dialog = new InputDialog(title, message, initialValue) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }
}
