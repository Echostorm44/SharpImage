using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SharpImage.Editor.Assets;

namespace SharpImage.Editor.Controls;

/// <summary>
/// A collapsible panel with a clickable header bar containing the panel name,
/// an icon, and a collapse/expand chevron. Used in the right panel dock.
/// Uses Grid layout (header=Auto, content=*) so it fills its allocated space.
/// Content scrolls internally via its own ScrollViewer unless managesOwnScroll is true.
/// </summary>
public sealed class CollapsiblePanel : Border
{
    private readonly Control contentHost;
    private readonly Viewbox chevron;
    private bool isExpanded = true;

    public string PanelName { get; }

    /// <summary>Fired when the expanded/collapsed state changes.</summary>
    public event Action? ExpandedChanged;

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            isExpanded = value;
            contentHost.IsVisible = value;
            UpdateChevron();
            ExpandedChanged?.Invoke();
        }
    }

    /// <param name="managesOwnScroll">
    /// When true, content is placed directly without a wrapping ScrollViewer.
    /// Use this when the content needs its own scroll layout (e.g., layers panel
    /// with pinned top/bottom bars and a scrollable list in between).
    /// </param>
    public CollapsiblePanel(string panelName, string iconResourceKey, Control content,
        bool managesOwnScroll = false)
    {
        PanelName = panelName;
        Classes.Add("panel");

        var headerIcon = LucideIcon.CreateFromResource(this, iconResourceKey, 14);

        var headerLabel = new TextBlock
        {
            Text = panelName,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerLabel[!TextBlock.ForegroundProperty] = this.GetResourceObservable("LabelSecondaryBrush").ToBinding();

        chevron = LucideIcon.CreateFromResource(this, "IconChevronDown", 12);
        chevron.VerticalAlignment = VerticalAlignment.Center;

        var headerRow = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
            Height = 28,
            Margin = new Thickness(8, 0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };

        var leftGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        leftGroup.Children.Add(headerIcon);
        leftGroup.Children.Add(headerLabel);

        Grid.SetColumn(leftGroup, 0);
        Grid.SetColumn(chevron, 2);
        headerRow.Children.Add(leftGroup);
        headerRow.Children.Add(chevron);

        headerRow.PointerPressed += (_, _) => IsExpanded = !IsExpanded;

        if (managesOwnScroll)
        {
            contentHost = content;
        }
        else
        {
            contentHost = new ScrollViewer
            {
                Content = content,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            };
        }

        // Grid: header row is fixed (Auto), content row fills remaining space (*)
        var outerGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
        };
        Grid.SetRow(headerRow, 0);
        Grid.SetRow(contentHost, 1);
        outerGrid.Children.Add(headerRow);
        outerGrid.Children.Add(contentHost);

        Child = outerGrid;
    }

    private void UpdateChevron()
    {
        // Rotate the chevron: 0° expanded (pointing down), -90° collapsed (pointing right)
        chevron.RenderTransform = isExpanded
            ? null
            : new RotateTransform(-90);
        chevron.RenderTransformOrigin = RelativePoint.Center;
    }

    /// <summary>
    /// Provides access to the content ScrollViewer for operations like ScrollToEnd.
    /// Returns null if managesOwnScroll was true.
    /// </summary>
    public ScrollViewer? ContentScrollViewer => contentHost as ScrollViewer;
}
