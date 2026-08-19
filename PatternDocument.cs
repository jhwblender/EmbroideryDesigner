using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace EmbroideryDesigner;

/// <summary>Plain-data DTO for a single thread, used only for JSON save/load.</summary>
public sealed class ThreadLineDto
{
    public int I1 { get; set; }
    public int J1 { get; set; }
    public int I2 { get; set; }
    public int J2 { get; set; }
    public string Color { get; set; } = "#000000";
}

/// <summary>Plain-data DTO for the whole project file.</summary>
public sealed class PatternDocumentDto
{
    public int FormatVersion { get; set; } = 1;
    public int Cols { get; set; }
    public int Rows { get; set; }
    public double Spacing { get; set; }
    public string BackgroundColor { get; set; } = "#000000";
    public double HoleRadius { get; set; } = 6.0;
    public string HoleOutlineColor { get; set; } = "#404040";
    public double HoleOutlineThickness { get; set; } = 1.0;
    public double ThreadThickness { get; set; } = 3.0;
    public List<string> Palette { get; set; } = new();
    public List<ThreadLineDto> Lines { get; set; } = new();
}

public static class PatternIo
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ColorToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color HexToColor(string hex)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex)!;
            return c;
        }
        catch
        {
            return Colors.Black;
        }
    }

    public static void Save(string path, int cols, int rows, double spacing, Color backgroundColor,
        double holeRadius, Color holeOutlineColor, double holeOutlineThickness, double threadThickness,
        IEnumerable<Color> palette, IEnumerable<ThreadLine> lines)
    {
        var dto = new PatternDocumentDto
        {
            Cols = cols,
            Rows = rows,
            Spacing = spacing,
            BackgroundColor = ColorToHex(backgroundColor),
            HoleRadius = holeRadius,
            HoleOutlineColor = ColorToHex(holeOutlineColor),
            HoleOutlineThickness = holeOutlineThickness,
            ThreadThickness = threadThickness,
            Palette = palette.Select(ColorToHex).ToList(),
            Lines = lines.Select(l => new ThreadLineDto
            {
                I1 = l.A.I,
                J1 = l.A.J,
                I2 = l.B.I,
                J2 = l.B.J,
                Color = ColorToHex(l.Color)
            }).ToList()
        };

        string json = JsonSerializer.Serialize(dto, JsonOptions);
        File.WriteAllText(path, json);
    }

    public static PatternDocumentDto Load(string path)
    {
        string json = File.ReadAllText(path);
        var dto = JsonSerializer.Deserialize<PatternDocumentDto>(json, JsonOptions);
        return dto ?? new PatternDocumentDto();
    }
}
