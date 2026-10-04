using System;
using System.Collections.Generic;

namespace TerrariaAgent.Bridge
{
    public struct WalkingBodyRect
    {
        public readonly double Left, Top, Width, Height;
        public WalkingBodyRect(double left, double top, double width, double height)
        { Left = left; Top = top; Width = width; Height = height; }
        public double Right { get { return Left + Width; } }
        public double Feet { get { return Top + Height; } }
    }

    public sealed class WalkingFloorCell
    {
        public readonly int TileX, TileY, Shape;
        public readonly bool IsFirstFloor;
        // IsFirstFloor is caller proof from this capture, not a fact inferred
        // from coordinates. Only full (0), floor slope 1 and floor slope 2 are
        // supported. Half blocks, liquids and other slopes must not be encoded
        // as full cells; the caller must reject them before constructing proof.
        public WalkingFloorCell(int tileX, int tileY, int shape, bool isFirstFloor)
        { TileX = tileX; TileY = tileY; Shape = shape; IsFirstFloor = isFirstFloor; }
    }

    public sealed class WalkingSlopeResult
    {
        public readonly string Reason;
        public readonly double MinFloor, MaxFloor;
        internal WalkingSlopeResult(string reason, double minFloor, double maxFloor)
        { Reason = reason; MinFloor = minFloor; MaxFloor = maxFloor; }
    }

    // Pure support geometry only: never reads a Tile, game object, visibility,
    // head/body air, history or control state. Success is NOT a movement permit.
    // A caller must separately prove the complete swept body/head envelope and
    // normal gameplay state. These formulas match the checked 1.4.5.8 floor
    // slope contact expressions, not the entire vanilla collision algorithm.
    public static class WalkingSlopeGeometry
    {
        public const int MaxFirstFloorCells = 8;
        public const double MaxLookahead = 32;
        private const double TileSize = 16;
        private const double Epsilon = 0.00001;
        private const double Tolerance = 0.000001;

        public static bool TryEvaluate(WalkingBodyRect body, int direction,
            IList<WalkingFloorCell> firstFloors, out WalkingSlopeResult result,
            double lookahead = MaxLookahead)
        {
            double unavailable = double.NaN;
            if (!ValidBody(body) || (direction != -1 && direction != 1) ||
                !Finite(lookahead) || lookahead <= 0 || lookahead > MaxLookahead)
                return Reject("invalid_body_direction_or_lookahead", unavailable, unavailable, out result);
            if (firstFloors == null || firstFloors.Count == 0 || firstFloors.Count > MaxFirstFloorCells)
                return Reject("invalid_first_floor_count", unavailable, unavailable, out result);

            var cells = new WalkingFloorCell[firstFloors.Count];
            for (int i = 0; i < cells.Length; ++i)
            {
                WalkingFloorCell cell = firstFloors[i];
                if (cell == null || cell.TileX < 0 || cell.TileY < 0 || cell.TileX > 32767 || cell.TileY > 32767)
                    return Reject("invalid_floor_cell", unavailable, unavailable, out result);
                if (!cell.IsFirstFloor)
                    return Reject("first_floor_not_proven", unavailable, unavailable, out result);
                if (cell.Shape != 0 && cell.Shape != 1 && cell.Shape != 2)
                    return Reject("unsupported_floor_shape", unavailable, unavailable, out result);
                for (int j = 0; j < i; ++j)
                    if (cells[j].TileX == cell.TileX)
                        return Reject("duplicate_floor_column", unavailable, unavailable, out result);
                cells[i] = cell;
            }

            double destination = body.Left + direction * lookahead;
            double minLeft = Math.Min(body.Left, destination), maxLeft = Math.Max(body.Left, destination);
            if (!Finite(destination) || !Finite(maxLeft + body.Width) || minLeft < 0 ||
                maxLeft + body.Width > 32768 * TileSize)
                return Reject("unknown_swept_columns", unavailable, unavailable, out result);
            int firstColumn = (int)Math.Floor(minLeft / TileSize);
            int lastColumn = (int)Math.Ceiling((maxLeft + body.Width) / TileSize) - 1;
            if (lastColumn - firstColumn + 1 > MaxFirstFloorCells)
                return Reject("unknown_swept_columns", unavailable, unavailable, out result);
            for (int column = firstColumn; column <= lastColumn; ++column)
                if (!HasColumn(cells, column))
                    return Reject("unknown_swept_columns", unavailable, unavailable, out result);

            double startFloor = FloorAt(cells, body.Left, body.Width);
            if (!Finite(startFloor) || Math.Abs(startFloor - body.Feet) > 1 + Tolerance)
                return Reject("own_feet_do_not_match_start_surface", startFloor, startFloor, out result);

            var critical = new List<double> { minLeft, maxLeft };
            foreach (WalkingFloorCell cell in cells)
            {
                double left = cell.TileX * TileSize, right = left + TileSize;
                AddInside(critical, left, minLeft, maxLeft);
                AddInside(critical, right, minLeft, maxLeft);
                AddInside(critical, left - body.Width, minLeft, maxLeft);
                AddInside(critical, right - body.Width, minLeft, maxLeft);
            }
            SortDistinct(critical);
            var samples = new List<double>(critical);
            foreach (double point in critical)
            {
                AddInside(samples, point - Epsilon, minLeft, maxLeft);
                AddInside(samples, point + Epsilon, minLeft, maxLeft);
            }
            // Between edge crossings each contact is affine. Add all interior
            // intersections too: sampling endpoints alone can miss a peak in
            // the minimum of one rising and one falling contact surface.
            for (int i = 1; i < critical.Count; ++i)
            {
                double a = critical[i - 1], b = critical[i];
                var functions = FunctionsAt(cells, (a + b) / 2, body.Width);
                for (int j = 0; j < functions.Count; ++j)
                    for (int k = j + 1; k < functions.Count; ++k)
                    {
                        double slope = functions[j].Slope - functions[k].Slope;
                        if (slope == 0) continue;
                        double crossing = (functions[k].Intercept - functions[j].Intercept) / slope;
                        if (crossing > a && crossing < b)
                        {
                            samples.Add(crossing);
                            AddInside(samples, crossing - Epsilon, a, b);
                            AddInside(samples, crossing + Epsilon, a, b);
                        }
                    }
            }
            SortDistinct(samples);
            if (direction < 0) samples.Reverse();
            double minFloor = startFloor, maxFloor = startFloor, previousFloor = startFloor;
            int profileDirection = 0;
            foreach (double left in samples)
            {
                double floor = FloorAt(cells, left, body.Width);
                if (!Finite(floor)) return Reject("unknown_body_footprint", minFloor, maxFloor, out result);
                minFloor = Math.Min(minFloor, floor); maxFloor = Math.Max(maxFloor, floor);
                if (Math.Abs(floor - body.Feet) > TileSize + Tolerance)
                    return Reject("floor_exceeds_16px_height_budget", minFloor, maxFloor, out result);
                double delta = floor - previousFloor;
                if (Math.Abs(delta) > TileSize + Tolerance)
                    return Reject("floor_jump_exceeds_16px", minFloor, maxFloor, out result);
                int change = delta > Tolerance ? 1 : delta < -Tolerance ? -1 : 0;
                if (change != 0 && profileDirection != 0 && change != profileDirection)
                    return Reject("floor_profile_reverses_direction", minFloor, maxFloor, out result);
                if (change != 0) profileDirection = change;
                previousFloor = floor;
            }
            result = new WalkingSlopeResult("supported_floor_profile", minFloor, maxFloor);
            return true;
        }

        // Tests positive-area interior overlap with an axis-aligned body box.
        // Merely touching the floor or a tile edge is contact, not body clipping.
        // For local u/v in [0,16], the solid half is v>=u for shape 1 and
        // v>=16-u for shape 2. Reject unsupported shape instead of treating air.
        public static bool TrySolidInteriorOverlap(WalkingBodyRect body, WalkingFloorCell cell,
            out bool overlaps, out string reason)
        {
            overlaps = false; reason = "invalid_body_or_cell";
            if (!ValidBody(body) || cell == null || cell.TileX < 0 || cell.TileY < 0 ||
                cell.TileX > 32767 || cell.TileY > 32767) return false;
            if (cell.Shape != 0 && cell.Shape != 1 && cell.Shape != 2)
            { reason = "unsupported_floor_shape"; return false; }
            double tileLeft = cell.TileX * TileSize, tileTop = cell.TileY * TileSize;
            double minU = Math.Max(body.Left, tileLeft) - tileLeft;
            double maxU = Math.Min(body.Right, tileLeft + TileSize) - tileLeft;
            double minV = Math.Max(body.Top, tileTop) - tileTop;
            double maxV = Math.Min(body.Feet, tileTop + TileSize) - tileTop;
            reason = "solid_interior_checked";
            if (maxU <= minU || maxV <= minV) return true;
            overlaps = cell.Shape == 0 ||
                (cell.Shape == 1 ? maxV > minU : maxV > TileSize - maxU);
            return true;
        }

        private static bool ValidBody(WalkingBodyRect body)
        { return Finite(body.Left) && Finite(body.Top) && Finite(body.Width) && Finite(body.Height) &&
            body.Width > 0 && body.Height > 0 && Finite(body.Right) && Finite(body.Feet) &&
            body.Right > body.Left && body.Feet > body.Top; }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static bool Reject(string reason, double minFloor, double maxFloor, out WalkingSlopeResult result)
        { result = new WalkingSlopeResult(reason, minFloor, maxFloor); return false; }
        private static bool HasColumn(WalkingFloorCell[] cells, int column)
        { foreach (WalkingFloorCell cell in cells) if (cell.TileX == column) return true; return false; }
        private static double FloorAt(WalkingFloorCell[] cells, double left, double width)
        {
            double floor = double.PositiveInfinity, right = left + width;
            foreach (WalkingFloorCell cell in cells)
            {
                double tileLeft = cell.TileX * TileSize, tileRight = tileLeft + TileSize;
                if (left >= tileRight || right <= tileLeft) continue;
                double surface = cell.TileY * TileSize;
                if (cell.Shape == 1) surface += Clamp(left - tileLeft);
                if (cell.Shape == 2) surface += Clamp(tileRight - right);
                floor = Math.Min(floor, surface);
            }
            return floor;
        }
        private static double Clamp(double value) { return Math.Max(0, Math.Min(TileSize, value)); }
        private struct FloorFunction
        {
            internal double Slope, Intercept;
            internal FloorFunction(double slope, double intercept) { Slope = slope; Intercept = intercept; }
        }
        private static List<FloorFunction> FunctionsAt(WalkingFloorCell[] cells, double left, double width)
        {
            var functions = new List<FloorFunction>();
            foreach (WalkingFloorCell cell in cells)
            {
                double tileLeft = cell.TileX * TileSize, tileRight = tileLeft + TileSize, top = cell.TileY * TileSize;
                if (left >= tileRight || left + width <= tileLeft) continue;
                double offset = cell.Shape == 1 ? left - tileLeft : tileRight - (left + width);
                if (cell.Shape == 0 || offset <= 0) functions.Add(new FloorFunction(0, top));
                else if (offset >= TileSize) functions.Add(new FloorFunction(0, top + TileSize));
                else if (cell.Shape == 1) functions.Add(new FloorFunction(1, top - tileLeft));
                else functions.Add(new FloorFunction(-1, top + tileRight - width));
            }
            return functions;
        }
        private static void AddInside(List<double> values, double value, double min, double max)
        { if (value >= min && value <= max) values.Add(value); }
        private static void SortDistinct(List<double> values)
        {
            values.Sort();
            for (int i = values.Count - 1; i > 0; --i)
                // Never merge different positions just because they are close:
                // a subpixel crossing may still leave a full-height floor edge.
                if (values[i] == values[i - 1]) values.RemoveAt(i);
        }
    }
}
