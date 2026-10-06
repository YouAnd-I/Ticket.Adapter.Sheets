using System.Text.Json.Nodes;

namespace Ticket.Adapter.Sheets;

internal static class Palette
{
    internal const int Header = 0x1F2937;
    internal const int Band = 0xF3F4F6;
    internal const int White = 0xFFFFFF;
    internal const int Ink = 0x111827;
    internal const int Muted = 0x6B7280;
    internal const int Accent = 0x2563EB;

    internal static JsonObject Color(int hex) => new()
    {
        ["rgbColor"] = new JsonObject
        {
            ["red"] = (hex >> 16 & 255) / 255.0,
            ["green"] = (hex >> 8 & 255) / 255.0,
            ["blue"] = (hex & 255) / 255.0,
        },
    };

    internal static readonly (string Code, int Background, int Foreground)[] Status =
    [
        ("open", 0xDBEAFE, 0x1E40AF),
        ("planned", 0xFEF3C7, 0x92400E),
        ("complete", 0xD1FAE5, 0x065F46),
        ("reopened", 0xFFEDD5, 0xC2410C),
        ("unsolved", 0xF3F4F6, 0x6B7280),
    ];

    internal static readonly (string Code, int Background, int Foreground)[] Priority =
    [
        ("urgent", 0xFEE2E2, 0xB91C1C),
        ("no-rush", 0xDBEAFE, 0x1D4ED8),
        ("report", 0xFEF3C7, 0xB45309),
    ];
}

public static class SheetsPresentation
{
    public static List<object> TableRequests(int sheetId, SheetPlan plan, SheetTabState state)
    {
        var requests = new List<object>();
        var columns = plan.Columns.Length;
        var dataRows = plan.Rows.Count;
        var sheetRows = Math.Max(state.RowCount, dataRows + 1);

        requests.Add(new
        {
            updateSheetProperties = new
            {
                properties = new
                {
                    sheetId,
                    gridProperties = new { hideGridlines = true, frozenRowCount = 1 },
                },
                fields = "gridProperties(hideGridlines,frozenRowCount)",
            },
        });

        requests.Add(new
        {
            updateDimensionProperties = new
            {
                range = new { sheetId, dimension = "ROWS", startIndex = 0, endIndex = Math.Max(dataRows, 1) },
                properties = new { pixelSize = 20 },
                fields = "pixelSize",
            },
        });

        requests.Add(new
        {
            repeatCell = new
            {
                range = Grid(sheetId, 0, 1, 0, columns),
                cell = new JsonObject
                {
                    ["userEnteredFormat"] = new JsonObject
                    {
                        ["backgroundColorStyle"] = Palette.Color(Palette.Header),
                        ["horizontalAlignment"] = "CENTER",
                        ["verticalAlignment"] = "MIDDLE",
                        ["textFormat"] = new JsonObject
                        {
                            ["bold"] = true,
                            ["fontSize"] = 10,
                            ["foregroundColorStyle"] = Palette.Color(Palette.White),
                        },
                    },
                },
                fields = "userEnteredFormat",
            },
        });

        for (var i = 0; i < columns; i++)
        {
            var column = plan.Columns[i];
            requests.Add(new
            {
                updateDimensionProperties = new
                {
                    range = new { sheetId, dimension = "COLUMNS", startIndex = i, endIndex = i + 1 },
                    properties = new { pixelSize = column.Width },
                    fields = "pixelSize",
                },
            });

            requests.Add(new
            {
                repeatCell = new
                {
                    range = Grid(sheetId, 1, sheetRows, i, i + 1),
                    cell = new JsonObject { ["userEnteredFormat"] = DataFormat(column) },
                    fields = "userEnteredFormat",
                },
            });

            if (column.Kind is ColumnKind.Boolean)
                requests.Add(Validation(sheetId, 1, sheetRows, i,
                    new { condition = new { type = "BOOLEAN" } }));

            if (column.Dropdown is { Count: > 0 } codes)
                requests.Add(Validation(sheetId, 1, sheetRows, i, new
                {
                    condition = new
                    {
                        type = "ONE_OF_LIST",
                        values = codes.Select(code => new { userEnteredValue = code }).ToArray(),
                    },
                    strict = true,
                    showCustomUi = true,
                }));
        }

        foreach (var bandedRangeId in state.BandedRangeIds)
            requests.Add(new { deleteBanding = new { bandedRangeId } });
        requests.Add(new
        {
            addBanding = new
            {
                bandedRange = new
                {
                    range = Grid(sheetId, 0, Math.Max(dataRows, 1), 0, columns),
                    rowProperties = new
                    {
                        headerColorStyle = Palette.Color(Palette.Header),
                        firstBandColorStyle = Palette.Color(Palette.White),
                        secondBandColorStyle = Palette.Color(Palette.Band),
                    },
                },
            },
        });

        requests.Add(new { setBasicFilter = new { filter = new { range = Grid(sheetId, 0, Math.Max(dataRows, 1), 0, columns) } } });

        for (var i = state.ConditionalFormats.Count - 1; i >= 0; i--)
            if (state.ConditionalFormats[i].Any(range => Intersects(range, columns, Math.Max(dataRows, 1))))
                requests.Add(new { deleteConditionalFormatRule = new { sheetId, index = i } });
        AddCodeRules(requests, sheetId, plan, Palette.Status, Palette.Priority);

        AddProtections(requests, sheetId, plan, state, sheetRows);

        return requests;
    }

    public static List<object> DashboardRequests(int sheetId, DashboardModel dashboard, SheetTabState state)
    {
        var requests = new List<object>
        {
            new
            {
                updateSheetProperties = new
                {
                    properties = new { sheetId, index = 0, gridProperties = new { hideGridlines = true } },
                    fields = "index,gridProperties.hideGridlines",
                },
            },
            new
            {
                repeatCell = new
                {
                    range = Grid(sheetId, 0, 1, 0, 1),
                    cell = new JsonObject
                    {
                        ["userEnteredFormat"] = new JsonObject
                        {
                            ["textFormat"] = new JsonObject
                            {
                                ["bold"] = true,
                                ["fontSize"] = 14,
                                ["foregroundColorStyle"] = Palette.Color(Palette.Ink),
                            },
                        },
                    },
                    fields = "userEnteredFormat",
                },
            },
            new
            {
                repeatCell = new
                {
                    range = Grid(sheetId, 1, 2, 0, 1),
                    cell = new JsonObject
                    {
                        ["userEnteredFormat"] = new JsonObject
                        {
                            ["textFormat"] = new JsonObject
                            {
                                ["italic"] = true,
                                ["fontSize"] = 9,
                                ["foregroundColorStyle"] = Palette.Color(Palette.Muted),
                            },
                        },
                    },
                    fields = "userEnteredFormat",
                },
            },
        };

        foreach (var header in dashboard.SectionHeaders)
            requests.Add(new
            {
                repeatCell = new
                {
                    range = Grid(sheetId, header.RowIndex, header.RowIndex + 1, header.StartColumn, header.EndColumn),
                    cell = new JsonObject
                    {
                        ["userEnteredFormat"] = new JsonObject
                        {
                            ["backgroundColorStyle"] = Palette.Color(Palette.Header),
                            ["verticalAlignment"] = "MIDDLE",
                            ["textFormat"] = new JsonObject
                            {
                                ["bold"] = true,
                                ["fontSize"] = 10,
                                ["foregroundColorStyle"] = Palette.Color(Palette.White),
                            },
                        },
                    },
                    fields = "userEnteredFormat",
                },
            });

        if (dashboard.Recent.Count > 0)
            requests.Add(new
            {
                repeatCell = new
                {
                    range = Grid(sheetId, dashboard.RecentHeaderRowIndex + 1,
                        dashboard.RecentHeaderRowIndex + 1 + dashboard.Recent.Count, 3, 4),
                    cell = new JsonObject
                    {
                        ["userEnteredFormat"] = new JsonObject
                        {
                            ["horizontalAlignment"] = "CENTER",
                            ["verticalAlignment"] = "MIDDLE",
                            ["numberFormat"] = new JsonObject
                            {
                                ["type"] = "DATE_TIME",
                                ["pattern"] = "yyyy-mm-dd hh:mm:ss",
                            },
                        },
                    },
                    fields = "userEnteredFormat",
                },
            });

        for (var i = state.ConditionalFormats.Count - 1; i >= 0; i--)
            requests.Add(new { deleteConditionalFormatRule = new { sheetId, index = i } });
        if (dashboard.Status.Count > 0)
            AddRuleSet(requests, sheetId,
                Grid(sheetId, dashboard.StatusRowsStart, dashboard.StatusRowsStart + dashboard.Status.Count, 0, 1),
                Palette.Status);
        if (dashboard.Recent.Count > 0)
            AddRuleSet(requests, sheetId,
                Grid(sheetId, dashboard.RecentHeaderRowIndex + 1,
                    dashboard.RecentHeaderRowIndex + 1 + dashboard.Recent.Count, 1, 2),
                Palette.Status);

        foreach (var chart in state.Charts.Where(chart => dashboard.ManagedChartTitles.Contains(chart.Title)))
            requests.Add(new { deleteEmbeddedObject = new { objectId = chart.ChartId } });

        if (dashboard.Status.Count > 0)
            requests.Add(StatusChart(sheetId, dashboard));
        if (dashboard.Priority.Count > 0)
            requests.Add(PriorityChart(sheetId, dashboard));

        return requests;
    }


    private static object StatusChart(int sheetId, DashboardModel dashboard)
    {
        var first = dashboard.StatusRowsStart;
        var last = first + dashboard.Status.Count;
        return new
        {
            addChart = new
            {
                chart = new
                {
                    spec = new
                    {
                        title = DashboardModel.StatusChartTitle,
                        basicChart = new
                        {
                            chartType = "COLUMN",
                            legendPosition = "NO_LEGEND",
                            domains = new[]
                            {
                                new { domain = Source(sheetId, first, last, 0, 1) },
                            },
                            series = new[]
                            {
                                new
                                {
                                    series = Source(sheetId, first, last, 1, 2),
                                    targetAxis = "LEFT_AXIS",
                                    colorStyle = Palette.Color(Palette.Accent),
                                },
                            },
                            headerCount = 1,
                        },
                    },
                    position = new
                    {
                        overlayPosition = new
                        {
                            anchorCell = new { sheetId, rowIndex = 3, columnIndex = 7 },
                        },
                    },
                },
            },
        };
    }

    private static object PriorityChart(int sheetId, DashboardModel dashboard)
    {
        var first = dashboard.PriorityRowsStart;
        var last = first + dashboard.Priority.Count;
        return new
        {
            addChart = new
            {
                chart = new
                {
                    spec = new
                    {
                        title = DashboardModel.PriorityChartTitle,
                        pieChart = new
                        {
                            legendPosition = "RIGHT_LEGEND",
                            domain = Source(sheetId, first, last, 3, 4),
                            series = Source(sheetId, first, last, 4, 5),
                            pieHole = 0.55,
                        },
                    },
                    position = new
                    {
                        overlayPosition = new
                        {
                            anchorCell = new { sheetId, rowIndex = 20, columnIndex = 7 },
                        },
                    },
                },
            },
        };
    }

    private static object Source(int sheetId, int firstRow, int lastRow, int firstColumn, int lastColumn) => new
    {
        sourceRange = new { sources = new[] { Grid(sheetId, firstRow, lastRow, firstColumn, lastColumn) } },
    };

    private static JsonObject DataFormat(ColumnPlan column)
    {
        var format = new JsonObject { ["verticalAlignment"] = "MIDDLE" };
        if (column.Wrap)
            format["wrapStrategy"] = "WRAP";
        if (column.NumberFormat is { } pattern)
            format["numberFormat"] = new JsonObject
            {
                ["type"] = column.Kind is ColumnKind.DateTime ? "DATE_TIME" : "TEXT",
                ["pattern"] = pattern,
            };
        if (column.Kind is ColumnKind.DateTime)
            format["horizontalAlignment"] = "CENTER";
        return format;
    }

    private static object Validation(int sheetId, int firstRow, int lastRow, int column, object rule) => new
    {
        setDataValidation = new
        {
            range = Grid(sheetId, firstRow, lastRow, column, column + 1),
            rule,
        },
    };

    private static void AddCodeRules(
        List<object> requests, int sheetId, SheetPlan plan,
        (string Code, int Background, int Foreground)[] status,
        (string Code, int Background, int Foreground)[] priority)
    {
        for (var i = 0; i < plan.Columns.Length; i++)
        {
            var column = plan.Columns[i];
            var rules = column.Name switch
            {
                "status_code" => status,
                "priority_code" => priority,
                _ => [],
            };
            if (rules.Length == 0 || plan.Rows.Count == 0) continue;
            AddRuleSet(requests, sheetId, Grid(sheetId, 1, plan.Rows.Count + 1, i, i + 1), rules);
        }
    }

    private static void AddRuleSet(
        List<object> requests, int sheetId, object range,
        (string Code, int Background, int Foreground)[] rules)
    {
        foreach (var (code, background, foreground) in rules)
            requests.Add(new
            {
                addConditionalFormatRule = new
                {
                    rule = new
                    {
                        ranges = new[] { range },
                        booleanRule = new
                        {
                            condition = new
                            {
                                type = "TEXT_EQ",
                                values = new[] { new { userEnteredValue = code } },
                            },
                            format = new
                            {
                                backgroundColorStyle = Palette.Color(background),
                                textFormat = new { foregroundColorStyle = Palette.Color(foreground) },
                            },
                        },
                    },
                    index = 0,
                },
            });
    }

    private static void AddProtections(
        List<object> requests, int sheetId, SheetPlan plan, SheetTabState state, int sheetRows)
    {
        for (var i = 0; i < plan.Columns.Length; i++)
        {
            var column = plan.Columns[i];
            if (column.Kind is not ColumnKind.Identifier) continue;
            var description = $"neon sync {plan.Tab}.{column.Name}";
            var existing = state.ProtectedRanges.FirstOrDefault(
                protectedRange => protectedRange.Description == description);
            if (existing is { } current &&
                current.Range.StartColumnIndex == i && current.Range.EndColumnIndex == i + 1)
                continue;
            if (existing is { } stale)
                requests.Add(new
                {
                    updateProtectedRange = new
                    {
                        protectedRange = new
                        {
                            protectedRangeId = stale.Id,
                            range = Grid(sheetId, 1, sheetRows, i, i + 1),
                        },
                        fields = "range",
                    },
                });
            else
                requests.Add(new
                {
                    addProtectedRange = new
                    {
                        protectedRange = new
                        {
                            range = Grid(sheetId, 1, sheetRows, i, i + 1),
                            description,
                            warningOnly = true,
                        },
                    },
                });
        }
    }

    private static object Grid(int sheetId, int firstRow, int lastRow, int firstColumn, int lastColumn) => new
    {
        sheetId,
        startRowIndex = firstRow,
        endRowIndex = lastRow,
        startColumnIndex = firstColumn,
        endColumnIndex = lastColumn,
    };

    private static bool Intersects(SheetGridRange range, int columns, int rows) =>
        (range.StartColumnIndex ?? 0) < columns &&
        (range.EndColumnIndex ?? int.MaxValue) > 0 &&
        (range.StartRowIndex ?? 0) < rows &&
        (range.EndRowIndex ?? int.MaxValue) > 0;
}
