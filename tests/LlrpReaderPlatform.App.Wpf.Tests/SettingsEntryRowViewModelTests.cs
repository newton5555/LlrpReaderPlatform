using LlrpReaderPlatform.App.Wpf.ViewModels;
using LlrpReaderPlatform.Contracts.Settings;
using Xunit;

namespace LlrpReaderPlatform.App.Wpf.Tests;

public sealed class SettingsEntryRowViewModelTests
{
    private static SettingsEntry ChoiceEntry() => new()
    {
        Key = "antenna",
        Title = "Antenna",
        EditorKind = EditorKind.Choice,
        ValueType = typeof(ushort),
        CurrentValue = (ushort)1,
        Options =
        [
            new SettingsOption((ushort)1, "A1"),
            new SettingsOption((ushort)2, "A2"),
        ],
    };

    [Fact]
    public void Choice_exposes_displays_and_syncs_value()
    {
        var row = new SettingsEntryRowViewModel(ChoiceEntry());

        Assert.True(row.IsChoice);
        Assert.False(row.IsBoolean);
        Assert.Equal(0, row.SelectedChoiceIndex);
        Assert.Equal("1", row.ValueText);
        Assert.Equal(new[] { "A1", "A2" }, row.ChoiceDisplays);
        Assert.Equal((ushort)1, row.SelectedChoiceValue);

        row.SelectedChoiceIndex = 1;

        Assert.Equal("2", row.ValueText);
        Assert.Equal((ushort)2, row.SelectedChoiceValue);
    }

    [Fact]
    public void Numeric_capability_options_select_the_underlying_dBm_value()
    {
        var row = new SettingsEntryRowViewModel(new SettingsEntry
        {
            Key = "tx-power-dbm",
            Title = "Tx Power",
            EditorKind = EditorKind.Decimal,
            ValueType = typeof(decimal),
            CurrentValue = 20.5m,
            Options =
            [
                new SettingsOption(10m, "Index 0: 10 dBm"),
                new SettingsOption(20.5m),
            ],
        });

        Assert.Equal(1, row.SelectedChoiceIndex);
        Assert.Equal("Index 0: 10 dBm", row.ChoiceDisplays[0]);
        Assert.Equal("20.5", row.ChoiceDisplays[1]);
        Assert.Equal("20.5", row.ValueText);

        row.SelectedChoiceIndex = 0;

        Assert.Equal("10", row.ValueText);
        Assert.Equal(10m, row.SelectedChoiceValue);
    }

    [Fact]
    public void Boolean_syncs_value_text()
    {
        var row = new SettingsEntryRowViewModel(new SettingsEntry
        {
            Key = "enabled",
            Title = "Enabled",
            EditorKind = EditorKind.Boolean,
            ValueType = typeof(bool),
            CurrentValue = true,
        });

        Assert.True(row.IsBoolean);
        Assert.True(row.BooleanValue);
        Assert.Equal("True", row.ValueText);

        row.BooleanValue = false;
        Assert.Equal("False", row.ValueText);
    }

    [Fact]
    public void Text_row_is_editable_and_readonly_is_exposed()
    {
        var row = new SettingsEntryRowViewModel(new SettingsEntry
        {
            Key = "tx-power-dbm",
            Title = "Tx Power",
            EditorKind = EditorKind.Decimal,
            ValueType = typeof(decimal),
            CurrentValue = 20m,
        });
        var readonlyRow = new SettingsEntryRowViewModel(new SettingsEntry
        {
            Key = "capability-pending",
            Title = "Pending",
            EditorKind = EditorKind.Text,
            ValueType = typeof(string),
            ReadOnlyReason = "需要连接",
        });

        Assert.True(row.IsText);
        Assert.False(row.IsReadOnly);
        Assert.True(row.IsEditable);
        Assert.Equal("20", row.ValueText);

        Assert.True(readonlyRow.IsReadOnly);
        Assert.False(readonlyRow.IsEditable);
        Assert.Equal("需要连接", readonlyRow.ReadOnlyReason);
    }

    [Fact]
    public void Collection_row_syncs_selected_values_to_semantic_text()
    {
        var row = new SettingsEntryRowViewModel(new SettingsEntry
        {
            Key = "channels",
            Title = "Channels",
            EditorKind = EditorKind.Collection,
            ValueType = typeof(string),
            CurrentValue = "1,3",
            Options =
            [
                new SettingsOption(1, "1 - 865 MHz"),
                new SettingsOption(2, "2 - 866 MHz"),
                new SettingsOption(3, "3 - 867 MHz"),
            ],
        });

        Assert.True(row.IsCollection);
        Assert.False(row.IsText);
        Assert.Equal("1,3", row.ValueText);
        Assert.Equal(3, row.CollectionItems.Count);

        row.CollectionItems[1].IsSelected = true;

        Assert.Equal("1,2,3", row.ValueText);
    }
}
