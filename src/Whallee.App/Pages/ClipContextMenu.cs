using System.Windows.Controls;
using Whallee.App.ViewModels;
using Whallee.Core.Displays;

namespace Whallee.App.Pages;

/// <summary>What the tile menu can do. Supplied by the page that owns the menu.</summary>
internal sealed record ClipMenuActions(
    Action<ClipTile, string?> Apply,
    Action<ClipTile> PlayInDefaultPlayer,
    Action<ClipTile> Rename,
    Action<ClipTile> Edit,
    Action<ClipTile, string?> SetGenre,
    Action<ClipTile> NewGenre,
    Action<ClipTile> OpenFileLocation,
    Action<ClipTile> ShowDetails,
    Action<ClipTile> Delete);

/// <summary>
/// Builds the right-click menu for one library tile, fresh on every open.
/// </summary>
/// <remarks>
/// BUILT IN CODE BECAUSE "Apply to" IS DYNAMIC. Its items come from the displays attached RIGHT
/// NOW - DisplayEnumerator.Enumerate() at open time, not the view model's cached Targets list.
/// LibraryViewModel.RefreshTargets exists precisely because displays change under this app while it
/// runs; a submenu built once at startup would drift the same way the page's ComboBox did before
/// RefreshTargets was added.
///
/// NOT because markup handlers are banned. The CLAUDE.md rule is about handlers that fire DURING
/// InitializeComponent - a Text="..." attribute raising TextChanged - and Click cannot. Tile_Click
/// is wired in markup in this very DataTemplate. Do not "fix" it.
///
/// A ContextMenu LIVES IN ITS OWN VISUAL TREE, so VisualTreeHelper will not walk from a MenuItem
/// back to the tile. Every item therefore CAPTURES its ClipTile in the handler closure at build
/// time rather than trying to find it again later.
/// </remarks>
internal static class ClipContextMenu
{
    public static void Populate(
        ContextMenu menu, ClipTile tile, ClipMenuActions actions, bool canEdit,
        IReadOnlyList<string> genres, string? currentGenre)
    {
        // Cleared, not appended to: this runs on every open, and the menu instance is reused.
        menu.Items.Clear();

        menu.Items.Add(BuildApplyTo(tile, actions));
        Add(menu, "Play in default player", () => actions.PlayInDefaultPlayer(tile));

        // OWNER'S PLACEMENT, chosen 2026-09-03: directly below "Play in default player", near the
        // top rather than beside Delete. It stays OUTSIDE the separator - renaming changes
        // something, but nothing is lost by it. Do not "tidy" it down next to Delete.
        Add(menu, "Rename…", () => actions.Rename(tile));

        // DISABLED RATHER THAN HIDDEN when there is nothing to re-import from. A clip added from a
        // local file, or imported before Whallee started recording links on 2026-09-11, has no
        // source - and an item that silently vanishes reads as a bug, where a greyed one with a
        // reason reads as an explanation.
        var edit = new MenuItem { Header = "Edit…", IsEnabled = canEdit };
        if (canEdit) edit.Click += (_, _) => actions.Edit(tile);
        else edit.ToolTip = "Whallee has no record of where this clip came from.";
        menu.Items.Add(edit);

        menu.Items.Add(BuildGenre(tile, actions, genres, currentGenre));

        Add(menu, "Open file location", () => actions.OpenFileLocation(tile));
        Add(menu, "Details…", () => actions.ShowDetails(tile));

        // LAST, AND BEHIND A SEPARATOR. The only item here that can lose data, kept away from
        // anything harmless.
        menu.Items.Add(new Separator());
        Add(menu, "Delete", () => actions.Delete(tile));
    }

    private static void Add(ContextMenu menu, string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        menu.Items.Add(item);
    }

    private static MenuItem BuildApplyTo(ClipTile tile, ClipMenuActions actions)
    {
        var parent = new MenuItem { Header = "Apply to" };

        var all = new MenuItem { Header = "All displays" };
        all.Click += (_, _) => actions.Apply(tile, null);
        parent.Items.Add(all);

        // Enumerated HERE, on open, for the reason in the class remarks.
        var displays = DisplayEnumerator.Enumerate();
        if (displays.Count > 0) parent.Items.Add(new Separator());

        foreach (var display in displays)
        {
            // Captured into a local: the loop variable must not be closed over directly.
            var deviceName = display.DeviceName;

            var item = new MenuItem { Header = Whallee.Core.Desktop.DisplayLabel.For(deviceName) };
            item.Click += (_, _) => actions.Apply(tile, deviceName);
            parent.Items.Add(item);
        }

        return parent;
    }

    /// <summary>
    /// The Genre submenu, built from live state exactly as "Apply to" is.
    /// </summary>
    /// <remarks>
    /// The current genre is TICKED rather than merely listed - the menu is the only place the
    /// owner can see what a clip is filed under without opening Details.
    ///
    /// "Remove from genre" is offered only when there is something to remove, so the menu does not
    /// advertise an action that would do nothing.
    /// </remarks>
    private static MenuItem BuildGenre(
        ClipTile tile, ClipMenuActions actions, IReadOnlyList<string> genres, string? currentGenre)
    {
        var parent = new MenuItem { Header = "Genre" };

        foreach (var genre in genres)
        {
            var item = new MenuItem
            {
                Header = genre,
                IsCheckable = true,
                IsChecked = Whallee.Core.Configuration.ClipGenres.Same(genre, currentGenre)
            };

            // CAPTURED IN THE CLOSURE, like every other item here: a ContextMenu lives in its own
            // visual tree, so VisualTreeHelper cannot walk from a MenuItem back to the tile.
            var captured = genre;
            item.Click += (_, _) => actions.SetGenre(tile, captured);
            parent.Items.Add(item);
        }

        if (genres.Count > 0) parent.Items.Add(new Separator());

        var create = new MenuItem { Header = "New genre…" };
        create.Click += (_, _) => actions.NewGenre(tile);
        parent.Items.Add(create);

        if (currentGenre is not null)
        {
            var clear = new MenuItem { Header = "Remove from genre" };
            clear.Click += (_, _) => actions.SetGenre(tile, null);
            parent.Items.Add(clear);
        }

        return parent;
    }
}
