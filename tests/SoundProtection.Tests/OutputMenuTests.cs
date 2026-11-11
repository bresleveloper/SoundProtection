using SoundProtection.Core;

namespace SoundProtection.Tests;

public class OutputMenuTests
{
    private static readonly OutputDevice Headphones = new("hp", "Headphones (Realtek(R) Audio)", IsDefault: true);
    private static readonly OutputDevice Speakers = new("sp", "Speakers (Realtek(R) Audio)", IsDefault: false);

    [Fact]
    public void Follow_default_comes_first_and_is_selected_when_nothing_is_chosen()
    {
        var menu = OutputMenu.Build(new[] { Headphones, Speakers }, null);

        Assert.Equal(
            new[] { "Follow Windows default", "Headphones (Realtek(R) Audio) (default)", "Speakers (Realtek(R) Audio)" },
            menu.Labels);
        Assert.Equal(new string?[] { null, "hp", "sp" }, menu.Ids);
        Assert.Equal(0, menu.SelectedIndex);
    }

    [Fact]
    public void A_chosen_output_that_is_present_is_selected()
    {
        var menu = OutputMenu.Build(new[] { Headphones, Speakers }, "sp");

        Assert.Equal(2, menu.SelectedIndex);
        Assert.Equal("sp", menu.Ids[menu.SelectedIndex]);
    }

    [Fact]
    public void A_chosen_output_that_is_not_connected_stays_visible_and_selected()
    {
        var menu = OutputMenu.Build(new[] { Headphones }, "gone");

        Assert.Equal("Saved output (not connected)", menu.Labels[^1]);
        Assert.Equal("gone", menu.Ids[^1]);
        Assert.Equal(menu.Labels.Count - 1, menu.SelectedIndex);
    }

    [Fact]
    public void With_no_outputs_follow_default_is_still_offered()
    {
        var menu = OutputMenu.Build(Array.Empty<OutputDevice>(), null);

        Assert.Single(menu.Labels);
        Assert.Equal(0, menu.SelectedIndex);
    }
}

public class DeviceLabelsTests
{
    [Fact]
    public void A_normal_device_keeps_its_name() =>
        Assert.Equal(
            "Headphones (Realtek(R) Audio)",
            DeviceLabels.Describe("Headphones (Realtek(R) Audio)", fellBackToDefault: false));

    [Fact]
    public void A_fallback_says_where_the_sound_is_going() =>
        Assert.Equal(
            "Speakers (Realtek(R) Audio) (chosen output not connected: showing the Windows default)",
            DeviceLabels.Describe("Speakers (Realtek(R) Audio)", fellBackToDefault: true));

    [Fact]
    public void The_friendly_name_is_the_device_name_without_the_fallback_note() =>
        Assert.Equal(
            "Speakers (Realtek(R) Audio)",
            DeviceLabels.FriendlyName(DeviceLabels.Describe("Speakers (Realtek(R) Audio)", fellBackToDefault: true)));

    [Fact]
    public void The_friendly_name_of_a_plain_label_is_itself() =>
        Assert.Equal(
            "Headphones (Realtek(R) Audio)",
            DeviceLabels.FriendlyName(DeviceLabels.Describe("Headphones (Realtek(R) Audio)", fellBackToDefault: false)));
}
