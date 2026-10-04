using System;

namespace TerrariaAgent.Protocol
{
    // Pure geometry only. The game-thread caller owns the predicate which must
    // guard viewport/lighting BEFORE accessing any world tile. A false predicate
    // stops traversal immediately; there is no read-ahead or world data here.
    public static class VisibleRay
    {
        public const int MaxCellSteps = 256;

        public static bool IsClear(float eyeX, float eyeY, float targetX, float targetY,
            Func<int, int, bool, bool> guardedCell)
        {
            if (guardedCell == null || !Finite(eyeX) || !Finite(eyeY) ||
                !Finite(targetX) || !Finite(targetY)) return false;
            int x, y, endX, endY;
            if (!TryCell(eyeX, out x) || !TryCell(eyeY, out y) ||
                !TryCell(targetX, out endX) || !TryCell(targetY, out endY)) return false;
            long requiredSteps = Math.Abs((long)endX - x) + Math.Abs((long)endY - y) + 3;
            if (requiredSteps > MaxCellSteps) return false;
            int budget = (int)requiredSteps;
            double dx = (double)targetX - eyeX, dy = (double)targetY - eyeY;
            int stepX = Math.Sign(dx), stepY = Math.Sign(dy);
            double deltaX = stepX == 0 ? double.PositiveInfinity : 16.0 / Math.Abs(dx);
            double deltaY = stepY == 0 ? double.PositiveInfinity : 16.0 / Math.Abs(dy);
            double maxX = stepX == 0 ? double.PositiveInfinity :
                ((stepX > 0 ? (x + 1) * 16.0 : x * 16.0) - eyeX) / dx;
            double maxY = stepY == 0 ? double.PositiveInfinity :
                ((stepY > 0 ? (y + 1) * 16.0 : y * 16.0) - eyeY) / dy;
            while (budget-- > 0)
            {
                if (!guardedCell(x, y, x == endX && y == endY)) return false;
                if (x == endX && y == endY) return true;
                if (stepX != 0 && stepY != 0 && Math.Abs(maxX - maxY) <= 0.000000000001)
                {
                    // Both side cells touching a corner must pass. This prevents
                    // diagonal peeking through an opaque or unobserved seam.
                    if (!guardedCell(x + stepX, y, false) || !guardedCell(x, y + stepY, false)) return false;
                    x += stepX; y += stepY; maxX += deltaX; maxY += deltaY;
                }
                else if (maxX < maxY) { x += stepX; maxX += deltaX; }
                else { y += stepY; maxY += deltaY; }
            }
            return false;
        }

        private static bool Finite(float value)
        { return !float.IsNaN(value) && !float.IsInfinity(value); }

        private static bool TryCell(float coordinate, out int cell)
        {
            double value = Math.Floor((double)coordinate / 16.0);
            cell = 0;
            // Leave room for the supercover side cells at a corner. Validate in
            // double precision before integer conversion, including huge floats.
            if (value < (double)int.MinValue + 1 || value > (double)int.MaxValue - 1) return false;
            cell = (int)value;
            return true;
        }
    }
}
