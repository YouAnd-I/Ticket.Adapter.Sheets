using System.Text.Json.Nodes;
using Ticket.Adapter.Sheets;
using Xunit;

namespace Ticket.Adapter.Sheets.Tests;

public class SheetPlanTests
{
    private static JsonArray Row(params object?[] cells) =>
        new(cells.Select(NeonDump.Cell).ToArray());

    [Fact]
    public void Build_RenamesHeaders_TypeColumns_AndShapesValues()
    {
        var table = new NeonTable("ticket",
            ["ticket_id", "note_id", "created_at_utc", "classified_automatically", "description"],
            [
                Row("abc", 7L, "2026-01-02 03:04:05", true,
                    "a very long description that clearly exceeds fifty characters in length"),
                Row("def", 8L, "2026-02-03 04:05:06", false, null),
            ]);

        var plan = SheetPlanBuilder.Build(table, []);

        Assert.Equal(["Ticket Id", "Note Id", "Created At Utc", "Classified Automatically", "Description"],
            plan.Headers);
        Assert.Equal(ColumnKind.Identifier, plan.Columns[0].Kind);
        Assert.Equal(ColumnKind.Identifier, plan.Columns[1].Kind);
        Assert.Equal(ColumnKind.DateTime, plan.Columns[2].Kind);
        Assert.Equal(ColumnKind.Boolean, plan.Columns[3].Kind);
        Assert.Equal(ColumnKind.LongText, plan.Columns[4].Kind);

        Assert.Equal("abc", plan.Rows[0][0]!.GetValue<string>());
        Assert.Equal("7", plan.Rows[0][1]!.GetValue<string>());
        Assert.Equal("8", plan.Rows[1][1]!.GetValue<string>());
        Assert.Equal(
            SheetPlanBuilder.Serial(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)),
            plan.Rows[0][2]!.GetValue<double>(), 10);
        Assert.True(plan.Rows[0][3]!.GetValue<bool>());

        Assert.True(plan.Columns[4].Wrap);
        Assert.Equal("@", plan.Columns[1].NumberFormat);
        Assert.Equal("yyyy-mm-dd hh:mm:ss", plan.Columns[2].NumberFormat);
        Assert.Equal(320, plan.Columns[4].Width);
        Assert.InRange(plan.Columns[0].Width, 76, 240);
    }

    [Fact]
    public void Build_SourcesDropdownsFromLookupTables()
    {
        var dump = new List<NeonTable>
        {
            new("priority", ["priority_code"], [Row("urgent"), Row("no-rush")]),
            new("ticket", ["ticket_id", "priority_code", "status_code"],
                [Row("abc", "urgent", "open")]),
        };

        var plan = SheetPlanBuilder.Build(dump[1], dump);

        var priority = plan.Columns.Single(column => column.Name == "priority_code");
        Assert.Equal(["urgent", "no-rush"], priority.Dropdown);
        var status = plan.Columns.Single(column => column.Name == "status_code");
        Assert.Null(status.Dropdown);
    }

    [Fact]
    public void DisplayHeader_CapitalizesEveryPart()
    {
        Assert.Equal("Filed By User Id", SheetPlanBuilder.DisplayHeader("filed_by_user_id"));
        Assert.Equal("Slug", SheetPlanBuilder.DisplayHeader("slug"));
    }
}
