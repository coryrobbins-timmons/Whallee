using System.IO;
using System.Windows;
using System.Windows.Controls;
using Whallee.Core.Configuration;

namespace Whallee.App.Dialogs;

/// <summary>
/// "Rename…": edit a clip's base name. The extension is fixed and shown beside the box.
/// </summary>
/// <remarks>
/// THE EXTENSION IS NOT EDITABLE, by design. Changing it cannot make a video play better and can
/// make it unplayable: ClipResolver.ListLibraryFiles filters on SupportedExtensions, so a clip
/// renamed to an unlisted extension would vanish from the library entirely.
///
/// EVERY HANDLER IS SUBSCRIBED HERE, after InitializeComponent, and none in the markup. TextChanged
/// is the one that makes this mandatory rather than tidy: a Text="..." attribute raises it
/// synchronously while InitializeComponent is still walking the tree, before OkButton and ErrorText
/// have been created, and this app has already been killed once exactly that way. OkButton's Click
/// is subscribed here too, for uniformity - a Click cannot fire that early, but a reader should not
/// have to know which is which to trust this file.
/// </remarks>
public partial class RenameClipDialog : Window
{
    private readonly string _originalBaseName;

    /// <summary>The chosen base name, without extension. Null unless the dialog was accepted.</summary>
    public string? NewBaseName { get; private set; }

    public RenameClipDialog(string fileName)
    {
        InitializeComponent();

        _originalBaseName = Path.GetFileNameWithoutExtension(fileName);
        ExtensionText.Text = Path.GetExtension(fileName);
        NameBox.Text = _originalBaseName;

        NameBox.TextChanged += NameBox_TextChanged;
        OkButton.Click += Ok_Click;

        // Selecting the whole name makes "type a replacement" the default action, which is what a
        // rename usually is.
        NameBox.SelectAll();
        NameBox.Focus();

        UpdateState();
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateState();

    /// <remarks>
    /// An unchanged name is NOT an error - there is simply nothing to do, so the button is disabled
    /// without a red message scolding the user for opening the dialog. The comparison is Ordinal on
    /// purpose: a case-only rename (Forest -> forest) IS a change, and the rename flow supports it.
    /// </remarks>
    private void UpdateState()
    {
        var error = ClipRename.Validate(NameBox.Text);

        // COMPARED NORMALISED, so typing a trailing space onto the existing name is correctly
        // "nothing to do" rather than an apparent edit that would rename the file to itself.
        var unchanged = error is null &&
                        string.Equals(ClipRename.Normalise(NameBox.Text), _originalBaseName,
                                      StringComparison.Ordinal);

        OkButton.IsEnabled = error is null && !unchanged;

        ErrorText.Text = error ?? string.Empty;
        ErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <remarks>
    /// NORMALISED HERE, AT COMMIT - never in <see cref="NameBox_TextChanged"/>. Trimming as the user
    /// types would delete the space the instant it is typed, making "forest at dusk" impossible to
    /// enter: the space would vanish before the next word could be started.
    /// </remarks>
    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        NewBaseName = ClipRename.Normalise(NameBox.Text);
        DialogResult = true;
    }
}
