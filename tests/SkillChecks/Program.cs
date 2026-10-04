using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TerrariaAgent.Controller;
using TerrariaAgent.Protocol;

internal static class Program
{
    // Synthetic offline boundaries. No game process, save file,
    // live world, or gameplay success is exercised by these fixtures.
    private static int Main()
    {
        var results = new List<object>();
        Check("ack_cannot_advance_frozen_observation", new FakeClient("stale"),
            failure => (failure.Message.Contains("observation_stalled") || failure.Message.Contains("observation_not_advancing")) &&
                !failure.Results.Any(task => task.Status == "success"),
            client => client.GameplayActions == 0, results);
        Check("control_loss_forbids_cleanup_from_previous_agent_sample", new FakeClient("lost"),
            failure => failure.Message.Contains("control_lost_no_automatic_rearm"),
            client => client.Actions == 1 && client.GameplayActions == 0, results);
        Check("hidden_tree_with_wood_gain_is_not_chop_evidence", new FakeClient("hidden"),
            failure => failure.Message.Contains("tree_visibility_lost_without_gone_confirmation") &&
                !failure.Results.Any(task => task.Skill == "chop_tree" && task.Status == "success"),
            client => client.Hidden && client.UseAfterHidden == 0 && client.UseActions >= 2, results);
        CheckPartialPickup(results);
        CheckLatePickup(results);
        Check("late_pickup_cannot_replace_first_exact_consumption_proof", new FakeClient("masked_craft"),
            failure => failure.Message.Contains("craft_timeout") &&
                !failure.Results.Any(task => task.Skill == "craft_workbench" && task.Status == "success"),
            client => client.CraftRequests == 1 && client.LateExactSamples == 0 && client.PlacementRequests == 0, results);
        Check("twenty_health_loss_while_still_healthy_stops_resource_tools", new FakeClient("damage_twenty"),
            failure => failure.Message.Contains("resource_task_stopped_after_health_loss_20") &&
                !failure.Results.Any(task => task.Skill == "chop_tree" && task.Status == "success"),
            client => client.UseActions == 1 && client.DamageSamples == 1 && client.LastHealth == 80 &&
                client.GameplayAfterUnsafe == 0 && client.CraftRequests == 0 && client.PlacementRequests == 0, results);
        foreach (string scenario in new[] { "paused", "options_open" })
            Check("resource_task_rejects_" + scenario + "_before_more_tools", new FakeClient(scenario),
                failure => failure.Message.Contains("unsafe_world_menu_death_or_text_input"),
                client => client.UseActions == 1 && client.UnsafeUiSamples == 1 && client.LastHealth == 100 &&
                    client.GameplayAfterUnsafe == 0 && client.CraftRequests == 0 && client.PlacementRequests == 0, results);
        Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static void CheckLatePickup(List<object> results)
    {
        var client = new FakeClient("late_pickup");
        List<StageBTaskResult> tasks = StageB.Run(client, () => false, (kind, detail, observation) => { });
        StageBTaskResult craft = tasks.Single(task => task.Skill == "craft_workbench");
        if (client.CraftRequests != 1 || client.LateExactSamples != 1 || client.LateGainSamples < 1 ||
            client.CraftWood != 26 || craft.Status != "success" || craft.WoodBefore != 26 ||
            craft.WoodAfter <= 16 || craft.WorkBenchesBefore != 0 || craft.WorkBenchesAfter != 1 ||
            client.PlacementRequests != 1 || tasks.Any(task => task.Status != "success"))
            throw new InvalidOperationException("offline_boundary_failed:exact_consumption_then_late_wood_pickup");
        results.Add(new { Check = "exact_consumption_then_late_wood_pickup", Status = "pass_synthetic_offline",
            Reason = "26_to_16_exact_witness_then_27_and_35_pickup;one_craft;placement_verified",
            client.LateExactSamples, client.LateGainSamples, client.CraftRequests, client.PlacementRequests,
            craft.WoodBefore, craft.WoodAfter, craft.WorkBenchesBefore, craft.WorkBenchesAfter });
    }

    private static void CheckPartialPickup(List<object> results)
    {
        var client = new FakeClient("partial_pickup");
        int woodAtCraftStart = -1;
        List<StageBTaskResult> tasks = StageB.Run(client, () => false, (kind, detail, observation) =>
        {
            if (kind == "stage_b_skill_start" && detail.StartsWith("craft_workbench:", StringComparison.Ordinal))
                woodAtCraftStart = observation.Gameplay.Wood;
        });
        StageBTaskResult collect = tasks.Single(task => task.Skill == "collect_wood");
        if (client.PartialWoodSamples < 4 || client.FirstTenSequence <= 0 || client.ReadyFreshSamples < 2 ||
            woodAtCraftStart < 10 || collect.Status != "success" || collect.WoodAfter < 10 ||
            collect.WoodAfter <= collect.WoodBefore || client.CraftWood < 10 ||
            client.CraftSequence <= client.FirstTenSequence || client.CraftRequests != 1 ||
            client.PlacementRequests != 1 || tasks.Any(task => task.Status != "success"))
            throw new InvalidOperationException("offline_boundary_failed:first_six_wood_must_wait_for_fresh_ten");
        results.Add(new { Check = "first_six_wood_must_wait_for_fresh_ten", Status = "pass_synthetic_offline",
            Reason = "six_wood_waited;advancing_ten_retained_before_craft;synthetic_chain_completed",
            client.PartialWoodSamples, client.FirstTenSequence, client.ReadyFreshSamples,
            WoodAtCraftStart = woodAtCraftStart, client.CraftWood, client.CraftSequence,
            client.CraftRequests, client.PlacementRequests });
    }

    private static void Check(string name, FakeClient client, Func<StageBFailure, bool> failureCheck,
        Func<FakeClient, bool> actionCheck, List<object> results)
    {
        StageBFailure caught = null;
        try { StageB.Run(client, () => false, (kind, detail, observation) => { }); }
        catch (StageBFailure failure) { caught = failure; }
        if (caught == null || !failureCheck(caught) || !actionCheck(client))
            throw new InvalidOperationException("offline_boundary_failed:" + name + ":" + (caught == null ? "unexpected_success" : caught.Message));
        results.Add(new { Check = name, Status = "pass_synthetic_offline", Reason = caught.Message,
            client.Actions, client.GameplayActions, client.UseActions, client.UseAfterHidden,
            client.DamageSamples, client.LastHealth, client.UnsafeUiSamples, client.GameplayAfterUnsafe,
            client.CraftRequests, client.PlacementRequests });
    }

    private sealed class FakeClient : IStageBClient
    {
        private readonly string _mode;
        private long _sequence;
        private InputState _input = new InputState();
        private OwnObservation _last;
        public int Actions;
        public int GameplayActions;
        public int UseActions;
        public int UseAfterHidden;
        public bool Hidden;
        public int PartialWoodSamples;
        public long FirstTenSequence;
        public int ReadyFreshSamples;
        public int CraftWood;
        public long CraftSequence;
        public int CraftRequests;
        public int PlacementRequests;
        public int LateExactSamples;
        public int LateGainSamples;
        public int DamageSamples, LastHealth, UnsafeUiSamples, GameplayAfterUnsafe;
        private bool _unsafeObserved;
        private int _deliveredWood;
        private int _selectedSlot = 2;
        private bool _crafted;
        private bool _placed;
        private int _postCraftSamples;
        public FakeClient(string mode) { _mode = mode; }

        public OwnObservation Observe()
        {
            if (_mode != "stale" || _sequence == 0) ++_sequence;
            if (_mode == "hidden" && UseActions >= 2) Hidden = true;
            _last = PickupMode ? PartialPickupSample() : Sample(_sequence, "Agent", Hidden, _input);
            if (_mode == "damage_twenty" && UseActions > 0)
            { _last.Health = 80; ++DamageSamples; _unsafeObserved = true; }
            if ((_mode == "paused" || _mode == "options_open") && UseActions > 0)
            {
                _last.GamePaused = _mode == "paused"; _last.OptionsOpen = _mode == "options_open";
                ++UnsafeUiSamples; _unsafeObserved = true;
            }
            LastHealth = _last.Health;
            return _last;
        }

        public OwnObservation Act(OwnObservation observation, InputState input)
        {
            ++Actions;
            bool gameplay = input.Left || input.Right || input.Jump || input.UseItem || input.SelectedSlot >= 0 ||
                input.CraftWorkBench || input.CraftRecipe != null;
            if (gameplay) { ++GameplayActions; if (_unsafeObserved) ++GameplayAfterUnsafe; }
            if (input.UseItem)
            {
                ++UseActions;
                if (Hidden) ++UseAfterHidden;
            }
            _input = input.Copy();
            if (PickupMode)
            {
                if (input.SelectedSlot >= 0) _selectedSlot = input.SelectedSlot;
                if (input.CraftWorkBench)
                {
                    ++CraftRequests;
                    CraftWood = observation.Gameplay.Wood;
                    CraftSequence = observation.Sequence;
                    if (_deliveredWood < 10 || _crafted) throw new InvalidOperationException("synthetic_invalid_craft");
                    _crafted = true;
                }
                else if (_crafted && !_placed && input.UseItem && input.SelectedSlot == 3 &&
                    input.AimTileX == 4 && input.AimTileY == 2)
                { _placed = true; ++PlacementRequests; }
            }
            if (_mode == "lost") return Sample(_sequence, "Manual", false, _input);
            if (_mode == "stale")
            {
                // Even a plausible successful action payload must not promote
                // the frozen Observe stream into gameplay result evidence.
                OwnObservation invented = Sample(_sequence + 1000, "Agent", true, _input);
                invented.Gameplay.WorkBenches = 1;
                invented.Gameplay.GoneTreeTargets = new[] { new VisibleTarget { TileX = 2, TileY = 2 } };
                return invented;
            }
            return _last;
        }

        private bool PickupMode { get { return _mode == "partial_pickup" || _mode == "late_pickup" || _mode == "masked_craft"; } }

        private OwnObservation PartialPickupSample()
        {
            bool gone = UseActions >= 2;
            if (gone && !_crafted)
            {
                if (_mode != "partial_pickup") _deliveredWood = 26;
                else if (PartialWoodSamples < 4) { _deliveredWood = 6; ++PartialWoodSamples; }
                else
                {
                    _deliveredWood = 10;
                    if (FirstTenSequence == 0) FirstTenSequence = _sequence;
                    ++ReadyFreshSamples;
                }
            }
            OwnObservation sample = Sample(_sequence, "Agent", gone, _input);
            GameplayObservation gameplay = sample.Gameplay;
            gameplay.Wood = _deliveredWood - (_crafted ? 10 : 0);
            if (_crafted && _mode != "partial_pickup")
            {
                ++_postCraftSamples;
                if (_mode == "late_pickup" && _postCraftSamples == 1) ++LateExactSamples;
                else
                {
                    gameplay.Wood += _mode == "late_pickup" && _postCraftSamples == 2 ? 11 : 19;
                    ++LateGainSamples;
                }
            }
            gameplay.WorkBenches = _crafted && !_placed ? 1 : 0;
            gameplay.WorkBenchSlot = _crafted && !_placed ? 3 : -1;
            gameplay.SelectedSlot = _selectedSlot;
            gameplay.CanCraftWorkBench = !_crafted && _deliveredWood >= 10;
            gameplay.GoneTreeTargets = gone ? new[] { new VisibleTarget { TileX = 2, TileY = 2 } } : new VisibleTarget[0];
            gameplay.PlacementTargets = _placed ? new VisibleTarget[0] : new[] { new VisibleTarget { TileX = 4, TileY = 2 } };
            gameplay.WorkBenchTargets = _placed ? new[] { new VisibleTarget { TileX = 4, TileY = 2 } } : new VisibleTarget[0];
            return sample;
        }

        private static OwnObservation Sample(long sequence, string state, bool hidden, InputState input)
        {
            return new OwnObservation { Sequence = sequence, GameTick = sequence, MonotonicMs = sequence * 50,
                WorldId = "offline-fixture-world", X = 30, Y = 19, Health = 100, MaxHealth = 100,
                ControlState = state, Inputs = input.Copy(),
                Gameplay = new GameplayObservation { Wood = hidden ? 20 : 0, WorkBenches = 0,
                    AxeSlot = 2, WorkBenchSlot = -1, SelectedSlot = input.SelectedSlot < 0 ? 2 : input.SelectedSlot,
                    HasFreeSlot = true, CanCraftWorkBench = hidden,
                    TreeTargets = hidden ? new VisibleTarget[0] : new[] { new VisibleTarget { TileX = 2, TileY = 2 } },
                    GoneTreeTargets = new VisibleTarget[0], PlacementTargets = new VisibleTarget[0], WorkBenchTargets = new VisibleTarget[0] } };
        }
    }
}
