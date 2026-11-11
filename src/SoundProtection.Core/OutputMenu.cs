namespace SoundProtection.Core;

/// <summary>
/// What the panel's output dropdown lists. "Follow Windows default" is always first, and a saved output that
/// is not connected right now stays visible and selected, so the panel never misreports which output is chosen.
/// </summary>
public sealed record OutputMenu(IReadOnlyList<string> Labels, IReadOnlyList<string?> Ids, int SelectedIndex)
{
    public const string FollowDefault = "Follow Windows default";
    public const string NotConnected = "Saved output (not connected)";

    public static OutputMenu Build(IReadOnlyList<OutputDevice> outputs, string? selectedId)
    {
        var labels = new List<string> { FollowDefault };
        var ids = new List<string?> { null };
        foreach (var output in outputs)
        {
            labels.Add(output.IsDefault ? output.Name + " (default)" : output.Name);
            ids.Add(output.Id);
        }

        var selected = 0;
        if (selectedId is not null)
        {
            selected = ids.IndexOf(selectedId);
            if (selected < 0)
            {
                labels.Add(NotConnected);
                ids.Add(selectedId);
                selected = labels.Count - 1;
            }
        }

        return new OutputMenu(labels, ids, selected);
    }
}
