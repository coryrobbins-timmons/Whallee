using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Whallee.Core.Configuration;

namespace Whallee.App.Pages;

/// <summary>What the genre bar can do. Supplied by the page that owns it.</summary>
/// <param name="Select">Show this genre; null means All, and <see cref="GenreBar.Ungrouped"/> means the untagged ones.</param>
/// <param name="Create">Create a new, empty genre.</param>
/// <param name="MoveTo">Move a genre to a dropped position.</param>
internal sealed record GenreBarActions(
    Action<string?> Select,
    Action<string> Rename,
    Action<string> Delete,
    Action Create,
    Action<string, int> MoveTo);

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
    /// means All. The NUL prefix is what makes it uncollidable with anything the owner can type.
    /// </remarks>
    public const string Ungrouped = "\u0000ungrouped";

    /// <summary>The drag-and-drop payload format. Private to this bar.</summary>
    private const string DragFormat = "WhalleeGenreTab";

    /// <summary>Border thickness marking the edge a drop would land on.</summary>
    private static readonly Thickness DropBefore = new(3, 1, 1, 1);
    private static readonly Thickness DropAfter = new(1, 1, 3, 1);

    public static void Populate(
        Panel host, IReadOnlyList<string> genres, string? selected, bool hasUngrouped,
        GenreBarActions actions)
    {
        host.Children.Clear();

        // ALWAYS VISIBLE NOW. It used to hide until a genre existed, which was right when tagging a
        // clip was the only way to make one; "+ New genre" lives here, so hiding the bar would hide
        // the button that creates the first genre.
        host.Visibility = Visibility.Visible;

        host.Children.Add(Tab("All", selected is null, () => actions.Select(null)));

        foreach (var genre in genres)
        {
            var captured = genre;
            var button = Tab(genre, ClipGenres.Same(genre, selected), () => actions.Select(captured));

            button.ContextMenu = Manage(captured, actions);
            MakeDraggable(button, captured, host, genres, actions);

            host.Children.Add(button);
        }

        // Only when there is something in it: an "Ungrouped" tab that is always empty is noise.
        if (hasUngrouped)
            host.Children.Add(Tab(
                "Ungrouped", selected == Ungrouped, () => actions.Select(Ungrouped)));

        // LAST, AND DELIBERATELY NOT A TAB. It creates rather than filters, so it never carries the
        // selected style and is set off by a wider margin - a button that looks like the filters
        // would read as a genre called "+ New genre".
        var create = Tab("+ New genre", isSelected: false, actions.Create);
        create.Margin = new Thickness(16, 0, 0, 0);
        host.Children.Add(create);
    }

    private static Button Tab(string header, bool isSelected, Action onClick)
    {
        var button = new Button
        {
            Content = header,
            Margin = new Thickness(0, 0, 8, 0),

            // The selected tab uses the app's primary button look, so "which one am I on?" is
            // answered by the same visual language as every other affirmative control.
            //
            // THE UNSELECTED TABS ASK FOR THE IMPLICIT STYLE BY KEY, AND MUST NOT BE LEFT null.
            // Theme/Controls.xaml carries an implicit TargetType="Button" style, and setting Style
            // locally to null BLOCKS implicit-style lookup rather than falling back to it - so a
            // null here renders the unselected tabs as native light-chrome Windows buttons sitting
            // beside the dark themed ones.
            Style = isSelected
                ? (Style)Application.Current.FindResource("PrimaryButtonStyle")
                : (Style)Application.Current.FindResource(typeof(Button))
        };

        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>
    /// Makes one genre tab draggable, and a drop target for the others.
    /// </summary>
    /// <remarks>
    /// REPLACED "Move left" / "Move right" at the owner's request on 2026-09-14, who asked for the
    /// way Chrome reorders tabs. Those menu items are gone rather than kept alongside: two ways to
    /// do one thing is worse than either.
    ///
    /// A DRAG ONLY STARTS PAST THE SYSTEM DRAG THRESHOLD. A press, a wobble of a pixel or two and a
    /// release is how people click; starting a drag on the first MouseMove would swallow the Click
    /// that selects the genre, and the tabs would read as broken rather than draggable.
    ///
    /// THE DROP POSITION COMES FROM WHICH HALF OF THE TARGET THE POINTER IS OVER, not from the
    /// target's index alone. Without that, a rightward drag can never reach the last position,
    /// because every drop would land before the tab it was dropped on.
    ///
    /// THE INDEX IS AMONG GENRES, NOT AMONG THE PANEL'S CHILDREN. "All" sits at child 0 and
    /// "Ungrouped" and "+ New genre" sit at the end, so a child index would be off by one and would
    /// allow a drop outside the genre run entirely.
    /// </remarks>
    private static void MakeDraggable(
        Button tab, string genre, Panel host, IReadOnlyList<string> genres, GenreBarActions actions)
    {
        var pressedAt = default(Point?);

        tab.PreviewMouseLeftButtonDown += (_, e) => pressedAt = e.GetPosition(host);

        // Cleared on release, so a press, a move away and a later move back cannot resurrect a drag
        // from a gesture the owner already finished as a click.
        tab.PreviewMouseLeftButtonUp += (_, _) => pressedAt = null;

        tab.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || pressedAt is not { } start) return;

            var now = e.GetPosition(host);
            if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            pressedAt = null;

            // DoDragDrop RUNS ITS OWN MESSAGE LOOP and does not return until the drop completes, so
            // nothing after it may assume this bar still exists - the drop rebuilds it.
            try
            {
                DragDrop.DoDragDrop(tab, new DataObject(DragFormat, genre), DragDropEffects.Move);
            }
            catch (Exception)
            {
                // A drag that cannot start is not worth failing the click over, and this runs on a
                // Dispatcher callback where an escaping exception would end the process.
            }
        };

        tab.AllowDrop = true;

        tab.DragOver += (_, e) =>
        {
            var carried = e.Data.GetDataPresent(DragFormat);
            e.Effects = carried ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;

            // The edge the drop would land on, thickened. An insertion caret would have to be added
            // to the panel, which reflows the very tabs the owner is aiming at.
            if (carried) tab.BorderThickness = DropsBefore(tab, e) ? DropBefore : DropAfter;
        };

        tab.DragLeave += (_, _) => tab.ClearValue(Control.BorderThicknessProperty);

        tab.Drop += (_, e) =>
        {
            tab.ClearValue(Control.BorderThicknessProperty);

            if (e.Data.GetData(DragFormat) is not string dragged) return;
            e.Handled = true;

            // Dropping a tab on itself is the ordinary way to abandon a drag.
            if (ClipGenres.Same(dragged, genre)) return;

            var target = IndexOf(genres, genre);
            var source = IndexOf(genres, dragged);
            if (target < 0 || source < 0) return;

            var index = DropsBefore(tab, e) ? target : target + 1;

            // ClipGenres.MoveTo reads the index AFTER lifting the dragged genre out. When the
            // dragged tab started to the LEFT of the target, that removal has already shifted the
            // target one place left, so the index computed above is one too high.
            if (source < target) index--;

            actions.MoveTo(dragged, index);
        };
    }

    /// <summary>Whether the pointer is over the left half of <paramref name="tab"/>.</summary>
    private static bool DropsBefore(Button tab, DragEventArgs e) =>
        e.GetPosition(tab).X < tab.ActualWidth / 2;

    private static int IndexOf(IReadOnlyList<string> genres, string name)
    {
        for (var i = 0; i < genres.Count; i++)
            if (ClipGenres.Same(genres[i], name)) return i;

        return -1;
    }

    /// <summary>The per-tab menu. All, Ungrouped and "+ New genre" never get one.</summary>
    /// <remarks>
    /// NO "Move left" / "Move right" ANY MORE - dragging replaced them on 2026-09-14. What remains
    /// is the two things a drag cannot express.
    /// </remarks>
    private static ContextMenu Manage(string genre, GenreBarActions actions)
    {
        var menu = new ContextMenu();

        Add(menu, "Rename…", () => actions.Rename(genre));

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
