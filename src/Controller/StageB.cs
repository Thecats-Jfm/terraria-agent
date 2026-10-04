using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using TerrariaAgent.Protocol;

namespace TerrariaAgent.Controller
{
    public interface IStageBClient
    {
        OwnObservation Observe();
        OwnObservation Act(OwnObservation observation, InputState input);
    }

    public sealed class StageBTaskResult
    {
        public string Skill { get; set; }
        public string Status { get; set; }
        public string Reason { get; set; }
        public long ElapsedMs { get; set; }
        public int Attempts { get; set; }
        public long StartSequence { get; set; }
        public long EndSequence { get; set; }
        public int WoodBefore { get; set; }
        public int WoodAfter { get; set; }
        public int WorkBenchesBefore { get; set; }
        public int WorkBenchesAfter { get; set; }
        public int TileX { get; set; } = -1;
        public int TileY { get; set; } = -1;
    }

    public sealed class StageBFailure : InvalidOperationException
    {
        public IReadOnlyList<StageBTaskResult> Results { get; private set; }

        internal StageBFailure(string reason, List<StageBTaskResult> results, Exception inner)
            : base(reason, inner)
        {
            Results = results.AsReadOnly();
        }
    }

    // A bounded, rule-only task. This controller neither arms nor reconnects.
    // Act replies acknowledge transport; only advancing game samples prove results.
    public static class StageB
    {
        public static List<StageBTaskResult> Run(IStageBClient client, Func<bool> isCancelled,
            Action<string, string, OwnObservation> record, bool harvestOnly = false)
        {
            if (client == null) throw new ArgumentNullException("client");
            if (isCancelled == null) throw new ArgumentNullException("isCancelled");
            if (record == null) throw new ArgumentNullException("record");
            return new Runner(client, isCancelled, record).Run(harvestOnly);
        }

        private sealed class Runner
        {
            private const int PeriodMs = 50;
            private const int InventorySlots = 50;
            private const int MaximumTargets = GameplayObservation.MaxTargetsPerKind;
            private readonly IStageBClient _client;
            private readonly Func<bool> _cancelled;
            private readonly Action<string, string, OwnObservation> _record;
            private readonly List<StageBTaskResult> _results = new List<StageBTaskResult>();
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private Stopwatch _skillTimer;
            private OwnObservation _current;
            private OwnObservation _skillStart;
            private string _world;
            private string _skill = "observe";
            private int _runStartHealth = -1;
            private int _attempts = 1;
            private VisibleTarget _target;
            private long _lastAdvanceMs;
            private bool _jumpRecoveryUsed;
            private bool _craftRequested;
            private bool _cleanupAllowed;

            public Runner(IStageBClient client, Func<bool> cancelled, Action<string, string, OwnObservation> record)
            {
                _client = client; _cancelled = cancelled; _record = record;
            }

            public List<StageBTaskResult> Run(bool harvestOnly)
            {
                try
                {
                    Begin("observe", "requires explicit existing Agent control, safe health and enabled Gameplay");
                    Observe();
                    // Acquire a fresh advancing tuple before any gameplay action.
                    OwnObservation initial = _current;
                    while (!Newer(_current, initial) && _skillTimer.ElapsedMilliseconds < 1000) Step(new InputState());
                    if (!Newer(_current, initial)) Fail("observation_not_advancing");
                    Succeed("fresh_gameplay_observation");

                    if (!harvestOnly && _current.Gameplay.WorkBenchTargets != null && _current.Gameplay.WorkBenchTargets.Length > 0)
                    {
                        Begin("reuse_workbench", "a complete normally observed workbench already exists");
                        Succeed("existing_visible_workbench;no_new_craft_or_tree_task_claimed");
                        return _results;
                    }
                    if (!harvestOnly && _current.Gameplay.WorkBenches > 0)
                    {
                        Begin("reuse_workbench_item", "normally observed own workbench item; do not craft twice");
                        Succeed("existing_backpack_workbench;resume_placement_only");
                        Place();
                        return _results;
                    }

                    Begin("discover_visible_tree", "requires filtered local TreeTargets");
                    _target = Nearest(_current.Gameplay.TreeTargets, null, false);
                    if (_target == null) Fail("no_visible_local_tree");
                    Succeed("visible_tree_selected");
                    VisibleTarget tree = CopyTarget(_target);

                    Begin("approach_tree", "visible tree; 15s; horizontal reach 64px; one stuck jump total");
                    Approach(tree, 64, _skillTimer, 15000);
                    if (!Contains(_current.Gameplay.TreeTargets, tree)) Fail("tree_no_longer_visible");
                    Succeed("actual_position_in_tree_reach");

                    OwnObservation beforeChop = Chop(tree);
                    Collect(tree, beforeChop);
                    if (harvestOnly) return _results;
                    Craft();
                    Place();
                    return _results;
                }
                catch (Exception error)
                {
                    string status = error is OperationCanceledException ? "cancelled" : "failure";
                    _results.Add(Result(status, error.Message, _skillStart));
                    TryRecord("stage_b_skill_result", _skill + ":" + status + ":" + error.Message);
                    throw new StageBFailure(_skill + ":" + error.Message, _results, error);
                }
                finally
                {
                    // One best-effort neutral request. The caller's final Stop and
                    // the bridge's independent TTL/disconnect gate remain mandatory.
                    if (_cleanupAllowed && _current != null && _current.ControlState == "Agent" && !_current.Dead && !_current.Menu &&
                        !_current.TextInput && _current.Health >= 25 && _current.WorldId == _world &&
                        _clock.ElapsedMilliseconds - _lastAdvanceMs <= ProtocolLimits.MaxObservationAgeMs)
                    {
                        try { _client.Act(_current, new InputState()); }
                        catch { /* No retries and no automatic rearm. */ }
                    }
                }
            }

            private OwnObservation Chop(VisibleTarget tree)
            {
                Begin("chop_tree", "requires visible tree and axe; 8s inventory-progress budget, 30s total");
                _target = tree;
                if (!Contains(_current.Gameplay.TreeTargets, tree)) Fail("tree_no_longer_visible");
                RequireSlot(_current.Gameplay.AxeSlot, "axe_missing");
                if (!InReach(tree, 64)) Fail("tree_out_of_reach");
                int before = _current.Gameplay.Wood;
                int greatestWood = before;
                long lastWoodProgress = 0;
                bool actualAxeUse = false;
                OwnObservation baseline = _current;
                while (_skillTimer.ElapsedMilliseconds < 30000)
                {
                    CheckCancelled();
                    RequireSlot(_current.Gameplay.AxeSlot, "axe_missing");
                    if (!InReach(tree, 64)) Fail("tree_out_of_reach");
                    if (Contains(_current.Gameplay.GoneTreeTargets, tree))
                    {
                        if (!actualAxeUse || !Newer(_current, baseline)) Fail("tree_disappeared_without_actual_axe_use");
                        Step(new InputState());
                        if (!Contains(_current.Gameplay.GoneTreeTargets, tree)) Fail("gone_tree_confirmation_not_retained");
                        Succeed("observed_gone_tree_after_actual_axe_use");
                        return baseline;
                    }
                    if (!Contains(_current.Gameplay.TreeTargets, tree))
                    {
                        // Occlusion, darkness or a truncated list cannot prove
                        // that the tree was cut. Stop the tool until reobserved.
                        Step(new InputState());
                        if (_skillTimer.ElapsedMilliseconds - lastWoodProgress >= 8000) Fail("tree_visibility_lost_without_gone_confirmation");
                        continue;
                    }
                    int axe = _current.Gameplay.AxeSlot;
                    // Release normally for one 50ms period every 750ms. Vanilla
                    // item controls decide auto-reuse; releaseUseItem is never written.
                    bool use = _skillTimer.ElapsedMilliseconds % 800 < 750;
                    Step(new InputState { SelectedSlot = axe, AimTileX = tree.TileX, AimTileY = tree.TileY, UseItem = use });
                    if (Newer(_current, baseline) && _current.Inputs != null && _current.Inputs.UseItem &&
                        _current.Gameplay.SelectedSlot == axe) actualAxeUse = true;
                    if (_current.Gameplay.Wood > greatestWood)
                    {
                        greatestWood = _current.Gameplay.Wood;
                        lastWoodProgress = _skillTimer.ElapsedMilliseconds;
                    }
                    if (Contains(_current.Gameplay.TreeTargets, tree) &&
                        _skillTimer.ElapsedMilliseconds - lastWoodProgress >= 8000) Fail("no_backpack_wood_progress_8s");
                }
                Fail("chop_timeout_30s");
                return baseline;
            }

            private void Collect(VisibleTarget tree, OwnObservation beforeChop)
            {
                Begin("collect_wood", "requires actual Wood increase and at least 10 for Workbench; 15s total");
                _target = tree;
                Approach(tree, 32, _skillTimer, 15000);
                while (_skillTimer.ElapsedMilliseconds < 15000)
                {
                    if (_current.Gameplay.Wood > beforeChop.Gameplay.Wood && _current.Gameplay.Wood >= 10)
                    {
                        OwnObservation ready = _current;
                        Step(new InputState());
                        if (!Newer(_current, ready)) continue;
                        if (_current.Gameplay.Wood <= beforeChop.Gameplay.Wood) Fail("wood_gain_not_retained");
                        if (_current.Gameplay.Wood < 10) Fail("workbench_materials_not_retained");
                        Succeed("actual_backpack_wood_increased_and_workbench_materials_ready", beforeChop);
                        return;
                    }
                    Step(new InputState());
                }
                Fail(_current.Gameplay.Wood <= beforeChop.Gameplay.Wood ?
                    "pickup_timeout_no_actual_wood_gain" : "pickup_timeout_wood_below_10");
            }

            private void Craft()
            {
                Begin("craft_workbench", "requires 10 Wood and normal recipe availability; one craft request only");
                _target = null;
                int stableWood = _current.Gameplay.Wood;
                var settling = Stopwatch.StartNew();
                long lastChange = 0;
                while (settling.ElapsedMilliseconds - lastChange < 500 && settling.ElapsedMilliseconds < 3000)
                {
                    Step(new InputState());
                    if (_current.Gameplay.Wood != stableWood)
                    { stableWood = _current.Gameplay.Wood; lastChange = settling.ElapsedMilliseconds; }
                }
                if (settling.ElapsedMilliseconds - lastChange < 500) Fail("pickup_not_settled_before_craft_3s");
                if (_current.Gameplay.Wood < 10) Fail("insufficient_materials_wood_10");
                var ready = Stopwatch.StartNew();
                while (!_current.Gameplay.CanCraftWorkBench && ready.ElapsedMilliseconds < 3000)
                {
                    Step(new InputState()); // Let normal axe animation finish.
                    if (_current.Gameplay.Wood < 10) Fail("insufficient_materials_wood_10");
                }
                if (!_current.Gameplay.CanCraftWorkBench) Fail("craft_precondition_timeout_3s");
                if (!_current.Gameplay.HasFreeSlot && _current.Gameplay.WorkBenchSlot < 0) Fail("inventory_full");
                if (_craftRequested) Fail("craft_already_requested_no_retry");
                OwnObservation before = _current;
                int wood = before.Gameplay.Wood;
                int benches = before.Gameplay.WorkBenches;
                _craftRequested = true; // Also blocks retry after an ambiguous transport error.
                Step(new InputState { CraftWorkBench = true });
                var resultTimer = Stopwatch.StartNew();
                while (resultTimer.ElapsedMilliseconds < 3000)
                {
                    if (Newer(_current, before) && _current.Gameplay.Wood == wood - 10 &&
                        _current.Gameplay.WorkBenches == benches + 1)
                    {
                        Step(new InputState());
                        // The previous fresh sample already proved exact normal
                        // consumption. Later legitimate pickup may add Wood.
                        if (_current.Gameplay.Wood < wood - 10 || _current.Gameplay.WorkBenches != benches + 1)
                            Fail("craft_material_delta_not_retained");
                        Succeed("actual_wood_minus_10_and_workbench_plus_1", before);
                        return;
                    }
                    Step(new InputState());
                }
                Fail("craft_timeout_without_exact_inventory_delta");
            }

            private void Place()
            {
                Begin("place_workbench", "visible canonical left origin; world placement plus item-minus-1; 5s, two positions maximum");
                RequireSlot(_current.Gameplay.WorkBenchSlot, "workbench_item_missing");
                if (_current.Gameplay.WorkBenches < 1) Fail("workbench_item_missing");
                var excluded = new List<VisibleTarget>();
                for (int attempt = 1; attempt <= 2 && _skillTimer.ElapsedMilliseconds < 5000; ++attempt)
                {
                    _attempts = attempt;
                    _target = Nearest(_current.Gameplay.PlacementTargets, excluded, true);
                    if (_target == null) Fail("no_visible_reachable_placement_target");
                    excluded.Add(CopyTarget(_target));
                    if (Contains(_current.Gameplay.WorkBenchTargets, _target)) Fail("placement_target_already_has_workbench");
                    OwnObservation before = _current;
                    int benches = before.Gameplay.WorkBenches;
                    long attemptDeadline = Math.Min(5000, _skillTimer.ElapsedMilliseconds + 2500);
                    string attemptReason = "placement_timeout";
                    while (_skillTimer.ElapsedMilliseconds < attemptDeadline)
                    {
                        if (Newer(_current, before) && _current.Gameplay.WorkBenches == benches - 1 &&
                            Contains(_current.Gameplay.WorkBenchTargets, _target))
                        {
                            Step(new InputState());
                            if (_current.Gameplay.WorkBenches != benches - 1 || !Contains(_current.Gameplay.WorkBenchTargets, _target))
                                Fail("placement_result_not_retained");
                            Succeed("visible_workbench_at_target_and_inventory_minus_1", before);
                            return;
                        }
                        if (_current.Gameplay.WorkBenches < benches - 1) Fail("unexpected_workbench_consumption");
                        if (!Contains(_current.Gameplay.PlacementTargets, _target))
                        {
                            attemptReason = "target_no_longer_placeable";
                            // A placed object may cease to be a placement target;
                            // sample once before deciding whether this consumed an item.
                            Step(new InputState());
                            if (_current.Gameplay.WorkBenches == benches - 1 && Contains(_current.Gameplay.WorkBenchTargets, _target)) continue;
                            break;
                        }
                        if (!InReach(_target, 64)) { attemptReason = "placement_target_out_of_reach"; break; }
                        RequireSlot(_current.Gameplay.WorkBenchSlot, "workbench_item_missing");
                        Step(new InputState { SelectedSlot = _current.Gameplay.WorkBenchSlot, AimTileX = _target.TileX,
                            AimTileY = _target.TileY, UseItem = true });
                    }
                    Step(new InputState());
                    if (Newer(_current, before) && _current.Gameplay.WorkBenches == benches - 1 &&
                        Contains(_current.Gameplay.WorkBenchTargets, _target))
                    {
                        Succeed("visible_workbench_at_target_and_inventory_minus_1", before);
                        return;
                    }
                    if (_current.Gameplay.WorkBenches != benches)
                        Fail("placement_consumed_item_without_verified_world_result");
                    _record("stage_b_recovery", "place_workbench:attempt=" + attempt + ":" + attemptReason, _current);
                }
                Fail("placement_failed_after_at_most_two_positions");
            }

            private void Approach(VisibleTarget target, int reach, Stopwatch timer, int budgetMs)
            {
                if (!Local(target)) Fail("target_outside_local_range");
                float progressX = _current.X;
                long progressAt = timer.ElapsedMilliseconds;
                long jumpUntil = -1;
                while (timer.ElapsedMilliseconds < budgetMs)
                {
                    CheckCancelled();
                    if (InReach(target, reach)) { Step(new InputState()); return; }
                    if (!Local(target)) Fail("target_became_unreachable");
                    double delta = TileCenterX(target) - (_current.X + 10);
                    if (Math.Abs(delta) <= reach) Fail("target_unreachable_vertically");
                    if (Math.Abs(_current.X - progressX) >= 4)
                    {
                        progressX = _current.X;
                        progressAt = timer.ElapsedMilliseconds;
                    }
                    if (timer.ElapsedMilliseconds - progressAt >= 1000)
                    {
                        if (_jumpRecoveryUsed) Fail("stuck_after_single_jump_recovery");
                        _jumpRecoveryUsed = true;
                        jumpUntil = timer.ElapsedMilliseconds + 150;
                        progressAt = timer.ElapsedMilliseconds;
                        _record("stage_b_recovery", _skill + ":one_stuck_jump", _current);
                    }
                    Step(new InputState { Left = delta < 0, Right = delta > 0, Jump = timer.ElapsedMilliseconds < jumpUntil });
                }
                Fail("approach_or_pickup_timeout_15s");
            }

            private void Step(InputState input)
            {
                CheckCancelled();
                CheckFresh();
                var cycle = Stopwatch.StartNew();
                long requestedAt = _clock.ElapsedMilliseconds;
                OwnObservation acknowledgement = _client.Act(_current, input);
                if (_clock.ElapsedMilliseconds - requestedAt > ProtocolLimits.MaxObservationAgeMs) Fail("action_reply_too_old");
                // Never promote an immediate Act acknowledgement into a result.
                // It can validate safety, but only Observe advances our state.
                Accept(acknowledgement, false);
                while (cycle.ElapsedMilliseconds < PeriodMs)
                {
                    CheckCancelled();
                    Thread.Sleep((int)Math.Min(10, PeriodMs - cycle.ElapsedMilliseconds));
                }
                Observe();
                _record("stage_b_action_result", _skill + ":actual_sample_after_action", _current);
            }

            private void Observe()
            {
                CheckCancelled();
                long requestedAt = _clock.ElapsedMilliseconds;
                OwnObservation sample = _client.Observe();
                if (_clock.ElapsedMilliseconds - requestedAt > ProtocolLimits.MaxObservationAgeMs) Fail("observation_reply_too_old");
                Accept(sample);
                CheckFresh();
            }

            private void Accept(OwnObservation sample, bool update = true)
            {
                // An unsafe newly received sample invalidates cleanup permission,
                // even when the previous accepted sample still said Agent.
                _cleanupAllowed = false;
                if (sample == null) Fail("missing_observation");
                if (sample.Menu || sample.Dead || sample.TextInput || sample.GamePaused || sample.OptionsOpen ||
                    string.IsNullOrEmpty(sample.WorldId)) Fail("unsafe_world_menu_death_or_text_input");
                if (sample.Health < 25) Fail("unsafe_low_health");
                if (_runStartHealth < 0) _runStartHealth = sample.Health;
                if (_runStartHealth - sample.Health >= 20) Fail("resource_task_stopped_after_health_loss_20");
                if (sample.ControlState != "Agent") Fail("control_lost_no_automatic_rearm");
                if (sample.Sequence <= 0 || sample.GameTick <= 0 || sample.MonotonicMs < 0 ||
                    float.IsNaN(sample.X) || float.IsInfinity(sample.X) || float.IsNaN(sample.Y) || float.IsInfinity(sample.Y))
                    Fail("invalid_observation");
                if (_world == null) _world = sample.WorldId;
                if (sample.WorldId != _world) Fail("world_changed");
                if (sample.Gameplay == null) Fail("gameplay_observation_not_enabled");
                GameplayObservation gameplay = sample.Gameplay;
                if (gameplay.Wood < 0 || gameplay.WorkBenches < 0 || gameplay.AxeSlot < -1 || gameplay.AxeSlot >= InventorySlots ||
                    gameplay.WorkBenchSlot < -1 || gameplay.WorkBenchSlot >= InventorySlots || gameplay.SelectedSlot < 0 || gameplay.SelectedSlot >= InventorySlots)
                    Fail("invalid_gameplay_observation");
                ValidateTargets(gameplay.TreeTargets);
                ValidateTargets(gameplay.PlacementTargets);
                ValidateTargets(gameplay.WorkBenchTargets);
                ValidateTargets(gameplay.GoneTreeTargets);
                if (_current != null && (sample.Sequence < _current.Sequence || sample.GameTick < _current.GameTick ||
                    sample.MonotonicMs < _current.MonotonicMs)) Fail("observation_regressed");
                _cleanupAllowed = true;
                if (!update) return;
                if (_current != null && sample.Sequence == _current.Sequence && sample.GameTick == _current.GameTick &&
                    sample.MonotonicMs == _current.MonotonicMs) return;
                if (_current == null || Newer(sample, _current)) _lastAdvanceMs = _clock.ElapsedMilliseconds;
                _current = sample;
            }

            private void CheckFresh()
            {
                if (_current == null || _clock.ElapsedMilliseconds - _lastAdvanceMs > ProtocolLimits.MaxObservationAgeMs)
                    Fail("observation_stalled");
            }

            private void CheckCancelled()
            {
                if (_cancelled()) throw new OperationCanceledException("cancelled");
            }

            private void Begin(string skill, string preconditions)
            {
                _skill = skill; _skillTimer = Stopwatch.StartNew(); _skillStart = _current; _attempts = 1;
                _record("stage_b_skill_start", skill + ":" + preconditions, _current);
            }

            private void Succeed(string reason, OwnObservation baseline = null)
            {
                StageBTaskResult result = Result("success", reason, baseline ?? _skillStart);
                _results.Add(result);
                _record("stage_b_skill_result", _skill + ":success:" + reason, _current);
            }

            private StageBTaskResult Result(string status, string reason, OwnObservation before)
            {
                return new StageBTaskResult { Skill = _skill, Status = status, Reason = reason,
                    ElapsedMs = _skillTimer == null ? 0 : _skillTimer.ElapsedMilliseconds, Attempts = _attempts,
                    StartSequence = before == null ? 0 : before.Sequence, EndSequence = _current == null ? 0 : _current.Sequence,
                    WoodBefore = before == null || before.Gameplay == null ? 0 : before.Gameplay.Wood,
                    WoodAfter = _current == null || _current.Gameplay == null ? 0 : _current.Gameplay.Wood,
                    WorkBenchesBefore = before == null || before.Gameplay == null ? 0 : before.Gameplay.WorkBenches,
                    WorkBenchesAfter = _current == null || _current.Gameplay == null ? 0 : _current.Gameplay.WorkBenches,
                    TileX = _target == null ? -1 : _target.TileX, TileY = _target == null ? -1 : _target.TileY };
            }

            private void TryRecord(string name, string detail)
            {
                try { _record(name, detail, _current); } catch { /* Preserve original failure and cease actions. */ }
            }

            private static void Fail(string reason) { throw new InvalidOperationException(reason); }
            private static bool Newer(OwnObservation sample, OwnObservation before)
            {
                return sample != null && before != null && sample.Sequence > before.Sequence &&
                    sample.GameTick > before.GameTick && sample.MonotonicMs > before.MonotonicMs;
            }
            private static void RequireSlot(int slot, string reason)
            {
                if (slot < 0 || slot >= InventorySlots) Fail(reason);
            }
            private static void ValidateTargets(VisibleTarget[] targets)
            {
                if (targets == null) return;
                if (targets.Length > MaximumTargets) Fail("too_many_visible_targets");
                foreach (VisibleTarget target in targets)
                    if (target == null || target.TileX < 0 || target.TileY < 0 || target.TileX > 1000000 || target.TileY > 1000000)
                        Fail("invalid_visible_target");
            }
            private bool Local(VisibleTarget target)
            {
                return target != null && Math.Abs(TileCenterX(target) - (_current.X + 10)) <= 640 &&
                    Math.Abs(TileCenterY(target) - (_current.Y + 21)) <= 96;
            }
            private bool InReach(VisibleTarget target, int pixels)
            {
                return Local(target) && Math.Abs(TileCenterX(target) - (_current.X + 10)) <= pixels;
            }
            private VisibleTarget Nearest(VisibleTarget[] targets, List<VisibleTarget> excluded, bool placement)
            {
                VisibleTarget nearest = null;
                double bestDistance = double.MaxValue;
                if (targets == null) return null;
                foreach (VisibleTarget target in targets)
                {
                    if (!Local(target) || (placement && !InReach(target, 64)) ||
                        (excluded != null && excluded.Exists(item => Same(item, target))) ||
                        (placement && Contains(_current.Gameplay.WorkBenchTargets, target))) continue;
                    double distance = Math.Abs(TileCenterX(target) - (_current.X + 10));
                    if (distance < bestDistance) { bestDistance = distance; nearest = CopyTarget(target); }
                }
                return nearest;
            }
            private static bool Contains(VisibleTarget[] targets, VisibleTarget target)
            {
                if (targets == null || target == null) return false;
                foreach (VisibleTarget item in targets) if (Same(item, target)) return true;
                return false;
            }
            private static bool Same(VisibleTarget first, VisibleTarget second)
            {
                return first != null && second != null && first.TileX == second.TileX && first.TileY == second.TileY;
            }
            private static VisibleTarget CopyTarget(VisibleTarget target)
            {
                return new VisibleTarget { TileX = target.TileX, TileY = target.TileY };
            }
            private static double TileCenterX(VisibleTarget target) { return target.TileX * 16.0 + 8; }
            private static double TileCenterY(VisibleTarget target) { return target.TileY * 16.0 + 8; }
        }
    }
}
