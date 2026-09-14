using System.Windows;
using System.Windows.Controls;
using Whallee.Core.Configuration;

namespace Whallee.App.Pages;

/// <summary>What the genre bar can do. Supplied by the page that owns it.</summary>
/// <param name="Select">Show this genre; null means All, and <see cref="GenreBar.Ungrouped"/> means the untagged ones.</param>
internal sealed record GenreBarActions(
    Action<string?> Select,
    Action<string> Rename,
    Action<string> Delete,
    Action<string, int> Move);

/// <summary>
/// Builds the library's genre filter bar, fresh every time the genres change.
/// </summary>
/// <remarks>
/// BUILT IN CODE, like ClipContextMenu and for the same reason: the buttons ARE the live genre list,
/// and markup cannot know it. This is its own file because LibraryPage.xaml.cs is already large.
///
/// A tab LIVES IN THE PAGE'S VISUAL TREE, unlike a context menu, but each button still captures its
/// genre in the handler closure - walking the tree to recover it would be strictly more work for
/// the same answer.
/// </remarks>
internal static class GenreBar
{
    /// <summary>The pseudo-genre standing for "clips with no genre".</summary>
    /// <remarks>
    /// A SENTINEL, NOT A REAL GENRE. It is never stored, never renamed and never deleted - it is
    /// how the bar asks for the untagged clips, which null cannot express because null already
    /// means All.
    /// </remarks>
    public const string Ungrouped = "\u0000ungrouped";

    public static void Populate(
        Panel host, IReadOnlyList<string> genres, string? selected, bool hasUngrouped,
        GenreBarActions actions)
    {
        host.Children.Clear();

        // HIDDEN ENTIRELY UNTIL THERE IS A GENRE. Before the owner tags anything the bar would be a
        // single "All" button that filters nothing.
        if (genres.Count == 0)
        {
            host.Visibility = Visibility.Collapsed;
            return;
        }

        host.Visibility = Visibility.Visible;

        host.Children.Add(Tab("All", selected is null, () => actions.Select(null)));

        foreach (var genre in genres)
        {
            var captured = genre;
            var button = Tab(genre, ClipGenres.Same(genre, selected), () => actions.Select(captured));
            button.ContextMenu = Manage(captured, actions);
            host.Children.Add(button);
        }

        // Only when there is something in it: an "Ungrouped" tab that is always empty is noise.
        if (hasUngrouped)
            host.Children.Add(Tab(
                "Ungrouped", selected == Ungrouped, () => actions.Select(Ungrouped)));
    }

    private static Button Tab(string header, bool isSelected, Action onClick)
    {
        var button = new Button
        {
            Content = header,
            Margin = new Thickness(0, 0, 8, 0),

            // The selected tab uses the app's primary button look, so "which one am I on?" is
            // answered by the same visual language as every other affirmative control.
            Style = isSelected
                ? (Style)Application.Current.FindResource("PrimaryButtonStyle")
                : null
        };

        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>The per-tab menu. Ungrouped and All never get one - neither is a real genre.</summary>
    private static ContextMenu Manage(string genre, GenreBarActions actions)
    {
        var menu = new ContextMenu();

        Add(menu, "Rename…", () => actions.Rename(genre));
        Add(menu, "Move left", () => actions.Move(genre, -1));
        Add(menu, "Move right", () => actions.Move(genre, +1));

        // LAST AND BEHIND A SEPARATOR, matching ClipContextMenu: the only item here that changes
        // more than one clip is kept away from the harmless ones.
        menu.Items.Add(new Separator());
        Add(menu, "Delete genre", () => actions.Delete(genre));

        return menu;
    }

    private static void Add(ContextMenu menu, string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        menu.Items.Add(item);
    }
}
