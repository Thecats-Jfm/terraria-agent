using System;
using System.Collections.Generic;
using TerrariaAgent.Protocol;

internal static class Program
{
    private static int _passed;
    private static int _checks;

    private static int Main()
    {
        try
        {
            Check("clear_horizontal", ClearHorizontal);
            Check("blocked_tiles_do_not_read_behind", BlockedRay);
            Check("dark_tiles_reject_before_tile_read", DarkRay);
            Check("viewport_rejects_before_tile_read", ViewportRay);
            Check("unknown_tiles_stop_read_ahead", UnknownRay);
            Check("both_diagonal_corner_sides_block_peeking", CornerBlockers);
            Check("unlit_diagonal_corner_rejects", DarkCorner);
            Check("same_cell_has_single_endpoint", SameCell);
            Check("visible_solid_endpoint_but_no_peek_beyond", SolidEndpoint);
            Check("all_eight_ray_directions", EightDirections);
            Check("tile_boundary_negative_direction", NegativeBoundary);
            Check("ray_budget_rejects_before_predicate", Budget);
            Check("invalid_floats_are_bounded_rejection", InvalidFloats);
            Check("null_predicate_is_rejected", NullPredicate);
            Check("gameplay_copy_is_bounded_and_independent", BoundedCopy);
            Check("maximum_gameplay_reply_fits_frame", FrameBudget);
            Console.WriteLine("{\"scope\":\"pure_geometry_and_DTO_only_no_game_loaded\",\"passed\":" + _passed +
                ",\"checks\":" + _checks + ",\"status\":\"passed\"}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL " + error.Message);
            return 1;
        }
    }

    private static void Check(string name, Action action)
    { _checks++; action(); _passed++; Console.WriteLine("PASS " + name); }

    private static void Require(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }

    private static bool Ray(Grid grid, int endX = 8, int endY = 1)
    { return VisibleRay.IsClear(24f, 24f, endX * 16f + 8f, endY * 16f + 8f, grid.GuardedCell); }

    private static void ClearHorizontal()
    {
        var grid = new Grid();
        Require(Ray(grid), "clear ray rejected");
        Require(grid.Calls.Count == 8 && grid.EndpointCalls == 1 && grid.Reads.Count == 7, "wrong clear traversal");
        Require(grid.Calls[7] == "8,1", "endpoint not reached");
    }

    private static void BlockedRay()
    {
        var grid = new Grid(); grid.Solid.Add("3,1");
        Require(!Ray(grid), "opaque ray accepted");
        Require(grid.Reads.Contains("3,1") && !grid.Reads.Contains("4,1") &&
            !grid.Calls.Contains("8,1") && grid.Reads.Count == 3, "read behind opaque blocker");
    }

    private static void DarkRay()
    {
        var grid = new Grid(); grid.Dark.Add("3,1");
        Require(!Ray(grid), "dark ray accepted");
        Require(!grid.Reads.Contains("3,1") && !grid.Calls.Contains("4,1") && grid.Reads.Count == 2,
            "dark tile or tile beyond darkness was read");
    }

    private static void ViewportRay()
    {
        var grid = new Grid(); grid.MaxX = 3;
        Require(!Ray(grid), "out-of-viewport ray accepted");
        Require(grid.Calls.Contains("4,1") && !grid.Reads.Contains("4,1") && !grid.Calls.Contains("5,1"),
            "world tile read outside viewport or after failed viewport guard");
    }

    private static void UnknownRay()
    {
        var grid = new Grid(); grid.Unknown.Add("3,1");
        Require(!Ray(grid), "unknown ray accepted");
        Require(grid.Reads.Count == 3 && !grid.Calls.Contains("4,1"), "read beyond unknown tile");
    }

    private static void CornerBlockers()
    {
        foreach (string side in new[] { "2,1", "1,2" })
        {
            var grid = new Grid(); grid.Solid.Add(side);
            Require(!Ray(grid, 4, 4), "diagonal corner blocker bypassed: " + side);
            Require(grid.Reads.Contains(side) && !grid.Calls.Contains("2,2") && !grid.Calls.Contains("4,4"),
                "read behind diagonal corner blocker: " + side);
        }
    }

    private static void DarkCorner()
    {
        var grid = new Grid(); grid.Dark.Add("1,2");
        Require(!Ray(grid, 4, 4), "dark corner bypassed");
        Require(!grid.Reads.Contains("1,2") && !grid.Calls.Contains("2,2"), "dark corner read or peeked through");
    }

    private static void SameCell()
    {
        var grid = new Grid();
        Require(VisibleRay.IsClear(24, 24, 25, 25, grid.GuardedCell), "same-cell ray rejected");
        Require(grid.Calls.Count == 1 && grid.EndpointCalls == 1 && grid.Reads.Count == 0, "same-cell traversal unbounded");
    }

    private static void SolidEndpoint()
    {
        var endpoint = new Grid(); endpoint.Solid.Add("5,1");
        Require(Ray(endpoint, 5), "first visible facing solid endpoint rejected");
        Require(endpoint.EndpointCalls == 1 && !endpoint.Reads.Contains("5,1"), "endpoint must be checked by caller after visibility");
        var behind = new Grid(); behind.Solid.Add("5,1");
        Require(!Ray(behind, 8) && !behind.Calls.Contains("6,1"), "solid endpoint allowed seeing a farther target");
    }

    private static void EightDirections()
    {
        foreach (int directionX in new[] { -1, 0, 1 })
            foreach (int directionY in new[] { -1, 0, 1 })
            {
                if (directionX == 0 && directionY == 0) continue;
                var grid = new Grid();
                Require(VisibleRay.IsClear(88, 88, (5 + directionX * 3) * 16 + 8,
                    (5 + directionY * 3) * 16 + 8, grid.GuardedCell), "clear direction rejected");
                Require(grid.EndpointCalls == 1 && grid.Calls.Count <= 3 * VisibleRay.MaxCellSteps, "unbounded directional traversal");
            }
    }

    private static void NegativeBoundary()
    {
        var grid = new Grid();
        Require(VisibleRay.IsClear(64, 40, 24, 40, grid.GuardedCell), "negative boundary ray rejected");
        Require(grid.Calls[0] == "4,2" && grid.Calls[1] == "3,2" && grid.EndpointCalls == 1,
            "negative boundary skipped adjacent cell");
    }

    private static void Budget()
    {
        int calls = 0;
        Func<int, int, bool, bool> allow = (x, y, endpoint) => { calls++; return true; };
        Require(!VisibleRay.IsClear(24, 24, 16008, 24, allow) && calls == 0, "long ray called world predicate");
        Require(VisibleRay.IsClear(24, 24, (1 + 253) * 16 + 8, 24, allow) && calls <= VisibleRay.MaxCellSteps,
            "permitted bounded ray rejected or exceeded budget");
        calls = 0;
        Require(!VisibleRay.IsClear(24, 24, (1 + 254) * 16 + 8, 24, allow) && calls == 0,
            "one-over-budget ray called predicate");
    }

    private static void InvalidFloats()
    {
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.MaxValue, -float.MaxValue })
            for (int coordinate = 0; coordinate < 4; coordinate++)
            {
                float[] values = { 24, 24, 40, 40 }; values[coordinate] = invalid;
                int calls = 0;
                bool clear = VisibleRay.IsClear(values[0], values[1], values[2], values[3],
                    (x, y, endpoint) => { calls++; return true; });
                Require(!clear && calls == 0, "invalid coordinate invoked tile predicate");
            }
    }

    private static void NullPredicate()
    { Require(!VisibleRay.IsClear(24, 24, 40, 40, null), "null predicate accepted"); }

    private static VisibleTarget[] Targets(int count)
    {
        var targets = new VisibleTarget[count];
        for (int i = 0; i < count; i++) targets[i] = new VisibleTarget { TileX = int.MaxValue - i, TileY = int.MaxValue - i };
        return targets;
    }

    private static void BoundedCopy()
    {
        var gameplay = new GameplayObservation { TreeTargets = Targets(8), PlacementTargets = Targets(8),
            WorkBenchTargets = Targets(8), GoneTreeTargets = Targets(8) };
        GameplayObservation copy = gameplay.Copy();
        Require(copy.TreeTargets.Length == 4 && copy.PlacementTargets.Length == 4 &&
            copy.WorkBenchTargets.Length == 4 && copy.GoneTreeTargets.Length == 4, "candidate copy exceeds limit");
        gameplay.TreeTargets[0].TileX = 1; gameplay.GoneTreeTargets[0].TileY = 1;
        Require(copy.TreeTargets[0].TileX == int.MaxValue && copy.GoneTreeTargets[0].TileY == int.MaxValue,
            "cross-thread DTO copy aliases producer arrays");
    }

    private static void FrameBudget()
    {
        var gameplay = new GameplayObservation { Wood = int.MaxValue, WorkBenches = int.MaxValue,
            AxeSlot = 9, WorkBenchSlot = 9, SelectedSlot = 49, HasFreeSlot = true, CanCraftWorkBench = true,
            TreeTargets = Targets(4), PlacementTargets = Targets(4), WorkBenchTargets = Targets(4), GoneTreeTargets = Targets(4) };
        var observation = new OwnObservation { Sequence = long.MaxValue, WorldId = new string('a', 32),
            X = float.MaxValue, Y = float.MaxValue, VelocityX = float.MaxValue, VelocityY = float.MaxValue,
            Health = int.MaxValue, MaxHealth = int.MaxValue, ControlState = "LatchedStop", Reason = "initial_operator_arm_unavailable",
            Inputs = new InputState(), LeaseInputs = new InputState(), GameTick = long.MaxValue, MonotonicMs = long.MaxValue,
            CanArm = true, CanOperatorArm = true, Gameplay = gameplay };
        byte[] frame = JsonCodec.Serialize(new AgentReply { Type = "operator_arm", Status = "rejected",
            Reason = "initial_operator_arm_unavailable", SessionId = new string('b', 32), WorldId = new string('a', 32),
            Sequence = long.MaxValue, Observation = observation });
        Require(frame.Length < ProtocolLimits.MaxFrameBytes, "bounded gameplay reply exceeds frame");
        Console.WriteLine("Gameplay reply bytes=" + frame.Length);
    }

    private sealed class Grid
    {
        internal int MaxX = 10;
        internal readonly HashSet<string> Solid = new HashSet<string>();
        internal readonly HashSet<string> Dark = new HashSet<string>();
        internal readonly HashSet<string> Unknown = new HashSet<string>();
        internal readonly List<string> Calls = new List<string>();
        internal readonly List<string> Reads = new List<string>();
        internal int EndpointCalls;

        internal bool GuardedCell(int x, int y, bool endpoint)
        {
            string key = x + "," + y; Calls.Add(key);
            // Same ordering as the real bridge predicate: reject viewport/light
            // before any synthetic tile read, and never inspect beyond a blocker.
            if (x < 0 || y < 0 || x > MaxX || y > 10 || Dark.Contains(key)) return false;
            if (endpoint) { EndpointCalls++; return true; }
            Reads.Add(key);
            return !Unknown.Contains(key) && !Solid.Contains(key);
        }
    }
}
