using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using TerrariaAgent.Controller;
using TerrariaAgent.Protocol;

internal static class Program
{
    // Synthetic controller boundaries only. No game, save or world is loaded.
    private static int Main()
    {
        var results = new List<object>();
        Failure("stone_gone_without_inventory_gain_cannot_succeed", "stone_without_gain", "stone-test",
            failure => failure.Results.All(task => task.Skill != "mine_stone" || task.Status != "success"),
            client => client.GoneSamples >= 4 && client.UseActions > 0 && client.UseAfterMissing == 0, results);
        Failure("advancing_sequence_without_game_tick_cannot_unlock_skills", "tick_frozen", "craft-test",
            failure => failure.Results.All(task => task.Status != "success") &&
                (failure.Message.Contains("observation_stalled") || failure.Message.Contains("observation_not_advancing")),
            client => client.CraftRequests == 0 && client.UseActions == 0, results);
        Failure("craft_ack_delta_cannot_replace_frozen_observation_or_trigger_retry", "craft_ack_only", "craft-test",
            failure => failure.Message.Contains("observation_stalled") &&
                failure.Results.All(task => task.Skill != "craft_wooden_bow" || task.Status != "success"),
            client => client.CraftRequests == 1 && client.InventedAcks == 1, results);
        Failure("options_open_prevents_craft_request", "unsafe_craft", "craft-test",
            failure => failure.Message.Contains("unsafe_world_ui_death_or_pause"),
            client => client.CraftRequests == 0 && client.UseActions == 0, results);
        Failure("soil_gone_without_own_dirt_gain_cannot_succeed", "dirt_without_gain", "dig-test",
            failure => failure.Results.All(task => task.Skill != "dig_soil" || task.Status != "success"),
            client => client.SoilChangedSamples >= 4 && client.UseActions == 1 && client.UseAfterMissing == 0, results);
        Failure("grass_to_dirt_and_other_pickup_without_gone_cannot_succeed", "grass_changed", "dig-test",
            failure => failure.Results.All(task => task.Skill != "dig_soil" || task.Status != "success"),
            client => client.SoilChangedSamples >= 4 && client.UseActions > 0, results);
        Failure("twenty_health_lost_stops_dig_without_another_pick_action", "dirt_damage", "dig-test",
            failure => failure.Message.Contains("dig_stopped_after_health_loss_20"),
            client => client.UseActions == 1, results);
        var dug = new FakeClient("dirt_success");
        StageCTaskResult dig = Run(dug, "dig-test").Single(task => task.Skill == "dig_soil");
        Require(dig.Status == "success" && dig.DirtBefore == 0 && dig.DirtAfter == 1 && dig.ProductAfter == 1 &&
            dig.Reason.Contains("stone_not_verified") && dug.SoilChangedSamples >= 2 && dug.UseAfterMissing == 0,
            "normal_dig_requires_fresh_retained_world_and_own_inventory_delta");
        results.Add(new { Check = "normal_dig_requires_fresh_retained_world_and_own_inventory_delta", Status = "pass_synthetic_offline",
            dig.DirtBefore, dig.DirtAfter, dig.ProductAfter, dug.SoilChangedSamples, dug.UseActions, dug.UseAfterMissing });

        var crafted = new FakeClient("craft_once");
        List<StageCTaskResult> craftTasks = Run(crafted, "craft-test");
        Require(crafted.CraftRequests == 1 && crafted.RetainedProductSamples >= 2 &&
            craftTasks.Single(task => task.Skill == "craft_wooden_bow").Status == "success",
            "normal_craft_once_with_fresh_retained_delta");
        results.Add(new { Check = "normal_craft_once_with_fresh_retained_delta", Status = "pass_synthetic_offline",
            crafted.CraftRequests, crafted.RetainedProductSamples });

        var late = new FakeClient("craft_late_gain");
        List<StageCTaskResult> lateTasks = Run(late, "craft-test");
        StageCTaskResult lateCraft = lateTasks.Single(task => task.Skill == "craft_wooden_bow");
        Require(late.CraftRequests == 1 && late.RetainedProductSamples >= 2 && lateCraft.Status == "success" &&
            lateCraft.WoodBefore == 20 && lateCraft.WoodAfter == 17 && lateCraft.ProductAfter == 1,
            "exact_craft_then_late_material_gain_is_retained");
        results.Add(new { Check = "exact_craft_then_late_material_gain_is_retained", Status = "pass_synthetic_offline",
            lateCraft.WoodBefore, lateCraft.WoodAfter, lateCraft.ProductAfter, late.CraftRequests });
        Failure("net_gain_without_first_exact_craft_proof_cannot_succeed", "craft_masked_gain", "craft-test",
            failure => failure.Message.Contains("craft_timeout_no_exact_delta") &&
                failure.Results.All(task => task.Skill != "craft_wooden_bow" || task.Status != "success"),
            client => client.CraftRequests == 1, results);

        var hidden = new FakeClient("enemy_hidden");
        List<StageCTaskResult> combatTasks = Run(hidden, "combat-trial");
        StageCTaskResult encounter = combatTasks.Single(task => task.Skill == "combat_trial");
        Require(hidden.UseActions > 0 && hidden.UseAfterMissing == 0 && !encounter.KillVerified &&
            encounter.Reason.Contains("encounter_not_currently_visible;kill_unverified") &&
            combatTasks.All(task => !task.KillVerified), "missing_enemy_is_not_a_kill_and_stops_attack");
        results.Add(new { Check = "missing_enemy_is_not_a_kill_and_stops_attack", Status = "pass_synthetic_offline",
            EncounterStatus = encounter.Status, encounter.Reason, encounter.KillVerified, hidden.UseActions, hidden.UseAfterMissing });

        Failure("enemy_loss_with_same_sample_twenty_damage_cannot_succeed", "enemy_hidden_damage", "combat-trial",
            failure => failure.Message.Contains("encounter_lost_after_damage") &&
                failure.Results.All(task => task.Skill != "combat_trial" || task.Status != "success"),
            client => client.UseActions == 1 && client.DamageSamples > 0 && client.UseAfterMissing == 0 &&
                client.MovementAfterBoundary == 0, results);
        Failure("enemy_loss_without_actual_weapon_use_cannot_succeed", "enemy_hidden_no_use", "combat-trial",
            failure => failure.Message.Contains("encounter_lost_without_observed_actual_weapon_use") &&
                failure.Results.All(task => task.Skill != "combat_trial" || task.Status != "success"),
            client => client.WeaponSelections == 1 && client.UseActions == 0 && client.MovementActions == 1 &&
                client.MovementAfterBoundary == 0, results);
        Failure("visible_enemy_on_unknown_ground_cannot_authorize_combat_movement", "combat_unknown_ground", "combat-trial",
            failure => failure.Message.Contains("movement_ground_not_legally_verified") &&
                failure.Results.All(task => task.Skill != "combat_trial" || task.Status != "success"),
            client => client.MovementActions == 0 && client.UseActions == 0 && client.WeaponSelections == 0 &&
                client.CraftRequests == 0, results);
        Failure("combat_ground_permission_allows_only_the_proven_step_then_stops", "combat_ground_withdrawn", "combat-trial",
            failure => failure.Message.Contains("movement_ground_not_legally_verified") &&
                failure.Results.All(task => task.Skill != "combat_trial" || task.Status != "success"),
            client => client.MovementActions == 1 && client.MovementAfterBoundary == 0 &&
                client.UnsafeGroundSamples >= 1 && client.CraftRequests == 0, results);
        Failure("stationary_soil_pickup_displacement_stops_without_movement_or_tools", "pickup_overshoot", "collect-soil",
            failure => failure.Message.Contains("soil_pickup_step_exceeded_local_safety_bound") &&
                failure.Results.All(task => task.Skill != "collect_soil" || task.Status != "success"),
            client => client.MovementActions == 0 && client.MovementAfterBoundary == 0 &&
                client.UseActions == 0 && client.CraftRequests == 0 && client.OvershootSamples > 0, results);
        foreach (string scenario in new[] { "pickup_gain_with_x_overshoot", "pickup_gain_with_fall" })
            Failure("same_sample_dirt_gain_cannot_bypass_" + scenario, scenario, "collect-soil",
                failure => failure.Message.Contains("soil_pickup_step_exceeded_local_safety_bound") &&
                    failure.Results.All(task => task.Skill != "collect_soil" || task.Status != "success"),
                client => client.MovementActions == 0 && client.UseActions == 0 && client.CraftRequests == 0 &&
                    client.OvershootSamples > 0 && client.PickupGainSamples > 0, results);
        Failure("standing_without_health_gain_cannot_claim_recovery", "health_frozen", "recover-health",
            failure => failure.Message.Contains("normal_health_recovery_timeout") &&
                failure.Results.All(task => task.Skill != "recover_health" || task.Status != "success"),
            client => client.RecoverySelections >= 2 && client.ThresholdSamples == 0 && client.UseActions == 0 &&
                client.MovementActions == 0 && client.CraftRequests == 0, results);
        Failure("one_threshold_sample_followed_by_damage_cannot_claim_recovery", "health_threshold_lost", "recover-health",
            failure => failure.Message.Contains("normal_health_recovery_timeout") &&
                failure.Results.All(task => task.Skill != "recover_health" || task.Status != "success"),
            client => client.ThresholdSamples == 1 && client.PostThresholdLowSamples >= 2 &&
                client.UseActions == 0 && client.MovementActions == 0 && client.CraftRequests == 0, results);
        Failure("retained_sixty_health_cannot_override_same_sample_twenty_damage", "health_confirmation_damage", "recover-health",
            failure => failure.Message.Contains("recovery_stopped_after_health_loss_20") &&
                failure.Results.All(task => task.Skill != "recover_health" || task.Status != "success"),
            client => client.DamageSamples == 1 && client.LastHealth == 60 && client.UseActions == 0 &&
                client.MovementActions == 0 && client.CraftRequests == 0, results);

        var eighty = new FakeClient("health_target_eighty");
        StageCTaskResult recovered = Run(eighty, "recover-health", 80).Single(task => task.Skill == "recover_health");
        Require(recovered.Status == "success" && recovered.HealthBefore == 60 && recovered.HealthAfter == 80 &&
            recovered.HealthTarget == 80 && eighty.RecoverySelections >= 2 && eighty.BelowRequestedTargetSamples >= 1 &&
            eighty.ThresholdSamples >= 2 && recovered.EndSequence > recovered.StartSequence &&
            eighty.UseActions == 0 && eighty.MovementActions == 0 && eighty.CraftRequests == 0,
            "sixty_does_not_satisfy_eighty_and_final_fresh_eighty_is_retained");
        results.Add(new { Check = "sixty_does_not_satisfy_eighty_and_final_fresh_eighty_is_retained",
            Status = "pass_synthetic_offline", recovered.HealthBefore, recovered.HealthAfter, recovered.HealthTarget,
            recovered.StartSequence, recovered.EndSequence, eighty.RecoverySelections,
            eighty.BelowRequestedTargetSamples, eighty.ThresholdSamples });

        var clamped = new FakeClient("health_target_clamped");
        StageCTaskResult capped = Run(clamped, "recover-health", 100).Single(task => task.Skill == "recover_health");
        Require(capped.Status == "success" && capped.HealthTarget == 80 && capped.HealthAfter == 80 &&
            clamped.ThresholdSamples >= 2 && clamped.MovementActions == 0 && clamped.UseActions == 0,
            "requested_health_target_capped_at_own_max_health");
        results.Add(new { Check = "requested_health_target_capped_at_own_max_health", Status = "pass_synthetic_offline",
            RequestedTarget = 100, capped.HealthTarget, capped.HealthAfter, clamped.ThresholdSamples });

        foreach (int invalidTarget in new[] { 59, 101 })
        {
            var invalid = new FakeClient("health_target_eighty");
            ArgumentException caught = null;
            try { Run(invalid, "recover-health", invalidTarget); }
            catch (ArgumentException error) { caught = error; }
            Require(caught != null && caught.Message.Contains("health_target_must_be_60_to_100") &&
                invalid.ObserveCalls == 0 && invalid.ActCalls == 0,
                "out_of_range_health_target_rejected_before_any_observation_or_action");
            results.Add(new { Check = "out_of_range_health_target_" + invalidTarget + "_rejected_before_any_action",
                Status = "pass_synthetic_offline", Reason = caught.Message, invalid.ObserveCalls, invalid.ActCalls });
        }

        Failure("unknown_ground_cannot_start_stone_search_movement", "seek_unknown_ground", "seek-stone",
            failure => failure.Message.Contains("no_proven_visible_ground_for_next_step") &&
                failure.Results.All(task => task.Skill != "seek_stone" || task.Status != "success"),
            client => client.MovementActions == 0 && client.UseActions == 0 && client.CraftRequests == 0, results);
        Failure("withdrawn_ground_permission_stops_before_another_step", "seek_permission_withdrawn", "seek-stone",
            failure => failure.Message.Contains("no_proven_visible_ground_for_next_step") &&
                failure.Results.All(task => task.Skill != "seek_stone" || task.Status != "success"),
            client => client.MovementActions == 1 && client.MovementAfterBoundary == 0 &&
                client.UseActions == 0 && client.CraftRequests == 0 && client.UnsafeGroundSamples >= 2, results);
        var discovery = new FakeClient("seek_visible_stone");
        StageCTaskResult found = Run(discovery, "seek-stone").Single(task => task.Skill == "seek_stone");
        Require(found.Status == "success" && found.Reason.Contains("mining_and_pickup_not_verified") &&
            found.TileX == 3 && found.TileY == 2 && found.StoneBefore == 2 && found.StoneAfter == 2 &&
            found.DirtBefore == 3 && found.DirtAfter == 3 && discovery.FoundStoneSamples >= 2 &&
            discovery.MovementActions == 1 && discovery.UseActions == 0 && discovery.CraftRequests == 0,
            "fresh_retained_visible_stone_is_only_discovery_not_inventory_progress");
        results.Add(new { Check = "fresh_retained_visible_stone_is_only_discovery_not_inventory_progress",
            Status = "pass_synthetic_offline", found.Reason, found.StoneBefore, found.StoneAfter,
            found.DirtBefore, found.DirtAfter, discovery.FoundStoneSamples, discovery.MovementActions,
            discovery.UseActions, discovery.CraftRequests });
        foreach (string scenario in new[] { "seek_damage_with_stone", "seek_fall_with_stone" })
            Failure("same_sample_stone_discovery_cannot_override_" + scenario, scenario, "seek-stone",
                failure => failure.Message.Contains(scenario == "seek_damage_with_stone" ?
                    "exploration_stopped_after_damage" : "exploration_stopped_after_downward_motion") &&
                    failure.Results.All(task => task.Skill != "seek_stone" || task.Status != "success"),
                client => client.MovementActions == 1 && client.MovementAfterBoundary == 0 &&
                    client.FoundStoneSamples == 1 && client.UseActions == 0 && client.CraftRequests == 0, results);

        var approached = new FakeClient("soil_approach_success");
        StageCTaskResult approachedDig = Run(approached, "dig-test").Single(task => task.Skill == "dig_soil");
        Require(approachedDig.Status == "success" && approachedDig.DirtBefore == 0 && approachedDig.DirtAfter == 1 &&
            approached.MovementActions == 1 && approached.UseActions == 1 && approached.UseAfterMissing == 0,
            "visible_side_soil_72px_approached_then_normally_dug_and_retained");
        results.Add(new { Check = "visible_side_soil_72px_approached_then_normally_dug_and_retained",
            Status = "pass_synthetic_offline", approachedDig.DirtBefore, approachedDig.DirtAfter,
            approached.MovementActions, approached.UseActions, approached.UseAfterMissing });
        foreach (string scenario in new[] { "soil_approach_unknown_ground", "soil_approach_beyond128" })
            Failure("no_approach_or_tools_for_" + scenario, scenario, "dig-test",
                failure => failure.Message.Contains(scenario == "soil_approach_unknown_ground" ?
                    "movement_ground_not_legally_verified" : "no_visible_reachable_soil_target"),
                client => client.MovementActions == 0 && client.UseActions == 0 && client.CraftRequests == 0, results);
        foreach (string scenario in new[] { "soil_approach_visibility_lost", "soil_approach_damage", "soil_approach_enemy" })
            Failure("one_proven_step_then_stop_before_mining_" + scenario, scenario, "dig-test",
                failure => failure.Message.Contains(scenario == "soil_approach_visibility_lost" ? "approach_target_no_longer_visible" :
                    scenario == "soil_approach_damage" ? "dig_stopped_after_health_loss_20" : "soil_stopped_for_nearby_visible_enemy"),
                client => client.MovementActions == 1 && client.UseActions == 0 && client.MovementAfterBoundary == 0, results);
        foreach (string scenario in new[] { "soil_pickup_success", "soil_pickup_ground_withdrawn" })
        {
            var pickup = new FakeClient(scenario);
            StageCTaskResult collected = Run(pickup, "collect-soil").Single(task => task.Skill == "collect_soil");
            Require(collected.Status == "success" && collected.DirtAfter == 1 && collected.Reason.Contains("no_new_mining_claimed") &&
                pickup.MovementActions == 1 && pickup.MovementAfterBoundary == 0 && pickup.UseActions == 0 &&
                pickup.CraftRequests == 0, "bounded_proven_pickup_" + scenario);
            results.Add(new { Check = "bounded_proven_pickup_" + scenario, Status = "pass_synthetic_offline",
                collected.DirtBefore, collected.DirtAfter, collected.Reason, pickup.MovementActions, pickup.MovementAfterBoundary });
        }
        foreach (string scenario in new[] { "soil_pickup_visibility_lost", "soil_pickup_damage", "soil_pickup_enemy", "soil_pickup_gain_overshoot" })
            Failure("pickup_sample_cannot_bypass_" + scenario, scenario, "collect-soil",
                failure => failure.Message.Contains(scenario == "soil_pickup_visibility_lost" ? "soil_pickup_location_no_longer_visible" :
                    scenario == "soil_pickup_damage" ? "dig_stopped_after_health_loss_20" :
                    scenario == "soil_pickup_enemy" ? "soil_stopped_for_nearby_visible_enemy" : "soil_pickup_step_exceeded_local_safety_bound") &&
                    failure.Results.All(task => task.Skill != "collect_soil" || task.Status != "success"),
                client => client.MovementActions == 1 && client.MovementAfterBoundary == 0 && client.UseActions == 0, results);
        var settled = new FakeClient("seek_neutral_three_recoveries");
        StageCTaskResult settledResult = Run(settled, "seek-stone").Single(task => task.Skill == "seek_stone");
        Require(settledResult.Status == "success" && settled.MovementActions == 4 && settled.LeftActions == 0 &&
            settled.UseActions == 0 && settled.FoundStoneSamples >= 2,
            "neutral_fresh_same_direction_proof_is_rechecked_after_three_transitions");
        results.Add(new { Check = "neutral_fresh_same_direction_proof_is_rechecked_after_three_transitions",
            Status = "pass_synthetic_offline", settled.MovementActions, settled.LeftActions, settled.FoundStoneSamples });
        Failure("safe_ack_cannot_replace_still_unknown_actual_ground", "seek_ack_safe_only", "seek-stone",
            failure => failure.Message.Contains("no_proven_visible_ground_for_next_step"),
            client => client.MovementActions == 0 && client.UseActions == 0 && client.InventedAcks > 0, results);
        Failure("right_safe_at_end_does_not_grant_a_second_direction_change", "seek_one_reverse_budget", "seek-stone",
            failure => failure.Message.Contains("no_proven_visible_ground_for_next_step"),
            client => client.MovementActions == 2 && client.LeftActions == 1 && client.RightActions == 1 && client.UseActions == 0, results);
        foreach (string scenario in new[] { "soil_coast_reach", "soil_coast_safe_recovered" })
        {
            var coast = new FakeClient(scenario);
            StageCTaskResult dugAfterCoast = Run(coast, "dig-test").Single(task => task.Skill == "dig_soil");
            Require(dugAfterCoast.Status == "success" && dugAfterCoast.DirtAfter == 1 && coast.MovementActions == 1 &&
                coast.NeutralApproachSamples >= 1 && coast.FreshReachSamples >= 2 && coast.PickBeforeFreshReach == 0 &&
                coast.UseActions == 1 && coast.MovementAfterBoundary == 0,
                "soil_neutral_recovery_" + scenario);
            results.Add(new { Check = "soil_neutral_recovery_" + scenario, Status = "pass_synthetic_offline",
                coast.MovementActions, coast.NeutralApproachSamples, coast.FreshReachSamples, coast.PickBeforeFreshReach,
                coast.UseActions, dugAfterCoast.DirtAfter });
        }
        foreach (string scenario in new[] { "soil_coast_never", "soil_coast_ack_only" })
            Failure("unknown_ground_neutral_does_not_authorize_" + scenario, scenario, "dig-test",
                failure => failure.Message.Contains("movement_ground_not_legally_verified"),
                client => client.MovementActions == 0 && client.UseActions == 0 && client.NeutralApproachSamples >= 3, results);
        foreach (string scenario in new[] { "soil_coast_damage", "soil_coast_enemy", "soil_coast_visibility_lost" })
            Failure("first_neutral_sample_stops_for_" + scenario, scenario, "dig-test",
                failure => failure.Message.Contains(scenario == "soil_coast_damage" ? "dig_stopped_after_health_loss_20" :
                    scenario == "soil_coast_enemy" ? "soil_stopped_for_nearby_visible_enemy" : "approach_target_no_longer_visible"),
                client => client.MovementActions == 1 && client.MovementAfterBoundary == 0 && client.UseActions == 0 &&
                    client.NeutralApproachSamples == 1, results);
        Failure("soil_approach_neutral_processing_delay_cannot_escape_total_ten_seconds", "soil_coast_deadline", "dig-test",
            failure => failure.Message.Contains("dig_total_budget_10s"),
            client => client.DeadlineInjected && client.MovementActions == 0 && client.UseActions == 0 &&
                client.NeutralApproachSamples == 1, results);
        Failure("soil_approach_three_distinct_gaps_cannot_reset_two_recovery_budget", "soil_coast_budget_exhausted", "dig-test",
            failure => failure.Message.Contains("movement_ground_not_legally_verified"),
            client => client.MovementActions == 3 && client.UseActions == 0 && client.SettlingStarts == 2, results);
        var torch = new FakeClient("torch_hold_reveals_target");
        StageCTaskResult torchPlacement = Run(torch, "torch-test").Single(task => task.Skill == "place_torch");
        Require(torchPlacement.Status == "success" && torchPlacement.ProductBefore == 2 && torchPlacement.ProductAfter == 1 &&
            torch.TorchHeldSamples >= 3 && torch.TorchSelections == 3 && torch.UseBeforeTorchHeld == 0 &&
            torch.UseActions == 1 && torch.PlacedTorchSamples >= 2 && torch.MovementActions == 0 && torch.CraftRequests == 0,
            "normal_held_torch_fresh_visibility_precedes_verified_placement");
        results.Add(new { Check = "normal_held_torch_fresh_visibility_precedes_verified_placement", Status = "pass_synthetic_offline",
            torch.TorchSelections, torch.TorchHeldSamples, torch.UseBeforeTorchHeld, torch.PlacedTorchSamples,
            torchPlacement.ProductBefore, torchPlacement.ProductAfter });
        Failure("torch_selection_ack_cannot_replace_frozen_actual_observation", "torch_hold_frozen", "torch-test",
            failure => failure.Message.Contains("observation_stalled"),
            client => client.TorchSelections > 0 && client.InventedAcks > 0 && client.TorchHeldSamples == 0 &&
                client.UseActions == 0 && client.MovementActions == 0 && client.CraftRequests == 0, results);
        Failure("torch_outside_hotbar_cannot_select_or_use", "torch_bad_slot", "torch-test",
            failure => failure.Message.Contains("torch_not_in_hotbar_or_missing"),
            client => client.TorchSelections == 0 && client.UseActions == 0 && client.MovementActions == 0 &&
                client.CraftRequests == 0, results);
        Failure("holding_torch_without_new_visible_target_cannot_assume_light", "torch_hold_no_target", "torch-test",
            failure => failure.Message.Contains("no_visible_reachable_placement_target"),
            client => client.TorchSelections == 3 && client.TorchHeldSamples >= 3 && client.UseActions == 0 &&
                client.MovementActions == 0 && client.CraftRequests == 0, results);
        Failure("torch_hold_damage_prevents_placement_before_target_selection", "torch_hold_damage", "torch-test",
            failure => failure.Message.Contains("placement_stopped_after_health_loss_20"),
            client => client.TorchSelections == 1 && client.DamageSamples == 1 && client.UseActions == 0 &&
                client.MovementActions == 0 && client.CraftRequests == 0, results);
        Failure("torch_hold_ui_transition_prevents_placement", "torch_hold_ui", "torch-test",
            failure => failure.Message.Contains("unsafe_world_ui_death_or_pause"),
            client => client.TorchSelections == 1 && client.UseActions == 0 && client.MovementActions == 0 &&
                client.CraftRequests == 0, results);
        foreach (string scenario in new[] { "forage_soil_then_stone", "forage_torch_path" })
        {
            var forage = new FakeClient(scenario);
            List<StageCTaskResult> tasks = Run(forage, "forage-stone");
            StageCTaskResult gathered = tasks.Single(task => task.Skill == "forage_stone");
            Require(gathered.Status == "success" && gathered.StoneBefore == 0 && gathered.StoneAfter == 1 &&
                tasks.Single(task => task.Skill == "dig_soil").Status == "success" &&
                tasks.Single(task => task.Skill == "mine_stone").Status == "success" &&
                forage.ForageSoilPicks == 1 && forage.ForageStonePicks == 1 && forage.CraftRequests == 0 &&
                forage.StoneRetainedSamples >= 3 && (scenario != "forage_torch_path" ||
                (forage.TorchHeldSamples >= 6 && forage.UseBeforeTorchHeld == 0)),
                "forage_normal_soil_then_actual_retained_stone_" + scenario);
            results.Add(new { Check = "forage_normal_soil_then_actual_retained_stone_" + scenario,
                Status = "pass_synthetic_offline", forage.ForageSoilPicks, forage.ForageStonePicks,
                forage.StoneRetainedSamples, forage.TorchHeldSamples, gathered.StoneBefore, gathered.StoneAfter });
        }
        Failure("forage_three_soils_cannot_become_stone_or_repeat_a_fourth_dig", "forage_three_soils", "forage-stone",
            failure => failure.Message.Contains("forage_three_soil_cells_exhausted") &&
                failure.Results.Count(task => task.Skill == "dig_soil" && task.Status == "success") == 3 &&
                failure.Results.All(task => task.Skill != "forage_stone" || task.Status != "success"),
            client => client.ForageSoilPicks == 3 && client.ForageStonePicks == 0 && client.DuplicateSoilPicks == 0, results);
        Failure("forage_inventory_stone_gain_without_visible_mining_is_not_success", "forage_fake_stone_gain", "forage-stone",
            failure => failure.Message.Contains("forage_three_soil_cells_exhausted") &&
                failure.Results.All(task => task.Skill != "forage_stone" || task.Status != "success"),
            client => client.ForageSoilPicks == 3 && client.ForageStonePicks == 0 && client.LastStone == 50, results);
        Failure("forage_frozen_resource_candidate_cannot_unlock_pick", "forage_frozen_target", "forage-stone",
            failure => failure.Message.Contains("visible_side_soil_discovery_not_retained"),
            client => client.UseActions == 0 && client.MovementActions == 0 && client.CraftRequests == 0, results);
        Failure("forage_gone_and_stone_gain_without_actual_pick_cannot_succeed", "forage_no_actual_pick", "forage-stone",
            failure => failure.Message.Contains("stone_gone_without_actual_pick_use") &&
                failure.Results.All(task => task.Skill != "mine_stone" || task.Status != "success"),
            client => client.ForageStonePicks == 1 && client.UseActions == 1 && client.LastStone == 1, results);
        Failure("forage_actual_pick_and_gone_without_stone_gain_cannot_succeed", "forage_stone_without_gain", "forage-stone",
            failure => failure.Results.All(task => task.Skill != "mine_stone" || task.Status != "success"),
            client => client.ForageStonePicks == 1 && client.LastStone == 0 && client.GoneSamples >= 4, results);
        Failure("forage_completed_soil_coordinate_cannot_be_selected_again", "forage_repeated_soil", "forage-stone",
            failure => failure.Message.Contains("no_proven_visible_ground_for_next_step") &&
                failure.Results.Count(task => task.Skill == "dig_soil" && task.Status == "success") == 1,
            client => client.ForageSoilPicks == 1 && client.DuplicateSoilPicks == 0 && client.MovementActions == 0, results);
        foreach (string scenario in new[] { "forage_global_time", "forage_global_x", "forage_global_down" })
            Failure("forage_phase_begin_cannot_reset_" + scenario, scenario, "forage-stone",
                failure => failure.Message.Contains(scenario == "forage_global_time" ? "forage_global_timeout_45s" :
                    scenario == "forage_global_x" ? "forage_global_horizontal_travel_384px" : "forage_global_downward_motion_32px") &&
                    failure.Results.Count(task => task.Skill == "dig_soil" && task.Status == "success") == 1,
                client => client.ForageSearchStarts == 2 && client.ForageSoilPicks == 1 && client.ForageStonePicks == 0 &&
                    client.DeadlineInjected, results);
        Failure("forage_cumulative_damage_survives_dig_begin_reset", "forage_cumulative_damage", "forage-stone",
            failure => failure.Message.Contains("forage_global_health_loss_or_low_health") &&
                failure.Results.Count(task => task.Skill == "dig_soil" && task.Status == "success") == 1,
            client => client.ForageSoilPicks == 2 && client.LastHealth == 80 && client.ForageStonePicks == 0, results);
        Failure("forage_near_enemy_in_move_sample_prevents_resource_action", "forage_enemy_during_search", "forage-stone",
            failure => failure.Message.Contains("forage_stopped_for_nearby_visible_enemy"),
            client => client.MovementActions == 1 && client.UseActions == 0 && client.CraftRequests == 0, results);
        Failure("forage_initial_health_below_sixty_prevents_search", "forage_low_health", "forage-stone",
            failure => failure.Message.Contains("forage_requires_health_60"),
            client => client.MovementActions == 0 && client.UseActions == 0 && client.TorchSelections == 0, results);
        foreach (string scenario in new[] { "seek_up_visible_stone", "seek_up_wrong_direction" })
        {
            var client = new FakeClient(scenario);
            var task = Run(client, "seek-stone").Single(t => t.Skill == "seek_stone");
            Require(task.Status == "success" && client.MovementActions == 1 && client.UseActions == 0 &&
                client.UpActions == (scenario == "seek_up_visible_stone" ? 1 : 0) &&
                client.UpWithoutFreshProof == 0 && !client.LastInput.Up,
                "normal_current_direction_platform_up_" + scenario);
            results.Add(new { Check="normal_current_direction_platform_up_"+scenario,
                Status="pass_synthetic_offline", client.UpActions, client.UpWithoutFreshProof });
        }
        Failure("withdrawn_platform_proof_prevents_a_second_up_move", "seek_up_withdrawn", "seek-stone",
            failure => failure.Message.Contains("no_proven_visible_ground_for_next_step"),
            client => client.UpActions == 1 && client.MovementActions == 1 && client.UpWithoutFreshProof == 0 &&
                !client.LastInput.Up, results);
        Failure("invented_ack_cannot_authorize_platform_up", "seek_up_ack_only", "seek-stone",
            failure => failure.Message.Contains("no_proven_visible_ground_for_next_step"),
            client => client.UpActions == 0 && client.MovementActions == 0 && client.InventedAcks > 0, results);
        Failure("frozen_sample_cannot_reuse_platform_up_proof", "seek_up_frozen", "seek-stone",
            failure => failure.Message.Contains("platform_up_requires_new_observation"),
            client => client.UpActions == 1 && client.MovementActions == 1 && client.UpWithoutFreshProof == 0 &&
                !client.LastInput.Up, results);
        var platformAttack = new FakeClient("combat_up_attack");
        var platformEncounter = Run(platformAttack, "combat-trial").Single(t => t.Skill == "combat_trial");
        Require(platformEncounter.Status == "success" && !platformEncounter.KillVerified &&
            platformAttack.UseActions == 1 && platformAttack.MovementActions == 0 && platformAttack.UpActions == 0,
            "platform_route_item_use_stays_still_without_up");
        results.Add(new { Check="platform_route_item_use_stays_still_without_up",Status="pass_synthetic_offline",
            platformAttack.UseActions,platformAttack.MovementActions,platformAttack.UpActions });
        var lowGhost = new FakeClient("health_low_ghost_defense");
        var lowRecovered = Run(lowGhost, "recover-health").Single(t => t.Skill == "recover_health");
        Require(lowRecovered.Status == "success" && lowRecovered.HealthBefore == 15 && lowRecovered.HealthAfter == 60 &&
            lowGhost.UseActions == 2 && lowGhost.UseAfterMissing == 0 && lowGhost.MovementActions == 0 &&
            lowGhost.UpActions == 0 && lowGhost.CraftRequests == 0 && !lowRecovered.KillVerified,
            "living_fifteen_health_can_defend_visible_ghost_and_retain_normal_recovery");
        results.Add(new { Check="living_fifteen_health_can_defend_visible_ghost_and_retain_normal_recovery",
            Status="pass_synthetic_offline",lowRecovered.HealthBefore,lowRecovered.HealthAfter,
            lowGhost.UseActions,lowGhost.MovementActions,lowRecovered.KillVerified });
        Failure("fifteen_health_does_not_unlock_resource_actions", "health_low_resource", "dig-test",
            failure => failure.Message.Contains("unsafe_low_health"),
            client => client.ActCalls == 0 && client.UseActions == 0 && client.MovementActions == 0 && client.CraftRequests == 0, results);
        Failure("dead_low_health_recovery_cannot_attack_or_rearm", "health_low_dead", "recover-health",
            failure => failure.Message.Contains("unsafe_world_ui_death_or_pause"),
            client => client.ActCalls == 0 && client.UseActions == 0 && client.MovementActions == 0, results);
        Failure("hidden_low_health_enemy_cannot_authorize_defense_or_claim_recovery", "health_low_hidden", "recover-health",
            failure => failure.Message.Contains("normal_health_recovery_timeout"),
            client => client.RecoverySelections >= 2 && client.UseActions == 0 && client.MovementActions == 0 && client.CraftRequests == 0, results);
        Failure("manual_low_health_recovery_cannot_regain_control", "health_low_manual", "recover-health",
            failure => failure.Message.Contains("control_lost_no_automatic_rearm"),
            client => client.ActCalls == 0 && client.UseActions == 0, results);
        Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static List<StageCTaskResult> Run(FakeClient client, string mode, int healthTarget = 60)
    { return StageC.Run(client, () => (client.Mode == "stone_without_gain" && client.GoneSamples >= 4) ||
        (client.Mode == "forage_stone_without_gain" && client.GoneSamples >= 4) ||
        ((client.Mode == "dirt_without_gain" || client.Mode == "grass_changed") && client.SoilChangedSamples >= 4),
        (kind, detail, observation) => {
            if (kind == "stage_c_skill_start" && detail.StartsWith("search_stone_or_soil:", StringComparison.Ordinal))
            {
                ++client.ForageSearchStarts;
                if (client.ForageSearchStarts == 2 && (client.Mode == "forage_global_time" ||
                    client.Mode == "forage_global_x" || client.Mode == "forage_global_down")) client.DeadlineInjected = true;
            }
            if (kind == "stage_c_recovery" && detail.StartsWith("soil_approach:", StringComparison.Ordinal)) ++client.SettlingStarts;
            // A slow result consumer must not turn the ten-second mining task
            // into an unlimited wait. No game or transport is executed here.
            if (client.Mode == "soil_coast_deadline" && kind == "stage_c_action_result" &&
                detail.StartsWith("dig_soil:", StringComparison.Ordinal) && !client.DeadlineInjected)
            { client.DeadlineInjected = true; Thread.Sleep(10020); }
        }, mode, 1, healthTarget: healthTarget); }

    private static void Failure(string name, string scenario, string mode, Func<StageCFailure, bool> resultCheck,
        Func<FakeClient, bool> actionCheck, List<object> results)
    {
        var client = new FakeClient(scenario);
        StageCFailure caught = null;
        try { Run(client, mode); }
        catch (StageCFailure failure) { caught = failure; }
        Require(caught != null && resultCheck(caught) && actionCheck(client), name + ":" +
            (caught == null ? "unexpected_success" : caught.Message));
        results.Add(new { Check = name, Status = "pass_synthetic_offline", Reason = caught.Message,
            client.CraftRequests, client.UseActions, client.UseAfterMissing, client.GoneSamples, client.SoilChangedSamples, client.InventedAcks,
            client.WeaponSelections, client.MovementActions, client.MovementAfterBoundary, client.DamageSamples,
            client.OvershootSamples, client.PickupGainSamples, client.RecoverySelections,
            client.ThresholdSamples, client.PostThresholdLowSamples, client.LastHealth,
            client.UnsafeGroundSamples, client.FoundStoneSamples });
    }

    private static void Require(bool condition, string name)
    { if (!condition) throw new InvalidOperationException("offline_boundary_failed:" + name); }

    private sealed class FakeClient : IStageBClient
    {
        public readonly string Mode;
        public int CraftRequests, UseActions, UseAfterMissing, GoneSamples, SoilChangedSamples, InventedAcks, RetainedProductSamples;
        public int WeaponSelections, MovementActions, MovementAfterBoundary, DamageSamples, OvershootSamples,
            PickupGainSamples, RecoverySelections, ThresholdSamples, PostThresholdLowSamples, LastHealth,
            UnsafeGroundSamples, FoundStoneSamples, BelowRequestedTargetSamples, ObserveCalls, ActCalls, LeftActions, RightActions;
        public int NeutralApproachSamples, FreshReachSamples, PickBeforeFreshReach, SettlingStarts;
        public int TorchSelections, TorchHeldSamples, UseBeforeTorchHeld, PlacedTorchSamples;
        public int ForageSearchStarts, ForageSoilPicks, ForageStonePicks, DuplicateSoilPicks, StoneRetainedSamples, LastStone;
        public int UpActions, UpWithoutFreshProof;
        public InputState LastInput { get { return _input; } }
        private readonly HashSet<int> _forageDug = new HashSet<int>();
        private int _forageLastSoilX = -1;
        public bool DeadlineInjected;
        private long _sequence;
        private InputState _input = new InputState();
        private OwnObservation _last;
        private int _selected = 0;
        private bool _missing;
        private bool _unsafeBoundary;

        public FakeClient(string mode) { Mode = mode; }

        public OwnObservation Observe()
        {
            ++ObserveCalls;
            // A craft ACK advertises completion, while the real sample stream
            // remains frozen at the qualification observation.
            if (Mode == "craft_ack_only" && CraftRequests > 0) return _last;
            if (Mode == "torch_hold_frozen" && TorchSelections > 0) return _last;
            if (Mode == "seek_up_frozen" && UpActions > 0) return _last;
            ++_sequence;
            if ((Mode == "stone_without_gain" || Mode == "enemy_hidden") && UseActions > 0) _missing = true;
            if (Mode == "enemy_hidden_damage" && UseActions > 0) _missing = true;
            if (Mode == "enemy_hidden_no_use" && WeaponSelections > 0) _missing = true;
            if ((Mode == "dirt_without_gain" || Mode == "dirt_success") && UseActions > 0) _missing = true;
            if (Mode == "soil_approach_success" && UseActions > 0) _missing = true;
            if (Mode.StartsWith("soil_coast_", StringComparison.Ordinal) && UseActions > 0) _missing = true;
            if (Mode == "stone_without_gain" && _missing) ++GoneSamples;
            if ((Mode == "dirt_without_gain" || Mode == "dirt_success" || Mode == "grass_changed") && UseActions > 0)
                ++SoilChangedSamples;
            _last = Sample(_sequence, false);
            return _last;
        }

        public OwnObservation Act(OwnObservation observation, InputState input)
        {
            ++ActCalls;
            if (input.Up)
            {
                ++UpActions;
                if (_last == null || !PlatformStepGuard.CanApply(input,_last.Gameplay) ||
                    observation.Sequence != _last.Sequence) ++UpWithoutFreshProof;
            }
            if (input.CraftRecipe != null)
            {
                ++CraftRequests;
                if (CraftRequests > 1) throw new InvalidOperationException("synthetic_duplicate_craft_request");
            }
            if (input.UseItem)
            {
                ++UseActions;
                if (_missing) ++UseAfterMissing;
                if (Mode.StartsWith("soil_coast_", StringComparison.Ordinal) && FreshReachSamples < 2) ++PickBeforeFreshReach;
                if (Mode.StartsWith("torch_", StringComparison.Ordinal) && TorchHeldSamples < 3) ++UseBeforeTorchHeld;
                if (Mode.StartsWith("forage_", StringComparison.Ordinal))
                {
                    if (Mode == "forage_torch_path" && TorchHeldSamples < 3) ++UseBeforeTorchHeld;
                    if (input.AimTileY == 3)
                    {
                        if (!_forageDug.Add(input.AimTileX)) ++DuplicateSoilPicks;
                        else { ++ForageSoilPicks; _forageLastSoilX = input.AimTileX; }
                    }
                    else if (input.AimTileY == 2) ++ForageStonePicks;
                }
            }
            if (input.Left || input.Right || input.Jump)
            {
                ++MovementActions;
                if (_unsafeBoundary || _missing) ++MovementAfterBoundary;
            }
            if (input.Left) ++LeftActions;
            if (input.Right) ++RightActions;
            if (input.SelectedSlot == 0 && input.AimTileX >= 0) ++WeaponSelections;
            if ((Mode == "health_frozen" || Mode == "health_threshold_lost" ||
                Mode == "health_target_eighty" || Mode == "health_target_clamped" ||
                Mode.StartsWith("health_low_", StringComparison.Ordinal)) && input.SelectedSlot == 0)
                ++RecoverySelections;
            if (input.SelectedSlot >= 0) _selected = input.SelectedSlot;
            if (Mode.StartsWith("torch_", StringComparison.Ordinal) && input.SelectedSlot == 8 && !input.UseItem)
                ++TorchSelections;
            if (Mode == "forage_torch_path" && input.SelectedSlot == 8 && !input.UseItem) ++TorchSelections;
            _input = input.Copy();
            if (Mode == "torch_hold_frozen" && input.SelectedSlot == 8)
            { ++InventedAcks; return Sample(_sequence + 1000, true); }
            if (Mode == "craft_ack_only" && input.CraftRecipe != null)
            {
                ++InventedAcks;
                return Sample(_sequence + 1000, true);
            }
            if (Mode == "seek_ack_safe_only")
            { ++InventedAcks; return Sample(_sequence + 1000, true); }
            if (Mode == "seek_up_ack_only")
            { ++InventedAcks; return Sample(_sequence + 1000,true); }
            if (Mode == "soil_coast_ack_only")
            {
                ++InventedAcks;
                OwnObservation ack = Sample(_sequence + 1000, true);
                ack.X = 34; ack.Gameplay.CanStepRight = true;
                return ack;
            }
            return _last;
        }

        private OwnObservation Sample(long sequence, bool invented)
        {
            bool normalCraft = Mode == "craft_once" || Mode == "craft_late_gain" || Mode == "craft_masked_gain";
            bool produced = invented || (normalCraft && CraftRequests > 0);
            if (normalCraft && produced) ++RetainedProductSamples;
            int wood = produced ? 10 : 20;
            if (produced && (Mode == "craft_masked_gain" || (Mode == "craft_late_gain" && RetainedProductSamples > 1))) wood = 17;
            var g = new GameplayObservation { Wood = wood, WoodenBows = produced ? 1 : 0,
                WoodenArrows = 25, SelectedSlot = _selected, PickaxeSlot = 2, BowSlot = 4, SwordSlot = 0,
                HasFreeSlot = true, CanCraftRecipes = new[] { GameplayRecipeIds.WoodenBow } };
            if (Mode == "stone_without_gain")
            {
                VisibleTarget target = new VisibleTarget { TileX = 3, TileY = 2 };
                if (_missing) g.GoneStoneTargets = new[] { target };
                else g.StoneTargets = new[] { target };
                // Stone intentionally never increases, even after a legal pick.
            }
            if ((Mode == "enemy_hidden" || Mode == "enemy_hidden_damage" || Mode == "enemy_hidden_no_use") && !_missing)
            {
                // The no-use target is outside sword reach; its first approach
                // sample loses visibility without claiming an actual attack.
                g.WoodenArrows = 0;
                float enemyX = Mode == "enemy_hidden_no_use" ? 180 : 52;
                g.Enemies = new[] { new VisibleEnemy { Id = 7, Kind = "slime", TileX = (int)(enemyX / 16),
                    TileY = 2, X = enemyX, Y = 33 } };
                // Only this fixture's one approach step has supplied visible
                // flat-ground evidence. All other samples default to unknown.
                if (Mode == "enemy_hidden_no_use") g.CanStepRight = true;
            }
            if (Mode == "combat_unknown_ground" || Mode == "combat_ground_withdrawn")
            {
                g.WoodenArrows = 0;
                g.Enemies = new[] { new VisibleEnemy { Id = 7, Kind = "slime", TileX = 11,
                    TileY = 2, X = 180, Y = 33 } };
                g.CanStepRight = Mode == "combat_ground_withdrawn" && MovementActions == 0;
                if (Mode == "combat_ground_withdrawn" && MovementActions > 0)
                { ++UnsafeGroundSamples; _unsafeBoundary = true; }
            }
            if (Mode == "dirt_without_gain" || Mode == "grass_changed" || Mode == "dirt_success" || Mode == "dirt_damage")
            {
                VisibleTarget soil = new VisibleTarget { TileX = 5, TileY = 3 };
                if (_missing) g.GoneDirtTargets = new[] { soil };
                else g.DirtTargets = new[] { soil };
                // Grass->Dirt remains soil, so the candidate stays and Gone is
                // empty. A simultaneous other drop cannot prove this cell gone.
                if (UseActions > 0 && (Mode == "grass_changed" || Mode == "dirt_success")) g.Dirt = 1;
            }
            float x = 22;
            float y = 12;
            int health = Mode == "dirt_damage" && UseActions > 0 ? 79 : 100;
            if (Mode.StartsWith("torch_", StringComparison.Ordinal))
            {
                var torchTarget = new VisibleTarget { TileX=3, TileY=2 };
                g.Torches = 2; g.TorchSlot = Mode == "torch_bad_slot" ? 20 : 8;
                if (!invented && _selected == 8) ++TorchHeldSamples;
                if (invented || (TorchHeldSamples >= 3 && Mode != "torch_hold_no_target"))
                    g.TorchPlacementTargets = new[] { torchTarget };
                if (UseActions > 0 && Mode == "torch_hold_reveals_target")
                {
                    g.Torches=1; g.TorchPlacementTargets=new VisibleTarget[0]; g.TorchTargets=new[] { torchTarget };
                    ++PlacedTorchSamples;
                }
                if (TorchSelections > 0 && Mode == "torch_hold_damage")
                { health=80; ++DamageSamples; }
            }
            if (Mode.StartsWith("soil_coast_", StringComparison.Ordinal))
            {
                var soil = new VisibleTarget { TileX=6,TileY=3 };
                g.CanStepRight = Mode != "soil_coast_never" && Mode != "soil_coast_ack_only" &&
                    Mode != "soil_coast_deadline" && Mode != "soil_coast_safe_recovered";
                if (_missing) { g.GoneDirtTargets=new[] { soil }; g.Dirt=1; }
                else g.DirtTargets=new[] { soil };
                // Samples after a neutral recovery advance own position only
                // through synthetic coast. No move command is accepted while
                // that current ground proof is false.
                bool neutral = !_input.Left && !_input.Right && !_input.UseItem && sequence >= 3;
                if (neutral && !invented) ++NeutralApproachSamples;
                if (Mode == "soil_coast_safe_recovered")
                { g.CanStepRight = sequence >= 3; x = MovementActions > 0 ? 34 : 22; }
                else if (Mode == "soil_coast_budget_exhausted")
                { x=22+MovementActions*2; g.CanStepRight = !_input.Right; }
                else if (MovementActions > 0)
                {
                    x = neutral || FreshReachSamples > 0 ? 32.4375f : 29.2f;
                    g.CanStepRight = false; _unsafeBoundary=true;
                    if (neutral && Mode == "soil_coast_damage") { health=79; ++DamageSamples; }
                    if (neutral && Mode == "soil_coast_enemy")
                        g.Enemies=new[] { new VisibleEnemy { Id=7,Kind="slime",TileX=3,TileY=2,X=60,Y=33 } };
                    if (neutral && Mode == "soil_coast_visibility_lost") g.DirtTargets=new VisibleTarget[0];
                }
                if (!invented && Math.Abs(104-(x+10)) <= 64) ++FreshReachSamples;
            }
            if (Mode.StartsWith("soil_approach_", StringComparison.Ordinal))
            {
                VisibleTarget soil = new VisibleTarget { TileX = Mode == "soil_approach_beyond128" ? 11 : 6, TileY = 3 };
                g.CanStepRight = Mode != "soil_approach_unknown_ground";
                x = MovementActions > 0 ? 34 : 22;
                if (_missing) { g.GoneDirtTargets = new[] { soil }; g.Dirt = 1; }
                else if (Mode != "soil_approach_visibility_lost" || MovementActions == 0) g.DirtTargets = new[] { soil };
                if (MovementActions > 0 && Mode == "soil_approach_damage")
                { health = 79; ++DamageSamples; _unsafeBoundary = true; }
                if (MovementActions > 0 && Mode == "soil_approach_enemy")
                {
                    g.Enemies = new[] { new VisibleEnemy { Id=7, Kind="slime", TileX=3, TileY=2, X=60, Y=33 } };
                    _unsafeBoundary = true;
                }
                if (MovementActions > 0 && Mode == "soil_approach_visibility_lost") _unsafeBoundary = true;
            }
            if (Mode.StartsWith("soil_pickup_", StringComparison.Ordinal))
            {
                var soil = new VisibleTarget { TileX=5, TileY=3 };
                g.GoneDirtTargets = new[] { soil }; g.CanStepRight = true;
                if (MovementActions > 0)
                {
                    x = 26;
                    if (Mode == "soil_pickup_success" || (Mode == "soil_pickup_ground_withdrawn" && sequence >= 4)) g.Dirt=1;
                    if (Mode == "soil_pickup_ground_withdrawn")
                    { g.CanStepRight=false; ++UnsafeGroundSamples; _unsafeBoundary=true; }
                    if (Mode == "soil_pickup_visibility_lost")
                    { g.GoneDirtTargets=new VisibleTarget[0]; _unsafeBoundary=true; }
                    if (Mode == "soil_pickup_damage")
                    { health=79; ++DamageSamples; _unsafeBoundary=true; }
                    if (Mode == "soil_pickup_enemy")
                    {
                        g.Enemies=new[] { new VisibleEnemy { Id=7,Kind="slime",TileX=3,TileY=2,X=60,Y=33 } };
                        _unsafeBoundary=true;
                    }
                    if (Mode == "soil_pickup_gain_overshoot")
                    { x=49; g.Dirt=1; ++OvershootSamples; ++PickupGainSamples; _unsafeBoundary=true; }
                }
            }
            if (Mode == "enemy_hidden_damage" && _missing)
            { health = 80; ++DamageSamples; _unsafeBoundary = true; }
            if (Mode == "pickup_overshoot" || Mode == "pickup_gain_with_x_overshoot" || Mode == "pickup_gain_with_fall")
            {
                g.GoneDirtTargets = new[] { new VisibleTarget { TileX = 5, TileY = 3 } };
                if (sequence >= 3)
                {
                    // The bridge's normal neutral sample can contain movement
                    // from knockback/gravity. A simultaneous normal pickup is
                    // real inventory progress, but cannot erase the safety stop.
                    if (Mode == "pickup_gain_with_fall") y = 33;
                    else x = 49;
                    ++OvershootSamples; _unsafeBoundary = true;
                    if (Mode != "pickup_overshoot") { g.Dirt = 1; ++PickupGainSamples; }
                }
            }
            if (Mode == "health_frozen") health = 59;
            if (Mode.StartsWith("health_low_", StringComparison.Ordinal))
            {
                health = Mode == "health_low_ghost_defense" && UseActions >= 2 ? 60 : 15;
                if (Mode == "health_low_ghost_defense" && UseActions < 2)
                    g.Enemies = new[] { new VisibleEnemy { Id=7,Kind="ghost",TileX=3,TileY=2,X=60,Y=33 } };
                // Hidden appearances are absent from the bridge's list; never
                // synthesize an attack target from damage or a remembered slot.
                if (Mode == "health_low_hidden") g.Enemies = new VisibleEnemy[0];
            }
            if (Mode == "health_threshold_lost")
            {
                // Fresh 60 HP at the first recovery step, then 59 at the
                // required neutral confirmation and every following sample.
                health = sequence == 3 ? 60 : 59;
                if (health == 60) ++ThresholdSamples;
                else if (sequence > 3) ++PostThresholdLowSamples;
            }
            if (Mode == "health_confirmation_damage")
            {
                // Both observations still satisfy the 60-HP target; the
                // confirmation must nonetheless obey the damage stop rule.
                health = sequence >= 3 ? 60 : 80;
                if (sequence >= 3) ++DamageSamples;
            }
            if (Mode == "health_target_eighty" || Mode == "health_target_clamped")
            {
                // 60 HP remains below the requested 80; only two normal
                // standing selections produce 80, then a newer neutral sample
                // must retain it. Act itself does not advertise a health delta.
                health = RecoverySelections >= 2 ? 80 : 60;
                if (health == 80) ++ThresholdSamples;
                else if (sequence >= 3) ++BelowRequestedTargetSamples;
            }
            if (Mode.StartsWith("seek_", StringComparison.Ordinal))
            {
                // Begin with no known stone and only the explicitly supplied
                // visible-ground direction. Inventory is constant throughout.
                g.Stone = 2; g.Dirt = 3;
                g.CanStepRight = Mode != "seek_unknown_ground";
                g.CanStepLeft = false;
                if (MovementActions > 0)
                {
                    x = 26;
                    if (Mode == "seek_permission_withdrawn")
                    { g.CanStepRight = false; ++UnsafeGroundSamples; _unsafeBoundary = true; }
                    else if (Mode == "seek_visible_stone" || Mode == "seek_damage_with_stone" || Mode == "seek_fall_with_stone")
                    {
                        g.StoneTargets = new[] { new VisibleTarget { TileX = 3, TileY = 2 } };
                        ++FoundStoneSamples;
                        if (Mode == "seek_damage_with_stone")
                        { health = 80; _unsafeBoundary = true; }
                        if (Mode == "seek_fall_with_stone")
                        { y = 45; _unsafeBoundary = true; }
                    }
                }
            }
            if (Mode == "seek_neutral_three_recoveries")
            {
                x=22+MovementActions*4; g.StoneTargets=new VisibleTarget[0]; g.CanStepRight=!_input.Right;
                if (MovementActions >= 4)
                { g.StoneTargets=new[] { new VisibleTarget { TileX=3,TileY=2 } }; ++FoundStoneSamples; }
            }
            if (Mode == "seek_ack_safe_only")
            { g.CanStepRight=invented; g.StoneTargets=new VisibleTarget[0]; }
            if (Mode.StartsWith("seek_up_",StringComparison.Ordinal))
            {
                g.StoneTargets=new VisibleTarget[0];g.CanStepLeft=false;
                g.CanStepRight=Mode != "seek_up_ack_only" || invented;
                g.StepRequiresUpRight=g.CanStepRight && Mode != "seek_up_wrong_direction";
                g.StepRequiresUpLeft=Mode == "seek_up_wrong_direction";
                if (MovementActions > 0)
                {
                    x=26;
                    if (Mode == "seek_up_withdrawn")
                    {g.CanStepRight=false;g.StepRequiresUpRight=false;_unsafeBoundary=true;}
                    else if (Mode == "seek_up_visible_stone" || Mode == "seek_up_wrong_direction")
                    {g.StoneTargets=new[] {new VisibleTarget {TileX=3,TileY=2}};++FoundStoneSamples;}
                }
            }
            if (Mode == "combat_up_attack")
            {
                g.WoodenArrows=0;g.CanStepRight=true;g.StepRequiresUpRight=true;
                if (UseActions == 0) g.Enemies=new[] {new VisibleEnemy {Id=7,Kind="slime",TileX=5,TileY=2,X=82,Y=33}};
            }
            if (Mode == "seek_one_reverse_budget")
            {
                x=22+MovementActions*4; g.StoneTargets=new VisibleTarget[0];
                g.CanStepRight=MovementActions != 1; g.CanStepLeft=MovementActions == 1;
            }
            if (Mode.StartsWith("forage_", StringComparison.Ordinal))
            {
                x=22+MovementActions*12; g.Dirt=ForageSoilPicks;
                g.CanStepLeft=g.CanStepRight=Mode != "forage_repeated_soil";
                if (_forageLastSoilX >= 0)
                    g.GoneDirtTargets=new[] { new VisibleTarget { TileX=_forageLastSoilX, TileY=3 } };
                bool directStone = Mode == "forage_no_actual_pick" || Mode == "forage_stone_without_gain";
                bool soilRevealsStone = (Mode == "forage_soil_then_stone" || Mode == "forage_torch_path") && ForageSoilPicks > 0;
                if (Mode == "forage_torch_path")
                {
                    g.Torches=2; g.TorchSlot=8;
                    if (!invented && _selected == 8) ++TorchHeldSamples;
                }
                if (directStone || soilRevealsStone)
                {
                    var stone=new VisibleTarget { TileX=4,TileY=2 };
                    if (ForageStonePicks == 0) g.StoneTargets=new[] { stone };
                    else
                    {
                        g.GoneStoneTargets=new[] { stone }; ++GoneSamples;
                        if (Mode != "forage_stone_without_gain") { g.Stone=1; ++StoneRetainedSamples; }
                    }
                }
                else if (Mode != "forage_enemy_during_search" &&
                    (Mode != "forage_torch_path" || TorchHeldSamples >= 3))
                    g.DirtTargets=new[] { new VisibleTarget { TileX=Mode == "forage_repeated_soil" ? 5 : 5+ForageSoilPicks,TileY=3 } };
                if (Mode == "forage_fake_stone_gain" && ForageSoilPicks > 0) g.Stone=50;
                if (Mode == "forage_cumulative_damage") health=100-10*ForageSoilPicks;
                if (Mode == "forage_low_health") health=59;
                if (Mode == "forage_enemy_during_search" && MovementActions > 0)
                    g.Enemies=new[] { new VisibleEnemy { Id=7,Kind="slime",TileX=3,TileY=2,X=x+30,Y=33 } };
                if (DeadlineInjected && Mode == "forage_global_x") x=422;
                if (DeadlineInjected && Mode == "forage_global_down") y=45;
            }
            LastHealth = health;
            LastStone = g.Stone;
            InputState actualInput=_input.Copy();
            if (Mode == "forage_no_actual_pick") actualInput.UseItem=false;
            return new OwnObservation { Sequence = sequence, GameTick = Mode == "tick_frozen" ? 1 :
                    Mode == "forage_frozen_target" ? Math.Min(2,sequence) : sequence,
                MonotonicMs = sequence * 50 + (Mode == "forage_global_time" && DeadlineInjected ? 45000 : 0),
                WorldId = "synthetic-current-world", X = x, Y = y,
                Health = health,
                MaxHealth = Mode == "health_target_clamped" ? 80 : 100,
                ControlState = Mode == "health_low_manual" ? "Manual" : "Agent",
                Dead = Mode == "health_low_dead", Inputs = actualInput, Gameplay = g,
                OptionsOpen = (Mode == "unsafe_craft" && sequence >= 2) ||
                    (Mode == "torch_hold_ui" && TorchSelections > 0) };
        }
    }
}
