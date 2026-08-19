using System;
using System.Windows;
using System.Windows.Media;

namespace EmbroideryDesigner;

/// <summary>
/// One hole in the 45°-rotated lattice, identified by a screen-rectangular
/// (column, row) index -- I is the column within its row, J is the row.
/// Odd rows are offset half a cell to the right, which is what makes the
/// lattice look 45°-rotated while the overall (Cols x Rows) extent is a
/// rectangle from the viewer's perspective, not a diamond.
/// </summary>
public readonly record struct HoleIndex(int I, int J)
{
    public override string ToString() => $"({I},{J})";
}

/// <summary>
/// A single stitched thread connecting two holes, in a given color.
/// Endpoints are stored in a canonical order (A before B) so that a line
/// A->B and B->A are recognized as the same thread for add/remove/toggle.
/// </summary>
public sealed class ThreadLine
{
    public HoleIndex A { get; }
    public HoleIndex B { get; }
    public Color Color { get; set; }

    public ThreadLine(HoleIndex a, HoleIndex b, Color color)
    {
        // Canonicalize ordering so (A,B) and (B,A) compare equal.
        if (Compare(a, b) <= 0) { A = a; B = b; }
        else { A = b; B = a; }
        Color = color;
    }

    private static int Compare(HoleIndex x, HoleIndex y)
    {
        if (x.I != y.I) return x.I.CompareTo(y.I);
        return x.J.CompareTo(y.J);
    }

    public static (HoleIndex, HoleIndex) Key(HoleIndex a, HoleIndex b)
        => Compare(a, b) <= 0 ? (a, b) : (b, a);

    public (HoleIndex, HoleIndex) Key() => (A, B);
}

/// <summary>
/// Converts between (column, row) hole indices and on-screen / fabric
/// coordinates for a rectangular field of holes arranged on a 45°-rotated
/// square lattice. Every other row is shifted right by half a cell, which
/// is the standard way that lattice tiles a rectangle: each hole's 4
/// nearest neighbors (its true "45°" diagonal neighbors on the fabric) sit
/// one row up/down and either the same or adjacent column, at a distance
/// of exactly <see cref="Spacing"/>. Cols/Rows therefore describe the
/// rectangle you actually see, not a diamond-shaped index range.
/// </summary>
public sealed class Lattice
{
    public int Cols { get; }   // holes per row
    public int Rows { get; }   // number of rows
    public double Spacing { get; } // nominal hole spacing controlling grid density (pixels at zoom = 1)

    private readonly double _dx; // horizontal distance between consecutive holes in the same row
    private readonly double _dy; // vertical distance between rows

    public double Dx => _dx; // expose for selection-size display
    public double Dy => _dy;

    public Lattice(int cols, int rows, double spacing)
    {
        Cols = Math.Max(1, cols);
        Rows = Math.Max(1, rows);
        Spacing = Math.Max(1, spacing);

        double c = Math.Cos(Math.PI / 4); // cos(45deg)
        _dx = 2 * Spacing * c;
        _dy = Spacing * c; // half of _dx; use ~2× rows vs cols for a visually square grid
    }

    public bool InBounds(HoleIndex h) => h.I >= 0 && h.I < Cols && h.J >= 0 && h.J < Rows;

    public Point Position(HoleIndex h) => Position(h.I, h.J);

    public Point Position(int col, int row)
    {
        double rowOffset = (row % 2 != 0) ? _dx / 2.0 : 0.0;
        return new Point(col * _dx + rowOffset, row * _dy);
    }

    /// <summary>Bounding box (in un-zoomed local coordinates) that contains every hole.</summary>
    public Rect ComputeBounds()
    {
        double maxRowOffset = Rows >= 2 ? _dx / 2.0 : 0.0; // some row is odd once there are 2+ rows
        double minX = 0, maxX = (Cols - 1) * _dx + maxRowOffset;
        double minY = 0, maxY = (Rows - 1) * _dy;
        return new Rect(new Point(minX, minY), new Point(maxX, maxY));
    }

    /// <summary>Finds the nearest hole to a local-space point, if within snapRadius.</summary>
    public bool TryFindNearestHole(Point local, double snapRadius, out HoleIndex hole)
    {
        int row0 = (int)Math.Round(local.Y / _dy);

        double best = double.MaxValue;
        hole = default;
        bool found = false;
        for (int row = row0 - 1; row <= row0 + 1; row++)
        {
            if (row < 0 || row >= Rows) continue;
            double rowOffset = (row % 2 != 0) ? _dx / 2.0 : 0.0;
            int col0 = (int)Math.Round((local.X - rowOffset) / _dx);
            for (int col = col0 - 1; col <= col0 + 1; col++)
            {
                if (col < 0 || col >= Cols) continue;
                var p = Position(col, row);
                double dist = (p - local).Length;
                if (dist < best)
                {
                    best = dist;
                    hole = new HoleIndex(col, row);
                    found = true;
                }
            }
        }
        if (found && best <= snapRadius) return true;
        hole = default;
        return false;
    }
}
