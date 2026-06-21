using ModelContextProtocol.Server;
using System.ComponentModel;

namespace dnSpy.Extension.MalwareMCP.Tools;

[McpServerToolType]
public static class NavigationTools
{
    /// <summary>Get the currently selected node in dnSpy's tree view</summary>
    [McpServerTool, Description("Get what the analyst currently has selected in dnSpy's tree view")]
    public static object GetSelectedNode(DnSpyBridge bridge)
    {
        return bridge.RunOnUiThread(() =>
        {
            var nodes = bridge.TreeView.TreeView.SelectedItems;
            return new
            {
                selected_count = nodes.Count(),
                nodes = nodes.Select(n => new
                {
                    text = n.ToString(),
                    node_type = n.GetType().Name,
                }).ToList(),
            };
        });
    }
}
