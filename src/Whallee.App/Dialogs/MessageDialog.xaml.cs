using System.Windows;

namespace Whallee.App.Dialogs;

/// <summary>
/// The app's own message and confirmation dialog, replacing every stock <c>MessageBox</c>.
/// </summary>
/// <remarks>
/// WHY NOT MessageBox. A stock MessageBox is themed by Windows, not by this app: light chrome, a
/// system icon and a system font in the middle of a dark interface. The owner flagged the delete
/// confirmation on 2026-09-11 as looking "really old"; the same complaint had already produced
/// ImportSummary's themed card on 2026-09-09. This is that fix generalised, so the remaining three
/// call sites stop being found one at a time.
///
/// THE CONFIRM VARIANT DEFAULTS TO CANCEL, and that is load-bearing rather than tidy. The
/// MessageBox it replaces passed MessageBoxResult.No as its fourth argument specifically so Enter
/// cancels instead of confirming; deleting a clip is the only destructive action in the app, and a
/// stray keypress must not do it. Cancel is therefore IsDefault as well as IsCancel, so Enter and
/// Escape both dismiss safely, and confirming takes a deliberate click.
///
/// EVERY HANDLER IS SUBSCRIBED HERE, after InitializeComponent, never in the markup - this app's
/// standing convention, stated at length in RenameClipDialog.
/// </remarks>
public partial class MessageDialog : Window
{
    private MessageDialog(string headline, string? detail)
    {
        InitializeComponent();
        Interop.DarkTitleBar.Apply(this);

        HeadlineText.Text = headline;

        if (!string.IsNullOrWhiteSpace(detail))
        {
            DetailText.Text = detail;
            DetailText.Visibility = Visibility.Visible;
        }

        ConfirmButton.Click += (_, _) => { DialogResult = true; };
        CancelButton.Click += (_, _) => { DialogResult = false; };
    }

    /// <summary>Tells the user something. One button, nothing to decide.</summary>
    public static void Show(Window? owner, string headline, string? detail = null)
    {
        var dialog = Build(owner, headline, detail);

        // One button, so it is both the default and the cancel: Enter and Escape both dismiss.
        dialog.ConfirmButton.Content = "OK";
        dialog.ConfirmButton.IsDefault = true;
        dialog.CancelButton.Visibility = Visibility.Collapsed;

        dialog.ShowDialog();
    }

    /// <summary>
    /// Asks the user to confirm. True only if they explicitly chose <paramref name="confirmText"/>.
    /// </summary>
    /// <remarks>
    /// Returns false for every other outcome - Cancel, Escape, or closing the window - because the
    /// caller acts destructively on true. Never invert this into "not cancelled".
    /// </remarks>
    public static bool Confirm(
        Window? owner, string headline, string? detail, string confirmText, string cancelText = "Cancel")
    {
        var dialog = Build(owner, headline, detail);

        dialog.ConfirmButton.Content = confirmText;
        dialog.CancelButton.Content = cancelText;

        // SEE THE CLASS REMARKS: Cancel is the default so Enter cannot confirm a destructive action.
        dialog.ConfirmButton.IsDefault = false;
        dialog.CancelButton.IsDefault = true;

        return dialog.ShowDialog() == true;
    }

    private static MessageDialog Build(Window? owner, string headline, string? detail)
    {
        var dialog = new MessageDialog(headline, detail);

        // A null owner would otherwise centre on the screen rather than the app, and could sit
        // behind the main window. Only set it when the owner is actually loaded - WPF throws if
        // the owner has not been shown yet.
        if (owner is not null && owner.IsLoaded) dialog.Owner = owner;

        return dialog;
    }
}
