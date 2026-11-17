using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ExplorerPlus.App.Views;

public sealed class FileListView : ListView
{
    private bool _refreshPending;

    public FileListView()
    {
        ItemContainerGenerator.StatusChanged += (_, _) =>
        {
            if (ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated) RefreshPeerChildren();
        };
    }

    // WPF's own peer has a child per item and walks them all after every layout while a UIA client listens
    protected override AutomationPeer OnCreateAutomationPeer()
    {
        // as ListView's own override does, for column headers and cells
        var peer = new RealizedItemsPeer(this);
        if (View is GridView grid) peer.SetViewPeer(new GridViewAutomationPeer(grid, this));
        return peer;
    }

    // a selection made just before a rename must not pull the focus out of the rename box
    public void FocusItem(object item)
    {
        if (Keyboard.FocusedElement is TextBox box && IsAncestorOf(box)) return;
        if (ItemContainerGenerator.ContainerFromItem(item) is ListViewItem c) c.Focus();
    }

    // after a scroll the realized rows change without any change to the items
    private void RefreshPeerChildren()
    {
        if (_refreshPending || UIElementAutomationPeer.FromElement(this) is not RealizedItemsPeer peer) return;
        _refreshPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _refreshPending = false;
            peer.ResetChildrenCache();
        });
    }

    private Panel? FindItemsHost()
    {
        var stack = new Stack<DependencyObject>([this]);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is Panel { IsItemsHost: true } panel && ItemsControl.GetItemsOwner(panel) == this) return panel;
            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(node, i));
        }
        return null;
    }

    private sealed class RealizedItemsPeer(FileListView owner) : ListViewAutomationPeer(owner)
    {
        private Dictionary<object, AutomationPeer> _peers = new(ReferenceEqualityComparer.Instance);
        private Panel? _host;

        public void SetViewPeer(IViewAutomationPeer peer) => ViewAutomationPeer = peer;

        protected override List<AutomationPeer>? GetChildrenCore()
        {
            if (owner.IsGrouping) return base.GetChildrenCore();
            if (_host is null || ItemsControl.GetItemsOwner(_host) != owner) _host = owner.FindItemsHost();

            var generator = owner.ItemContainerGenerator;
            var realized = new List<(int Index, object Item)>();
            if (_host is not null)
                foreach (UIElement child in _host.Children)
                {
                    // recycled containers stay in the panel with no item
                    var index = generator.IndexFromContainer(child);
                    if (index >= 0) realized.Add((index, generator.ItemFromContainer(child)));
                }
            realized.Sort((a, b) => a.Index.CompareTo(b.Index));

            var peers = new Dictionary<object, AutomationPeer>(ReferenceEqualityComparer.Instance);
            var children = new List<AutomationPeer>(realized.Count + 1);
            foreach (var (_, item) in realized)
            {
                if (peers.ContainsKey(item)) continue;
                var peer = _peers.GetValueOrDefault(item) ?? CreateItemAutomationPeer(item);
                if (peer is null) continue;
                peers[item] = peer;
                children.Add(peer);
            }
            _peers = peers;
            // the details view puts its column headers first
            return ViewAutomationPeer?.GetChildren(children) ?? children;
        }
    }
}
