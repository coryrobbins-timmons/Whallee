using System.Windows;

namespace Whallee.App.Dialogs;

/// <summary>
/// Asks for one line of text, validating as it is typed.
/// </summary>
/// <remarks>
/// EVERY HANDLER IS SUBSCRIBED HERE, after InitializeComponent, and none in the markup. TextChanged
/// is what makes that mandatory rather than tidy: a Text="..." attribute raises it synchronously
/// while InitializeComponent is still walking the tree, before OkButton and ErrorText exist, and
/// this app has already been killed exactly that way. RenameClipDialog states the same rule.
/// </remarks>
public partial class TextPromptDialog : Window
{
    private readonly Func<string, string?> _validate;

    /// <summary>The trimmed text, or null unless the dialog was accepted.</summary>
    public string? Value { get; private set; }

    private TextPromptDialog(string title, string label, string? initial, Func<string, string?> validate)
    {
        InitializeComponent();
        Interop.DarkTitleBar.Apply(this);

        _validate = validate;

        Title = title;
        LabelText.Text = label;

        ValueBox.TextChanged += (_, _) => Revalidate();
        OkButton.Click += (_, _) => Accept();

        // Set AFTER the subscription, so the validation runs for it and OK starts in the right
        // state. Before InitializeComponent returned, the elements it touches did not exist.
        ValueBox.Text = initial ?? string.Empty;
        ValueBox.SelectAll();
        ValueBox.Focus();
    }

    private void Revalidate()
    {
        var reason = _validate(ValueBox.Text.Trim());

        OkButton.IsEnabled = reason is null;
        ErrorText.Text = reason ?? string.Empty;
        ErrorText.Visibility = reason is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Accept()
    {
        // Re-checked rather than trusted: IsEnabled gates the button, but Enter reaches IsDefault
        // by another path and this is the only place the value escapes.
        if (_validate(ValueBox.Text.Trim()) is not null) return;

        Value = ValueBox.Text.Trim();
        DialogResult = true;
    }

    /// <summary>Shows the prompt. Returns the entered text, or null if cancelled.</summary>
    public static string? Ask(
        Window? owner, string title, string label, string? initial, Func<string, string?> validate)
    {
        var dialog = new TextPromptDialog(title, label, initial, validate);

        // Only when loaded: WPF throws if the owner has not been shown yet.
        if (owner is not null && owner.IsLoaded) dialog.Owner = owner;

        return dialog.ShowDialog() == true ? dialog.Value : null;
    }
}
