using System.Windows.Controls;
using Whallee.App.ViewModels;
using Whallee.Core.Displays;

namespace Whallee.App.Pages;

/// <summary>What the tile menu can do. Supplied by the page that owns the menu.</summary>
internal sealed record ClipMenuActions(
    Action<ClipTile, string?> Apply,
    Action<ClipTile> PlayInDefaultPlayer,
    Action<ClipTile> OpenFileLocation,
    Action<ClipTile> ShowDetails);

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
    public static void Populate(ContextMenu menu, ClipTile tile, ClipMenuActions actions)
    {
        // Cleared, not appended to: this runs on every open, and the menu instance is reused.
        menu.Items.Clear();

        menu.Items.Add(BuildApplyTo(tile, actions));
        Add(menu, "Play in default player", () => actions.PlayInDefaultPlayer(tile));
        Add(menu, "Open file location", () => actions.OpenFileLocation(tile));
        Add(menu, "Details…", () => actions.ShowDetails(tile));
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

            var item = new MenuItem { Header = deviceName };
            item.Click += (_, _) => actions.Apply(tile, deviceName);
            parent.Items.Add(item);
        }

        return parent;
    }
}
