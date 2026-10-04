using System;
using System.Collections.Generic;
using System.Text.Json;
using TerrariaAgent.Bridge;

internal static class Program
{
    private static readonly List<object> Results = new List<object>();
    private static int Main()
    {
        try
        {
            Check("twenty_pixel_flat_body_both_directions", () =>
            {
                Expect(new WalkingBodyRect(160, 118, 20, 42), 1, Flat(), 160, 160);
                Expect(new WalkingBodyRect(192, 118, 20, 42), -1, Flat(), 160, 160);
            });
            Check("slope_one_uses_own_left_and_min_across_two_cells", () =>
                Expect(new WalkingBodyRect(168, 126, 20, 42), 1, SlopeOne(), 168, 176));
            Check("slope_one_descends_right_and_ascends_left", () =>
            {
                Expect(new WalkingBodyRect(160, 118, 20, 42), 1, SlopeOne(), 160, 176);
                Expect(new WalkingBodyRect(192, 134, 20, 42), -1, SlopeOne(), 160, 176);
            });
            Check("slope_two_uses_own_right_on_a_twenty_pixel_footprint", () =>
                Expect(new WalkingBodyRect(160, 130, 20, 42), 1, SlopeTwo(), 160, 172));
            Check("slope_two_ascends_right_and_descends_left", () =>
            {
                Expect(new WalkingBodyRect(160, 130, 20, 42), 1, SlopeTwo(), 160, 172);
                Expect(new WalkingBodyRect(192, 118, 20, 42), -1, SlopeTwo(), 160, 172);
            });
            Check("exact_tile_edge_requires_body_width_not_point_support", () =>
            {
                var onlyOwnLeft = new[] { Cell(10, 10) };
                Refuse(new WalkingBodyRect(160, 118, 20, 42), 1, onlyOwnLeft, "unknown_swept_columns", 1);
                var twoColumns = new[] { Cell(10, 10), Cell(11, 10) };
                Expect(new WalkingBodyRect(160, 118, 16, 42), 1, twoColumns, 160, 160, 16);
            });
            Check("missing_interior_column_never_interpolates_ground", () =>
                Refuse(Body(), 1, new[] { Cell(10,10), Cell(11,10), Cell(13,10) }, "unknown_swept_columns"));
            Check("duplicate_column_different_rows_is_not_first_floor_selection", () =>
                Refuse(Body(), 1, new[] { Cell(10,10), Cell(10,11), Cell(11,10), Cell(12,10), Cell(13,10) }, "duplicate_floor_column"));
            Check("duplicate_identical_column_is_rejected", () =>
                Refuse(Body(), 1, new[] { Cell(10,10), Cell(10,10), Cell(11,10), Cell(12,10), Cell(13,10) }, "duplicate_floor_column"));
            Check("first_floor_proof_is_required", () =>
                Refuse(Body(), 1, new[] { new WalkingFloorCell(10,10,0,false), Cell(11,10), Cell(12,10), Cell(13,10) }, "first_floor_not_proven"));
            foreach (int shape in new[] { 3, 4, -1, 5 })
            {
                int rejected = shape;
                Check("unsupported_or_half_encoded_shape_" + shape, () =>
                    Refuse(Body(), 1, new[] { Cell(10,10,rejected), Cell(11,10), Cell(12,10), Cell(13,10) }, "unsupported_floor_shape"));
            }
            Check("liquid_and_half_block_are_caller_proof_obligations", () =>
            {
                // These facts are intentionally not representable in this
                // helper. A caller that knows liquid/half must not mark full
                // first-floor proof true; it supplies no usable proof here.
                Refuse(Body(), 1, new[] { new WalkingFloorCell(10,10,0,false), Cell(11,10), Cell(12,10), Cell(13,10) }, "first_floor_not_proven");
            });
            Check("feet_mismatch_is_not_assumed_to_settle", () =>
                Refuse(new WalkingBodyRect(160,120,20,42), 1, Flat(), "own_feet_do_not_match_start_surface"));
            Check("one_pixel_contact_tolerance_has_a_fixed_boundary", () =>
            {
                Expect(new WalkingBodyRect(160,119,20,42), 1, Flat(), 160,160);
                Refuse(new WalkingBodyRect(160,119.01,20,42), 1, Flat(), "own_feet_do_not_match_start_surface");
            });
            Check("thirty_two_pixel_drop_is_rejected", () =>
                Refuse(Body(), 1, new[] { Cell(10,10), Cell(11,10), Cell(12,12), Cell(13,12) }, "floor_exceeds_16px_height_budget"));
            Check("thirty_two_pixel_rise_is_rejected", () =>
                Refuse(Body(), 1, new[] { Cell(10,10), Cell(11,10), Cell(12,8), Cell(13,8) }, "floor_exceeds_16px_height_budget"));
            Check("tiny_boundary_crossing_cannot_erase_a_full_height_drop", () =>
                Refuse(new WalkingBodyRect(176-0.000000002,118,20,42), 1,
                    new[] { Cell(10,10), Cell(11,12), Cell(12,12) }, "floor_exceeds_16px_height_budget", 0.000000003));
            Check("thirty_two_pixel_jump_is_rejected_even_inside_total_height_bounds", () =>
                Refuse(Body(), 1, new[] { Cell(10,10), Cell(11,11), Cell(12,11), Cell(13,9) }, "floor_jump_exceeds_16px"));
            Check("up_then_down_profile_is_rejected", () =>
                Refuse(new WalkingBodyRect(160,114,20,42), 1, new[] { Cell(10,10), Cell(11,9,2), Cell(12,10), Cell(13,10) }, "floor_profile_reverses_direction"));
            Check("down_then_up_profile_is_rejected", () =>
                Refuse(Body(), 1, new[] { Cell(10,10,1), Cell(11,11), Cell(12,10), Cell(13,10) }, "floor_profile_reverses_direction"));
            Check("interior_contact_intersection_cannot_hide_reversal", () =>
            {
                var opposing = new[] { Cell(10,10,1), Cell(11,10,2) };
                Refuse(Body(), 1, opposing, "floor_profile_reverses_direction", 8);
                Refuse(new WalkingBodyRect(168,122,20,42), -1, opposing, "floor_profile_reverses_direction", 8);
            });
            Check("slope_one_floor_contact_is_not_solid_interior", () =>
            {
                Overlap(new WalkingBodyRect(168,126,20,42), Cell(10,10,1), false);
                Overlap(new WalkingBodyRect(168,126.001,20,42), Cell(10,10,1), true);
            });
            Check("slope_two_floor_contact_is_not_solid_interior", () =>
            {
                Overlap(new WalkingBodyRect(160,130,20,42), Cell(11,10,2), false);
                Overlap(new WalkingBodyRect(160,130.001,20,42), Cell(11,10,2), true);
            });
            Check("empty_triangle_region_is_not_a_whole_air_tile", () =>
            {
                Overlap(new WalkingBodyRect(168,160,4,4), Cell(10,10,1), false);
                Overlap(new WalkingBodyRect(160,160,4,4), Cell(10,10,2), false);
                Overlap(new WalkingBodyRect(160,172,4,4), Cell(10,10,1), true);
                Overlap(new WalkingBodyRect(172,172,4,4), Cell(10,10,2), true);
            });
            Check("tile_edge_touch_is_not_interior_but_tiny_clip_is", () =>
            {
                Overlap(new WalkingBodyRect(176,160,4,4), Cell(10,10), false);
                Overlap(new WalkingBodyRect(175.999,160,4,4), Cell(10,10), true);
                Overlap(new WalkingBodyRect(160,150,4,10), Cell(10,10), false);
                Overlap(new WalkingBodyRect(160,150,4,10.001), Cell(10,10), true);
            });
            Check("unsupported_shape_overlap_is_unknown_not_air", () =>
            {
                bool overlaps; string reason;
                Require(!WalkingSlopeGeometry.TrySolidInteriorOverlap(Body(), Cell(10,10,3), out overlaps, out reason) && !overlaps && reason == "unsupported_floor_shape");
            });
            Check("invalid_numbers_and_empty_bodies_fail_closed", () =>
            {
                foreach (var body in new[] { new WalkingBodyRect(double.NaN,118,20,42), new WalkingBodyRect(160,double.PositiveInfinity,20,42), new WalkingBodyRect(160,118,0,42), new WalkingBodyRect(160,118,20,-1) })
                    Refuse(body, 1, Flat(), "invalid_body_direction_or_lookahead");
            });
            Check("direction_and_lookahead_are_bounded", () =>
            {
                Refuse(Body(), 0, Flat(), "invalid_body_direction_or_lookahead");
                Refuse(Body(), 2, Flat(), "invalid_body_direction_or_lookahead");
                Refuse(Body(), 1, Flat(), "invalid_body_direction_or_lookahead", 32.001);
                Refuse(Body(), 1, Flat(), "invalid_body_direction_or_lookahead", 0);
            });
            Check("proof_count_is_bounded", () =>
            {
                Refuse(Body(), 1, new WalkingFloorCell[0], "invalid_first_floor_count");
                var nine = new WalkingFloorCell[9]; for (int i=0;i<nine.Length;i++) nine[i]=Cell(10+i,10);
                Refuse(Body(), 1, nine, "invalid_first_floor_count");
            });
            Check("floor_cells_and_results_do_not_grant_visibility_or_input", () =>
            {
                // Success only supplies floors. This assembly links no game,
                // lease, protocol, network, visibility reader or action API.
                var cells = Flat(); Expect(Body(),1,cells,160,160);
                Require(cells[0].TileX == 10 && cells[0].TileY == 10 && cells[0].Shape == 0 && cells[0].IsFirstFloor);
            });
            Console.WriteLine(JsonSerializer.Serialize(new { Scope="pure_math_support_and_triangle_interior_only_no_game_no_movement_permission", Passed=Results.Count, Results=Results }, new JsonSerializerOptions { WriteIndented=true }));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
    private static WalkingBodyRect Body() { return new WalkingBodyRect(160,118,20,42); }
    private static WalkingFloorCell Cell(int x,int y,int shape=0) { return new WalkingFloorCell(x,y,shape,true); }
    private static WalkingFloorCell[] Flat() { return new[] { Cell(10,10),Cell(11,10),Cell(12,10),Cell(13,10),Cell(14,10) }; }
    private static WalkingFloorCell[] SlopeOne() { return new[] { Cell(10,10,1),Cell(11,11),Cell(12,11),Cell(13,11),Cell(14,11) }; }
    private static WalkingFloorCell[] SlopeTwo() { return new[] { Cell(10,11),Cell(11,10,2),Cell(12,10),Cell(13,10),Cell(14,10) }; }
    private static void Expect(WalkingBodyRect body,int direction,WalkingFloorCell[] cells,double min,double max,double distance=32)
    {
        WalkingSlopeResult result; bool ok=WalkingSlopeGeometry.TryEvaluate(body,direction,cells,out result,distance);
        Require(ok, "expected_support:"+result.Reason); Require(Math.Abs(result.MinFloor-min)<0.000001 && Math.Abs(result.MaxFloor-max)<0.000001,"wrong_surface_bounds");
    }
    private static void Refuse(WalkingBodyRect body,int direction,WalkingFloorCell[] cells,string reason,double distance=32)
    {
        WalkingSlopeResult result; Require(!WalkingSlopeGeometry.TryEvaluate(body,direction,cells,out result,distance),"unsupported_profile_was_accepted");
        Require(result.Reason==reason,"wrong_rejection:"+result.Reason+" expected "+reason);
    }
    private static void Overlap(WalkingBodyRect body,WalkingFloorCell cell,bool expected)
    { bool overlaps; string reason; Require(WalkingSlopeGeometry.TrySolidInteriorOverlap(body,cell,out overlaps,out reason) && overlaps==expected,"wrong_triangle_or_box_overlap"); }
    private static void Check(string name,Action test) { test(); Results.Add(new { Check=name,Status="pass_synthetic_offline" }); }
    private static void Require(bool value,string reason="walking_slope_boundary_failed") { if(!value)throw new InvalidOperationException(reason); }
}
