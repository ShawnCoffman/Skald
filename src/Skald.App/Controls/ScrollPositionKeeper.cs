using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Skald.App.Controls;

internal sealed class ScrollPositionKeeper : IDisposable
{
    private readonly UIElement _root;
    private readonly List<(ScrollViewer Viewer, double Horizontal, double Vertical)> _positions = [];

    private ScrollPositionKeeper(UIElement root)
    {
        _root = root;
        Visit(root);
    }

    public static ScrollPositionKeeper Capture(UIElement root) => new(root);

    public void Dispose()
    {
        if (_positions.Count == 0) return;
        _root.UpdateLayout();
        foreach (var (viewer, horizontal, vertical) in _positions)
        {
            if (Math.Abs(viewer.HorizontalOffset - horizontal) > 1 || Math.Abs(viewer.VerticalOffset - vertical) > 1)
                viewer.ChangeView(horizontal, vertical, null, true);
        }
    }

    private void Visit(DependencyObject node)
    {
        if (node is ScrollViewer viewer && (viewer.VerticalOffset > 1 || viewer.HorizontalOffset > 1))
            _positions.Add((viewer, viewer.HorizontalOffset, viewer.VerticalOffset));
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            Visit(VisualTreeHelper.GetChild(node, index));
    }
}
