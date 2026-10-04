using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using TerrariaAgent.Protocol;

namespace TerrariaAgent.Controller
{
    public sealed class StageCTaskResult
    {
        public string Skill { get; set; }
        public string Status { get; set; }
        public string Reason { get; set; }
        public long ElapsedMs { get; set; }
        public int Attempts { get; set; }
        public long StartSequence { get; set; }
        public long EndSequence { get; set; }
        public int TileX { get; set; } = -1;
        public int TileY { get; set; } = -1;
        public int WoodBefore { get; set; }
        public int WoodAfter { get; set; }
        public int StoneBefore { get; set; }
        public int StoneAfter { get; set; }
        public int DirtBefore { get; set; }
        public int DirtAfter { get; set; }
        public int ProductBefore { get; set; }
        public int ProductAfter { get; set; }
        public int HealthBefore { get; set; }
        public int HealthAfter { get; set; }
        public int HealthTarget { get; set; }
        public string StepLeftReason { get; set; } = "unobserved";
        public string StepRightReason { get; set; } = "unobserved";
        public bool KillVerified { get; set; }
    }

    public sealed class StageCFailure : InvalidOperationException
    {
        public IReadOnlyList<StageCTaskResult> Results { get; private set; }
        internal StageCFailure(string reason, List<StageCTaskResult> results, Exception inner)
            : base(reason, inner) { Results = results.AsReadOnly(); }
    }

    // Rule mode only. Existing explicit control is required; this class never
    // arms, reconnects, reads a map, or treats an Act acknowledgement as proof.
    public static class StageC
    {
        public static List<StageCTaskResult> Run(IStageBClient client, Func<bool> isCancelled,
            Action<string, string, OwnObservation> record, string mode, int seconds = 30,
            string recipe = GameplayRecipeIds.WoodenBow, int healthTarget = 60)
        {
            if (client == null) throw new ArgumentNullException("client");
            if (isCancelled == null) throw new ArgumentNullException("isCancelled");
            if (record == null) throw new ArgumentNullException("record");
            if (mode != "stone-test" && mode != "dig-test" && mode != "collect-soil" && mode != "craft-test" && mode != "platform-test" && mode != "torch-test" &&
                mode != "combat-trial" && mode != "recover-health" && mode != "seek-stone" && mode != "forage-stone" && mode != "prepare") throw new ArgumentException("unsupported_stage_c_mode");
            if (seconds < 1 || seconds > 120) throw new ArgumentException("combat_duration_must_be_1_to_120_seconds");
            if (!GameplayRecipeIds.IsKnown(recipe)) throw new ArgumentException("unsupported_craft_recipe");
            ValidateHealthTarget(mode, healthTarget);
            return new Runner(client, isCancelled, record).Run(mode, seconds, recipe, healthTarget);
        }

        public static void ValidateHealthTarget(string mode, int healthTarget)
        {
            if (healthTarget < 60 || healthTarget > 100)
                throw new ArgumentException("health_target_must_be_60_to_100");
            if (mode != "recover-health" && healthTarget != 60)
                throw new ArgumentException("health_target_only_for_recover_health");
        }

        private sealed class Runner
        {
            private const int PeriodMs = 50;
            private readonly IStageBClient _client;
            private readonly Func<bool> _cancelled;
            private readonly Action<string, string, OwnObservation> _record;
            private readonly List<StageCTaskResult> _results = new List<StageCTaskResult>();
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private OwnObservation _current, _skillStart;
            private string _world, _skill = "observe", _product;
            private Stopwatch _skillClock;
            private VisibleTarget _target;
            private long _lastAdvance;
            private int _attempts = 1;
            private int _healthTarget;
            private int _soilApproachRecoveries;
            private bool _cleanupAllowed;
            private bool _lowHealthRecoveryOnly;
            private Stopwatch _forageClock;
            private OwnObservation _forageStart, _foragePrevious;
            private OwnObservation _lastUpObservation;
            private double _forageTravelX;

            internal Runner(IStageBClient client, Func<bool> cancelled, Action<string, string, OwnObservation> record)
            { _client = client; _cancelled = cancelled; _record = record; }

            internal List<StageCTaskResult> Run(string mode, int seconds, string recipe, int healthTarget)
            {
                // Fixed before the first Observe. This exception admits only
                // stationary normal defense/regeneration, never another mode.
                _lowHealthRecoveryOnly = mode == "recover-health";
                try
                {
                    Begin("observe", "enabled gameplay; existing Agent control; fresh advancing world observation");
                    Observe();
                    OwnObservation first = _current;
                    while (!Newer(_current, first) && _skillClock.ElapsedMilliseconds < 1000) Step(new InputState());
                    if (!Newer(_current, first)) Fail("observation_not_advancing");
                    Complete("success", "fresh_gameplay_observation");
                    if (mode == "stone-test") MineStone();
                    else if (mode == "dig-test") DigSoil();
                    else if (mode == "collect-soil") CollectSoil();
                    else if (mode == "craft-test") Craft(recipe);
                    else if (mode == "torch-test")
                    {
                        if (_current.Gameplay.Torches == 0) Craft(GameplayRecipeIds.Torch);
                        Place(true);
                    }
                    else if (mode == "platform-test")
                    {
                        if (_current.Gameplay.WoodPlatforms == 0) Craft(GameplayRecipeIds.WoodPlatform);
                        Place(false);
                    }
                    else if (mode == "combat-trial") Combat(seconds);
                    else if (mode == "recover-health") RecoverHealth(seconds, healthTarget);
                    else if (mode == "seek-stone") SeekStone(Math.Min(seconds, 15));
                    else if (mode == "forage-stone") ForageStone();
                    else Prepare();
                    return _results;
                }
                catch (Exception error)
                {
                    string status = error is OperationCanceledException ? "cancelled" : "failure";
                    _results.Add(Result(status, error.Message, _skillStart));
                    TryRecord("stage_c_skill_result", _skill + ":" + status + ":" + error.Message);
                    throw new StageCFailure(_skill + ":" + error.Message, _results, error);
                }
                finally
                {
                    if (_cleanupAllowed && _current != null && _current.ControlState == "Agent" &&
                        !_current.Menu && !_current.Dead && !_current.TextInput && !_current.GamePaused &&
                        !_current.OptionsOpen && _current.Health > 0 &&
                        (_current.Health >= 25 || _lowHealthRecoveryOnly) && _current.WorldId == _world &&
                        _clock.ElapsedMilliseconds - _lastAdvance <= ProtocolLimits.MaxObservationAgeMs)
                    {
                        try { _client.Act(_current, new InputState()); }
                        catch { /* Caller Stop and independent lease expiry remain mandatory. */ }
                    }
                }
            }

            private void Prepare()
            {
                // A small kit, not a Boss-readiness or exploration certificate.
                // Initial B supplies wood/table; missing resources fail explicitly.
                int mined = 0;
                while (_current.Gameplay.Stone < 2 && mined++ < 2) MineStone();
                if (_current.Gameplay.Stone < 2) Fail("prepare_stone_target_not_met");
                if (_current.Gameplay.WoodenBows == 0) Craft(GameplayRecipeIds.WoodenBow);
                if (_current.Gameplay.WoodenSwords == 0) Craft(GameplayRecipeIds.WoodenSword);
                for (int batch = 0; _current.Gameplay.WoodenArrows < 50 && batch < 2; ++batch)
                    Craft(GameplayRecipeIds.WoodenArrow);
                if (_current.Gameplay.WoodenArrows < 50) Fail("prepare_arrow_target_not_met");
                for (int batch = 0; _current.Gameplay.WoodPlatforms < 6 && batch < 3; ++batch)
                    Craft(GameplayRecipeIds.WoodPlatform);
                if (_current.Gameplay.WoodPlatforms < 6) Fail("prepare_platform_target_not_met");
                Place(false);
                if (_current.Gameplay.Torches == 0 && _current.Gameplay.Gel > 0) Craft(GameplayRecipeIds.Torch);
                if (_current.Gameplay.Torches > 0) Place(true);
                else
                {
                    Begin("torch", "normal Gel needed; optional step cannot invent materials");
                    Complete("skipped", "insufficient_materials_gel;lighting_not_verified");
                }
                Begin("kit", "weapons must be normally selectable in the hotbar");
                RequireHotbar(_current.Gameplay.BowSlot, "bow_not_in_hotbar_or_unsupported");
                RequireHotbar(_current.Gameplay.SwordSlot, "sword_not_in_hotbar_or_unsupported");
                Complete("success", "small_kit_observed;armor_arena_and_boss_readiness_not_verified");
            }

            private void MineStone()
            {
                Begin("mine_stone", "visible exposed Stone; normal pickaxe; two targets maximum; 30s total");
                _product = "stone";
                RequireHotbar(_current.Gameplay.PickaxeSlot, "pickaxe_not_in_hotbar_or_missing");
                if (!_current.Gameplay.HasFreeSlot) Fail("inventory_full");
                var excluded = new List<VisibleTarget>();
                for (int attempt = 1; attempt <= 2 && _skillClock.ElapsedMilliseconds < 30000; ++attempt)
                {
                    _attempts = attempt;
                    _target = Nearest(_current.Gameplay.StoneTargets, excluded, false);
                    if (_target == null) Fail("no_visible_local_stone_target");
                    VisibleTarget target = _target.Copy();
                    excluded.Add(target);
                    Approach(target, 64, () => Contains(_current.Gameplay.StoneTargets, target));
                    OwnObservation before = _current;
                    long started = _skillClock.ElapsedMilliseconds;
                    bool actualPick = false, gone = false;
                    while (_skillClock.ElapsedMilliseconds < 30000 && _skillClock.ElapsedMilliseconds - started < 15000)
                    {
                        RequireHotbar(_current.Gameplay.PickaxeSlot, "pickaxe_not_in_hotbar_or_missing");
                        gone = Contains(_current.Gameplay.GoneStoneTargets, target);
                        if (gone && actualPick && Newer(_current, before) && _current.Gameplay.Stone > before.Gameplay.Stone)
                        {
                            OwnObservation ready = _current;
                            Step(new InputState());
                            if (!Newer(_current, ready)) continue;
                            if (!Contains(_current.Gameplay.GoneStoneTargets, target) ||
                                _current.Gameplay.Stone <= before.Gameplay.Stone) Fail("stone_result_not_retained");
                            Complete("success", "actual_pick_use_visible_stone_gone_and_backpack_stone_increased", before);
                            return;
                        }
                        if (gone)
                        {
                            if (!actualPick) Fail("stone_gone_without_actual_pick_use");
                            // Walking near the previously visible drop location is
                            // normal pickup. We do not call a pickup/grant function.
                            double dx = CenterX(target) - (_current.X + 10);
                            Step(new InputState { Left = dx < -24, Right = dx > 24 });
                            continue;
                        }
                        if (!Contains(_current.Gameplay.StoneTargets, target))
                        {
                            Step(new InputState());
                            if (_skillClock.ElapsedMilliseconds - started >= 1500) break;
                            continue;
                        }
                        if (!InReach(target, 80)) Fail("stone_out_of_reach");
                        bool use = (_skillClock.ElapsedMilliseconds - started) % 800 < 700;
                        int slot = _current.Gameplay.PickaxeSlot;
                        Step(new InputState { SelectedSlot = slot, AimTileX = target.TileX, AimTileY = target.TileY, UseItem = use });
                        if (Newer(_current, before) && _current.Inputs != null && _current.Inputs.UseItem &&
                            _current.Gameplay.SelectedSlot == slot) actualPick = true;
                        if (_skillClock.ElapsedMilliseconds - started >= 8000 && _current.Gameplay.Stone <= before.Gameplay.Stone)
                            break;
                    }
                    Step(new InputState());
                    if (actualPick && Newer(_current, before) && Contains(_current.Gameplay.GoneStoneTargets, target) &&
                        _current.Gameplay.Stone > before.Gameplay.Stone)
                    {
                        OwnObservation ready = _current;
                        Step(new InputState());
                        if (!Newer(_current, ready) || !Contains(_current.Gameplay.GoneStoneTargets, target) ||
                            _current.Gameplay.Stone <= before.Gameplay.Stone) Fail("stone_result_not_retained");
                        Complete("success", "actual_pick_use_visible_stone_gone_and_backpack_stone_increased", before);
                        return;
                    }
                    if (Contains(_current.Gameplay.GoneStoneTargets, target) || _current.Gameplay.Stone != before.Gameplay.Stone)
                        Fail("stone_world_or_inventory_change_without_complete_confirmation");
                    _record("stage_c_recovery", "mine_stone:target=" + attempt + ":no_progress_or_visibility_lost", _current);
                }
                Fail("stone_failed_after_at_most_two_visible_targets");
            }

            private void DigSoil(List<VisibleTarget> previouslyDug = null)
            {
                Begin("dig_soil", "one currently visible side soil cell within 128px; proven-ground approach; normal pickaxe; at most two candidates; 10s total");
                _product = "dirt";
                RequireHotbar(_current.Gameplay.PickaxeSlot, "pickaxe_not_in_hotbar_or_missing");
                if (!_current.Gameplay.HasFreeSlot) Fail("inventory_full");
                var excluded = new List<VisibleTarget>();
                if (previouslyDug != null)
                    foreach (VisibleTarget dug in previouslyDug) excluded.Add(dug.Copy());
                for (int attempt = 1; attempt <= 2 && _skillClock.ElapsedMilliseconds < 10000; ++attempt)
                {
                    CheckDigContext();
                    _attempts = attempt;
                    _target = Nearest(_current.Gameplay.DirtTargets, excluded, true, 128);
                    if (_target == null) Fail("no_visible_reachable_soil_target");
                    VisibleTarget target = _target.Copy();
                    excluded.Add(target);
                    Approach(target, 64, () => Contains(_current.Gameplay.DirtTargets, target), CheckDigContext, true);
                    CheckDigContext();
                    OwnObservation before = _current;
                    bool actualPick = false, pickupAttempted = false;
                    long started = _skillClock.ElapsedMilliseconds;
                    long end = Math.Min(10000, started + 4500);
                    while (_skillClock.ElapsedMilliseconds < end)
                    {
                        CheckDigContext();
                        bool gone = Contains(_current.Gameplay.GoneDirtTargets, target);
                        if (gone && actualPick && Newer(_current, before) && _current.Gameplay.Dirt > before.Gameplay.Dirt)
                        {
                            ConfirmDig(target, before);
                            return;
                        }
                        if (gone)
                        {
                            if (!actualPick) Fail("soil_gone_without_actual_pick_use");
                            if (!pickupAttempted)
                            {
                                pickupAttempted = true;
                                PickupSoil(target, before);
                            }
                            Step(new InputState());
                            continue;
                        }
                        if (!Contains(_current.Gameplay.DirtTargets, target)) { Step(new InputState()); break; }
                        if (!InReach(target, 64)) Fail("soil_target_out_of_normal_local_reach");
                        int slot = _current.Gameplay.PickaxeSlot;
                        RequireHotbar(slot, "pickaxe_not_in_hotbar_or_missing");
                        OwnObservation beforeAction = _current;
                        Step(new InputState { SelectedSlot = slot, AimTileX = target.TileX, AimTileY = target.TileY,
                            UseItem = (_skillClock.ElapsedMilliseconds - started) % 800 < 700 });
                        CheckDigContext();
                        if (Newer(_current, beforeAction) && _current.Inputs != null && _current.Inputs.UseItem &&
                            _current.Gameplay.SelectedSlot == slot) actualPick = true;
                    }
                    Step(new InputState());
                    CheckDigContext();
                    if (actualPick && Newer(_current, before) && Contains(_current.Gameplay.GoneDirtTargets, target) &&
                        _current.Gameplay.Dirt > before.Gameplay.Dirt)
                    { ConfirmDig(target, before); return; }
                    if (Contains(_current.Gameplay.GoneDirtTargets, target) || _current.Gameplay.Dirt != before.Gameplay.Dirt)
                        Fail("soil_world_or_inventory_change_without_complete_confirmation");
                    _record("stage_c_recovery", "dig_soil:attempt=" + attempt + ":no_progress_or_visibility_lost", _current);
                }
                Fail("dig_timeout_or_no_progress_after_at_most_two_candidates_10s");
            }

            private void CollectSoil()
            {
                Begin("collect_soil", "currently visible remembered gone soil; one bounded nearby pickup attempt; no mining");
                _product = "dirt";
                if (!_current.Gameplay.HasFreeSlot) Fail("inventory_full");
                _target = Nearest(_current.Gameplay.GoneDirtTargets, null, true);
                if (_target == null) Fail("no_currently_visible_remembered_gone_soil");
                OwnObservation before = _current;
                PickupSoil(_target, before);
                OwnObservation ready = _current;
                Step(new InputState());
                CheckSoilPickup(_target, before.X, before.Y);
                if (!Newer(_current, ready) || !Contains(_current.Gameplay.GoneDirtTargets, _target) ||
                    _current.Gameplay.Dirt <= before.Gameplay.Dirt)
                    Fail("normal_soil_pickup_not_confirmed_or_retained");
                Complete("success", "normal_backpack_dirt_increase_at_previously_visible_gone_soil;no_new_mining_claimed", before);
            }

            private void PickupSoil(VisibleTarget target, OwnObservation before)
            {
                // A remembered removed cell is a pickup location only. Every
                // short step additionally needs fresh proven ground; an unknown
                // route permits only neutral waiting for normal item attraction.
                // Check again after every sample, including a pickup sample:
                // collecting the item must not bypass the movement bounds.
                float startX = _current.X, startY = _current.Y;
                var pickup = Stopwatch.StartNew();
                bool lastStepMoved = false;
                while (pickup.ElapsedMilliseconds < 1800 && _current.Gameplay.Dirt <= before.Gameplay.Dirt)
                {
                    CheckSoilPickup(target, startX, startY);
                    double dx = CenterX(target) - (_current.X + 10);
                    bool left = dx < -24 && _current.Gameplay.CanStepLeft;
                    bool right = dx > 24 && _current.Gameplay.CanStepRight;
                    // Release between pulses and stop issuing movement early
                    // enough to leave part of the 24px budget for inertia. The
                    // actual sample must still obey the full bound afterward.
                    bool move = !lastStepMoved && Math.Abs(_current.X - startX) < 16 && (left || right);
                    Step(move ? new InputState { Left = left, Right = right } : new InputState());
                    lastStepMoved = move;
                    CheckSoilPickup(target, startX, startY);
                }
                Step(new InputState());
                CheckSoilPickup(target, startX, startY);
            }

            private void CheckSoilPickup(VisibleTarget target, float startX, float startY)
            {
                CheckDigContext();
                if (!Contains(_current.Gameplay.GoneDirtTargets, target)) Fail("soil_pickup_location_no_longer_visible");
                if (Math.Abs(_current.X - startX) > 24 || _current.Y - startY > 20)
                    Fail("soil_pickup_step_exceeded_local_safety_bound");
            }

            private void ConfirmDig(VisibleTarget target, OwnObservation before)
            {
                OwnObservation ready = _current;
                Step(new InputState());
                CheckDigContext();
                if (!Newer(_current, ready) || !Contains(_current.Gameplay.GoneDirtTargets, target) ||
                    _current.Gameplay.Dirt <= before.Gameplay.Dirt) Fail("soil_result_not_retained_in_fresh_observation");
                Complete("success", "actual_pick_use_visible_soil_gone_and_backpack_dirt_increased;stone_not_verified", before);
            }

            private void CheckDigHealth()
            {
                if (_current.Health < 25) Fail("dig_stopped_low_health");
                if (_skillStart != null && _skillStart.Health - _current.Health >= 20)
                    Fail("dig_stopped_after_health_loss_20");
            }

            private void CheckDigContext()
            {
                CheckDigHealth();
                if (_skill == "dig_soil" && _skillClock.ElapsedMilliseconds >= 10000)
                    Fail("dig_total_budget_10s");
                foreach (VisibleEnemy enemy in _current.Gameplay.Enemies ?? new VisibleEnemy[0])
                    if (Math.Abs(enemy.X - (_current.X + 10)) < 112 && Math.Abs(enemy.Y - (_current.Y + 21)) < 96)
                        Fail("soil_stopped_for_nearby_visible_enemy");
            }

            private void Craft(string recipe)
            {
                Begin("craft_" + recipe, "normal whitelist recipe; materials/environment/free slot; one request without retry");
                _product = recipe;
                int wood = WoodCost(recipe), stone = recipe == GameplayRecipeIds.WoodenArrow ? 1 : 0;
                int gel = recipe == GameplayRecipeIds.Torch ? 1 : 0;
                if (_current.Gameplay.Wood < wood || _current.Gameplay.Stone < stone || _current.Gameplay.Gel < gel)
                    Fail("insufficient_materials_" + recipe);
                if (!_current.Gameplay.HasFreeSlot) Fail("inventory_full");
                if (NeedsTable(recipe) && !CanCraft(recipe))
                {
                    VisibleTarget table = Nearest(_current.Gameplay.WorkBenchTargets, null, false);
                    if (table == null) Fail("station_missing_visible_workbench");
                    _target = table;
                    Approach(table, 48, () => Contains(_current.Gameplay.WorkBenchTargets, table));
                }
                var qualification = Stopwatch.StartNew();
                while (!CanCraft(recipe) && qualification.ElapsedMilliseconds < 3000) Step(new InputState());
                if (!CanCraft(recipe)) Fail("normal_recipe_not_available_" + recipe);
                var settling = Stopwatch.StartNew();
                long lastChange = 0;
                GameplayObservation stable = _current.Gameplay.Copy();
                while (settling.ElapsedMilliseconds - lastChange < 500 && settling.ElapsedMilliseconds < 3000)
                {
                    Step(new InputState());
                    GameplayObservation now = _current.Gameplay;
                    if (now.Wood != stable.Wood || now.Stone != stable.Stone || now.Gel != stable.Gel ||
                        ProductCount(now, recipe) != ProductCount(stable, recipe))
                    { stable = now.Copy(); lastChange = settling.ElapsedMilliseconds; }
                }
                if (settling.ElapsedMilliseconds - lastChange < 500) Fail("pickup_not_settled_before_craft_3s");
                if (!CanCraft(recipe) || _current.Gameplay.Wood < wood || _current.Gameplay.Stone < stone ||
                    _current.Gameplay.Gel < gel || !_current.Gameplay.HasFreeSlot) Fail("craft_preconditions_changed_" + recipe);
                OwnObservation before = _current;
                Step(new InputState { CraftRecipe = recipe }); // Exactly once, including ambiguous transport failures.
                var wait = Stopwatch.StartNew();
                while (wait.ElapsedMilliseconds < 3000)
                {
                    if (Newer(_current, before) && CraftDelta(before.Gameplay, _current.Gameplay, recipe))
                    {
                        OwnObservation ready = _current;
                        Step(new InputState());
                        if (!Newer(_current, ready)) continue;
                        if (!RetainedCraftDelta(before.Gameplay, _current.Gameplay, recipe)) Fail("craft_result_not_retained_" + recipe);
                        Complete("success", "normal_material_consumption_and_product_delta_" + recipe, before);
                        return;
                    }
                    Step(new InputState());
                }
                Fail("craft_timeout_no_exact_delta_" + recipe);
            }

            private void Place(bool torch)
            {
                Begin(torch ? "place_torch" : "place_platform", "visible reachable empty target; normal item; inventory-minus-one and world object");
                _product = torch ? GameplayRecipeIds.Torch : GameplayRecipeIds.WoodPlatform;
                RequireHotbar(PlaceSlot(torch), torch ? "torch_not_in_hotbar_or_missing" : "platform_not_in_hotbar_or_missing");
                if (PlaceCount(torch) < 1) Fail("placement_item_missing");
                if (torch)
                {
                    // Holding an existing torch may normally illuminate nearby
                    // space. Wait for actual advancing samples before choosing
                    // a target; selecting a slot is not proof of illumination.
                    int heldSlot = PlaceSlot(true), heldSamples = 0;
                    while (heldSamples < 3 && _skillClock.ElapsedMilliseconds < 5000)
                    {
                        CheckPlacementHealth();
                        RequireHotbar(PlaceSlot(true), "torch_not_in_hotbar_or_missing");
                        if (PlaceSlot(true) != heldSlot || PlaceCount(true) < 1)
                            Fail("torch_changed_before_visible_placement");
                        OwnObservation previous = _current;
                        Step(new InputState { SelectedSlot = heldSlot });
                        CheckPlacementHealth();
                        if (Newer(_current, previous) && _current.Gameplay.SelectedSlot == heldSlot)
                            ++heldSamples;
                    }
                    if (heldSamples < 3) Fail("torch_hold_not_retained_in_fresh_samples");
                }
                var excluded = new List<VisibleTarget>();
                for (int attempt = 1; attempt <= 2 && _skillClock.ElapsedMilliseconds < 5000; ++attempt)
                {
                    CheckPlacementHealth();
                    _attempts = attempt;
                    _target = Nearest(PlaceCandidates(torch), excluded, true);
                    if (_target == null) Fail("no_visible_reachable_placement_target");
                    VisibleTarget target = _target.Copy();
                    excluded.Add(target);
                    if (Contains(PlacedTargets(torch), target)) Fail("placement_target_already_occupied");
                    OwnObservation before = _current;
                    int count = PlaceCount(torch);
                    long end = Math.Min(5000, _skillClock.ElapsedMilliseconds + 2200);
                    while (_skillClock.ElapsedMilliseconds < end)
                    {
                        CheckPlacementHealth();
                        if (Newer(_current, before) && PlaceCount(torch) == count - 1 && Contains(PlacedTargets(torch), target))
                        {
                            OwnObservation ready = _current;
                            Step(new InputState());
                            CheckPlacementHealth();
                            if (!Newer(_current, ready)) continue;
                            if (PlaceCount(torch) != count - 1 || !Contains(PlacedTargets(torch), target)) Fail("placement_result_not_retained");
                            Complete("success", "normal_item_minus_one_and_visible_world_object", before);
                            return;
                        }
                        if (PlaceCount(torch) < count - 1) Fail("unexpected_multiple_item_consumption");
                        if (!Contains(PlaceCandidates(torch), target)) { Step(new InputState()); CheckPlacementHealth(); break; }
                        if (!InReach(target, 80)) Fail("placement_target_out_of_reach");
                        int slot = PlaceSlot(torch);
                        RequireHotbar(slot, "placement_item_not_in_hotbar");
                        Step(new InputState { SelectedSlot = slot, AimTileX = target.TileX, AimTileY = target.TileY, UseItem = true });
                    }
                    Step(new InputState());
                    CheckPlacementHealth();
                    if (Newer(_current, before) && PlaceCount(torch) == count - 1 && Contains(PlacedTargets(torch), target))
                    { Complete("success", "normal_item_minus_one_and_visible_world_object", before); return; }
                    if (PlaceCount(torch) != count) Fail("placement_consumed_item_without_visible_world_confirmation");
                    _record("stage_c_recovery", "placement:attempt=" + attempt + ":blocked_or_visibility_lost", _current);
                }
                Fail("placement_failed_after_at_most_two_positions");
            }

            private void CheckPlacementHealth()
            {
                if (_skillStart.Health - _current.Health >= 20)
                    Fail("placement_stopped_after_health_loss_20");
            }

            private void Combat(int seconds)
            {
                Begin("combat_trial", "currently visible enemy; supported hotbar weapon; at least 60 health; bounded encounter");
                if (_current.Health < 60) Fail("combat_requires_health_at_least_60");
                VisibleEnemy enemy = NearestEnemy();
                // A short encounter wait advances normal game time. It observes
                // only current legal targets and never searches hidden NPCs.
                var encounterWait = Stopwatch.StartNew();
                while (enemy == null && encounterWait.ElapsedMilliseconds < 5000)
                {
                    Step(new InputState());
                    if (_current.Health < 60) Fail("combat_wait_stopped_below_60_health");
                    enemy = NearestEnemy();
                }
                if (enemy == null) Fail("no_visible_enemy_after_bounded_5s_wait");
                int id = enemy.Id;
                string kind = enemy.Kind;
                int initialHealth = _current.Health;
                bool actualUse = false;
                float previousX = enemy.X, previousY = enemy.Y;
                while (_skillClock.ElapsedMilliseconds < seconds * 1000L)
                {
                    enemy = FindEnemy(id, kind);
                    if (enemy == null)
                    {
                        // Occlusion, darkness, list truncation, despawn and death
                        // are indistinguishable here. Never continue by cached id.
                        Step(new InputState());
                        if (_current.Health <= 40 || initialHealth - _current.Health >= 20)
                            Fail("encounter_lost_after_damage;combat_not_completed");
                        if (!actualUse) Fail("encounter_lost_without_observed_actual_weapon_use");
                        Complete("success", "encounter_not_currently_visible;kill_unverified");
                        return;
                    }
                    if (Math.Abs(enemy.X - previousX) > 160 || Math.Abs(enemy.Y - previousY) > 160)
                        Fail("enemy_identity_uncertain_after_discontinuous_position");
                    previousX = enemy.X; previousY = enemy.Y;
                    if (_current.Health <= 40 || initialHealth - _current.Health >= 20)
                    {
                        // A short bounded retreat is only an attempt. There is no
                        // terrain planner yet, so stop if it begins to fall.
                        float startY = _current.Y;
                        double away = enemy.X - (_current.X + 10);
                        var retreat = Stopwatch.StartNew();
                        while (retreat.ElapsedMilliseconds < 250)
                        {
                            Step(new InputState { Left = away > 0, Right = away <= 0 });
                            if (_current.Y - startY > 24) break;
                        }
                        Step(new InputState());
                        Fail("bounded_retreat_after_damage;combat_not_completed");
                    }
                    double dx = enemy.X - (_current.X + 10), dy = enemy.Y - (_current.Y + 21);
                    if (Math.Abs(dx) > 640 || Math.Abs(dy) > 256) Fail("visible_enemy_outside_supported_local_combat_range");
                    bool bow = _current.Gameplay.BowSlot >= 0 && _current.Gameplay.WoodenArrows > 0;
                    int slot = bow ? _current.Gameplay.BowSlot : _current.Gameplay.SwordSlot;
                    RequireHotbar(slot, "supported_weapon_not_in_hotbar_or_ammo_missing");
                    bool use = _skillClock.ElapsedMilliseconds % 600 < 450 && (bow || Math.Abs(dx) <= 72);
                    // One encounter only; face/approach through normal movement.
                    // Ranged attacks keep distance and move away if very close.
                    bool left = bow ? Math.Abs(dx) < 112 && dx > 0 : dx < -28;
                    bool right = bow ? Math.Abs(dx) < 112 && dx < 0 : dx > 28;
                    OwnObservation beforeAction = _current;
                    Step(new InputState { SelectedSlot = slot, AimTileX = enemy.TileX, AimTileY = enemy.TileY,
                        UseItem = use, Left = left, Right = right });
                    if (Newer(_current, beforeAction) && _current.Inputs != null && _current.Inputs.UseItem && _current.Gameplay.SelectedSlot == slot)
                        actualUse = true;
                }
                Step(new InputState());
                if (_current.Health <= 40 || initialHealth - _current.Health >= 20)
                    Fail("combat_duration_ended_after_damage;combat_not_completed");
                if (!actualUse) Fail("combat_timeout_without_observed_actual_weapon_use");
                Complete("success", "bounded_attack_trial_completed;hit_and_kill_unverified");
            }

            private void SeekStone(int seconds)
            {
                SearchResources(seconds, null);
            }

            private void ForageStone()
            {
                Begin("forage_stone", "45s global; cumulative horizontal travel 384px/down32px; three side soil cells maximum; normal stone mining required");
                if (_current.Health < 60) Fail("forage_requires_health_60");
                _forageStart = _foragePrevious = _current;
                _forageClock = Stopwatch.StartNew();
                var dug = new List<VisibleTarget>();
                while (true)
                {
                    CheckForage();
                    HoldForageTorch();
                    bool foundStone = SearchResources(15, dug);
                    CheckForage();
                    if (foundStone)
                    {
                        MineStone();
                        OwnObservation ready = _current;
                        Step(new InputState());
                        if (!Newer(_current, ready) || _current.Gameplay.Stone <= _forageStart.Gameplay.Stone)
                            Fail("forage_mined_stone_not_retained_above_initial_inventory");
                        _skill = "forage_stone"; _skillStart = _forageStart; _skillClock = _forageClock;
                        _product = "stone";
                        Complete("success", "normal_pick_visible_stone_gone_and_retained_stone_gain;soil_gain_is_not_stone", _forageStart);
                        return;
                    }
                    if (dug.Count >= 3) Fail("forage_three_soil_cells_exhausted_without_visible_stone");
                    DigSoil(dug);
                    // DigSoil completes only after actual pick use, legally
                    // visible removal and retained own dirt growth. Remember
                    // its coordinate as an exclusion, not hidden terrain.
                    dug.Add(_target.Copy());
                    Step(new InputState());
                }
            }

            private void HoldForageTorch()
            {
                if (_current.Gameplay.Torches <= 0) return;
                int slot = _current.Gameplay.TorchSlot, samples = 0;
                RequireHotbar(slot, "forage_existing_torch_not_in_hotbar");
                while (samples < 3)
                {
                    CheckForage();
                    if (_current.Gameplay.Torches <= 0 || _current.Gameplay.TorchSlot != slot)
                        Fail("forage_torch_changed_during_normal_hold");
                    OwnObservation previous = _current;
                    Step(new InputState { SelectedSlot = slot });
                    if (Newer(_current, previous) && _current.Gameplay.SelectedSlot == slot) ++samples;
                }
            }

            private int SearchSlot(bool forage)
            {
                int slot = forage && _current.Gameplay.Torches > 0 ? _current.Gameplay.TorchSlot : _current.Gameplay.SwordSlot;
                RequireHotbar(slot, forage && _current.Gameplay.Torches > 0 ?
                    "forage_existing_torch_not_in_hotbar" : "normal_hotbar_melee_weapon_required");
                return slot;
            }

            private void CheckForage()
            {
                if (_forageClock == null) return;
                if (_forageClock.ElapsedMilliseconds >= 45000 ||
                    _current.MonotonicMs - _forageStart.MonotonicMs >= 45000)
                    Fail("forage_global_timeout_45s");
                if (Newer(_current, _foragePrevious))
                {
                    _forageTravelX += Math.Abs(_current.X - _foragePrevious.X);
                    _foragePrevious = _current;
                }
                if (_forageTravelX > 384) Fail("forage_global_horizontal_travel_384px");
                if (_current.Y - _forageStart.Y > 32) Fail("forage_global_downward_motion_32px");
                if (_current.Health <= 40 || _forageStart.Health - _current.Health >= 20)
                    Fail("forage_global_health_loss_or_low_health");
                foreach (VisibleEnemy enemy in _current.Gameplay.Enemies ?? new VisibleEnemy[0])
                    if (Math.Abs(enemy.X - (_current.X + 10)) < 112 && Math.Abs(enemy.Y - (_current.Y + 21)) < 96)
                        Fail("forage_stopped_for_nearby_visible_enemy");
            }

            private bool SearchResources(int seconds, List<VisibleTarget> excludedSoil)
            {
                bool forage = excludedSoil != null;
                Begin(forage ? "search_stone_or_soil" : "seek_stone",
                    "current visible ground only; 15s/384px maximum; one direction change; discovery is not mining");
                if (_current.Health < 60) Fail("exploration_requires_health_60");
                SearchSlot(forage);
                float startX = _current.X, startY = _current.Y, progressX = _current.X;
                long progressAt = 0;
                int direction = 1;
                bool reversed = false;
                int settlingAttempts = 0;
                while (_skillClock.ElapsedMilliseconds < seconds * 1000L)
                {
                    CheckExploration(startY);
                    if (forage) _product = "stone";
                    VisibleTarget stone = Nearest(_current.Gameplay.StoneTargets, null, false);
                    VisibleTarget soil = forage && stone == null ? Nearest(_current.Gameplay.DirtTargets, excludedSoil, true, 128) : null;
                    if (stone != null || soil != null)
                    {
                        _target = (stone ?? soil).Copy();
                        OwnObservation ready = _current;
                        Step(forage ? new InputState { SelectedSlot = SearchSlot(true) } : new InputState());
                        CheckExploration(startY);
                        if (!Newer(_current, ready) || !Contains(stone != null ? _current.Gameplay.StoneTargets : _current.Gameplay.DirtTargets, _target))
                            Fail(stone != null ? "visible_stone_discovery_not_retained" : "visible_side_soil_discovery_not_retained");
                        Complete("success", stone != null ? "currently_visible_stone_found;mining_and_pickup_not_verified" :
                            "currently_visible_side_soil_found;mining_and_pickup_not_verified");
                        return stone != null;
                    }
                    if (Math.Abs(_current.X - startX) >= 384) Fail("exploration_distance_budget_no_visible_stone");
                    foreach (VisibleEnemy enemy in _current.Gameplay.Enemies ?? new VisibleEnemy[0])
                        if (Math.Abs(enemy.X - (_current.X + 10)) < 112 && Math.Abs(enemy.Y - (_current.Y + 21)) < 96)
                            Fail("exploration_stopped_for_nearby_visible_enemy");
                    bool safe = direction < 0 ? _current.Gameplay.CanStepLeft : _current.Gameplay.CanStepRight;
                    if (Math.Abs(_current.X - progressX) >= 4)
                    { progressX = _current.X; progressAt = _skillClock.ElapsedMilliseconds; }
                    bool stuck = _skillClock.ElapsedMilliseconds - progressAt >= 1000;
                    if (!safe || stuck)
                    {
                        Step(forage ? new InputState { SelectedSlot = SearchSlot(true) } : new InputState());
                        CheckExploration(startY);
                        // Neutral can settle a step or complete real progress.
                        // Never decide from flags captured before that sample.
                        safe = direction < 0 ? _current.Gameplay.CanStepLeft : _current.Gameplay.CanStepRight;
                        if (Math.Abs(_current.X - progressX) >= 4)
                        { progressX = _current.X; progressAt = _skillClock.ElapsedMilliseconds; }
                        stuck = _skillClock.ElapsedMilliseconds - progressAt >= 1000;
                        if (safe && !stuck) continue;
                        if (!safe && !stuck && settlingAttempts++ < 2)
                        {
                            var settling = Stopwatch.StartNew();
                            while (settling.ElapsedMilliseconds < 300 && _skillClock.ElapsedMilliseconds < seconds * 1000L)
                            {
                                safe = direction < 0 ? _current.Gameplay.CanStepLeft : _current.Gameplay.CanStepRight;
                                if (safe) break;
                                Step(forage ? new InputState { SelectedSlot = SearchSlot(true) } : new InputState());
                                CheckExploration(startY);
                            }
                            if (safe) continue;
                        }
                        bool otherSafe = direction < 0 ? _current.Gameplay.CanStepRight : _current.Gameplay.CanStepLeft;
                        if (reversed || !otherSafe) Fail(stuck ? "exploration_stuck_after_bounded_recovery" : "no_proven_visible_ground_for_next_step");
                        direction = -direction; reversed = true;
                        progressX = _current.X; progressAt = _skillClock.ElapsedMilliseconds;
                        _record("stage_c_recovery", "seek_stone:one_direction_change", _current);
                        continue;
                    }
                    int slot = SearchSlot(forage);
                    Step(new InputState { Left = direction < 0, Right = direction > 0, SelectedSlot = slot });
                    CheckExploration(startY);
                }
                Fail("exploration_timeout_no_visible_stone;mining_not_attempted");
                return false;
            }

            private void CheckExploration(float startY)
            {
                if (_current.Health <= 40 || _skillStart.Health - _current.Health >= 20)
                    Fail("exploration_stopped_after_damage");
                if (_current.Y - startY > 32) Fail("exploration_stopped_after_downward_motion");
            }

            private void RecoverHealth(int seconds, int requestedHealthTarget)
            {
                _healthTarget = Math.Min(requestedHealthTarget, _current.MaxHealth);
                Begin("recover_health", "normal standing regeneration; requestedHealthTarget=" + requestedHealthTarget +
                    ";healthTarget=" + _healthTarget + "; current visible melee defense only; no movement or health writes");
                int initialHealth = _current.Health;
                int targetHealth = _healthTarget;
                if (targetHealth < 25) Fail("unsupported_max_health");
                RequireHotbar(_current.Gameplay.SwordSlot, "normal_hotbar_melee_weapon_required");
                while (_skillClock.ElapsedMilliseconds < seconds * 1000L)
                {
                    if (initialHealth - _current.Health >= 20) Fail("recovery_stopped_after_health_loss_20");
                    if (_current.Health >= targetHealth)
                    {
                        OwnObservation ready = _current;
                        Step(new InputState());
                        if (initialHealth - _current.Health >= 20) Fail("recovery_stopped_after_health_loss_20");
                        if (Newer(_current, ready) && _current.Health >= targetHealth)
                        {
                            Complete("success", "normal_health_threshold_retained;enemy_kills_unverified;health_target=" + targetHealth);
                            return;
                        }
                        continue;
                    }
                    VisibleEnemy enemy = NearestEnemy();
                    bool near = enemy != null && Math.Abs(enemy.X - (_current.X + 10)) <= 96 &&
                        Math.Abs(enemy.Y - (_current.Y + 21)) <= 96;
                    int slot = _current.Gameplay.SwordSlot;
                    RequireHotbar(slot, "normal_hotbar_melee_weapon_required");
                    Step(near ? new InputState { SelectedSlot = slot, AimTileX = enemy.TileX, AimTileY = enemy.TileY,
                        UseItem = _skillClock.ElapsedMilliseconds % 650 < 500 } : new InputState { SelectedSlot = slot });
                }
                Fail("normal_health_recovery_timeout;threshold_not_verified");
            }

            private void Approach(VisibleTarget target, int reach, Func<bool> stillVisible, Action guard = null,
                bool allowSoilSettling = false)
            {
                var timer = Stopwatch.StartNew();
                float progressX = _current.X;
                long progressAt = 0;
                while (timer.ElapsedMilliseconds < 15000)
                {
                    CheckApproachTarget(target, stillVisible, guard);
                    if (InReach(target, reach))
                    {
                        OwnObservation ready = _current;
                        Step(new InputState());
                        CheckApproachTarget(target, stillVisible, guard);
                        if (Newer(_current, ready) && InReach(target, reach)) return;
                        continue;
                    }
                    double dx = CenterX(target) - (_current.X + 10);
                    if (Math.Abs(dx) <= 24) Fail("target_unreachable_vertically");
                    if (Math.Abs(_current.X - progressX) >= 4) { progressX = _current.X; progressAt = timer.ElapsedMilliseconds; }
                    if (timer.ElapsedMilliseconds - progressAt >= 1000)
                        Fail("approach_stuck_1s_no_verified_jump_route");
                    bool ground = dx < 0 ? _current.Gameplay.CanStepLeft : _current.Gameplay.CanStepRight;
                    if (allowSoilSettling && !ground)
                    {
                        if (_soilApproachRecoveries >= 2) Fail("movement_ground_not_legally_verified");
                        ++_soilApproachRecoveries;
                        _record("stage_c_recovery", "soil_approach:neutral_settling_attempt=" + _soilApproachRecoveries, _current);
                        var settling = Stopwatch.StartNew();
                        bool readyToContinue = false;
                        while (settling.ElapsedMilliseconds < 300)
                        {
                            CheckApproachTarget(target, stillVisible, guard);
                            Step(new InputState());
                            CheckApproachTarget(target, stillVisible, guard);
                            if (InReach(target, reach))
                            { readyToContinue = true; break; }
                            ground = dx < 0 ? _current.Gameplay.CanStepLeft : _current.Gameplay.CanStepRight;
                            if (ground) { readyToContinue = true; break; }
                        }
                        if (!readyToContinue) Fail("movement_ground_not_legally_verified");
                        // Re-enter the loop for fresh reach confirmation or the
                        // current direction's ground proof. Never move in recovery.
                        continue;
                    }
                    Step(new InputState { Left = dx < 0, Right = dx > 0 });
                    if (guard != null) guard();
                }
                Fail("approach_timeout_15s");
            }

            private void CheckApproachTarget(VisibleTarget target, Func<bool> stillVisible, Action guard)
            {
                if (guard != null) guard();
                if (!stillVisible()) Fail("approach_target_no_longer_visible");
                if (!Local(target)) Fail("target_outside_supported_local_range");
            }

            private void Step(InputState input)
            {
                CheckCancelled(); CheckFresh();
                CheckForage();
                input = input.Copy();
                if (_lowHealthRecoveryOnly && (input.Left || input.Right || input.Up || input.Jump ||
                    input.CraftWorkBench || input.CraftRecipe != null))
                    Fail("recovery_only_stationary_defense_allowed");
                // Every Stage C movement path, including combat retreat and
                // resource pickup, needs the current filtered ground proof.
                // No jump corridor is exposed by this preview observation.
                if (input.Jump) Fail("jump_route_not_legally_verified");
                if ((input.Left && !_current.Gameplay.CanStepLeft) ||
                    (input.Right && !_current.Gameplay.CanStepRight))
                    Fail("movement_ground_not_legally_verified");
                bool needsUp = (input.Left && _current.Gameplay.StepRequiresUpLeft) ||
                    (input.Right && _current.Gameplay.StepRequiresUpRight);
                if (needsUp && input.UseItem)
                {
                    // Stay still for an ordinary attack instead of combining
                    // platform ascent with item use or aiming interactions.
                    input.Left = input.Right = input.Up = false;
                }
                else if (needsUp)
                {
                    input.Up = true;
                    if (!PlatformStepGuard.CanApply(input, _current.Gameplay)) Fail("invalid_platform_up_action");
                    if (_lastUpObservation != null && !Newer(_current, _lastUpObservation))
                        Fail("platform_up_requires_new_observation");
                    _lastUpObservation = _current;
                }
                else if (input.Up) Fail("platform_up_route_not_legally_verified");
                var period = Stopwatch.StartNew();
                long sent = _clock.ElapsedMilliseconds;
                OwnObservation acknowledgement = _client.Act(_current, input);
                if (_clock.ElapsedMilliseconds - sent > ProtocolLimits.MaxObservationAgeMs) Fail("action_reply_too_old");
                Accept(acknowledgement, false);
                while (period.ElapsedMilliseconds < PeriodMs)
                { CheckCancelled(); Thread.Sleep((int)Math.Min(10, PeriodMs - period.ElapsedMilliseconds)); }
                Observe();
                _record("stage_c_action_result", _skill + ":actual_sample_after_action", _current);
            }

            private void Observe()
            {
                CheckCancelled();
                long sent = _clock.ElapsedMilliseconds;
                OwnObservation sample = _client.Observe();
                if (_clock.ElapsedMilliseconds - sent > ProtocolLimits.MaxObservationAgeMs) Fail("observation_reply_too_old");
                Accept(sample, true); CheckFresh();
                CheckForage();
            }

            private void Accept(OwnObservation sample, bool update)
            {
                _cleanupAllowed = false;
                if (sample == null) Fail("missing_observation");
                if (sample.Menu || sample.Dead || sample.TextInput || sample.GamePaused || sample.OptionsOpen ||
                    string.IsNullOrEmpty(sample.WorldId)) Fail("unsafe_world_ui_death_or_pause");
                if (sample.Health <= 0 || (sample.Health < 25 && !_lowHealthRecoveryOnly)) Fail("unsafe_low_health");
                if (sample.ControlState != "Agent") Fail("control_lost_no_automatic_rearm");
                if (sample.Sequence <= 0 || sample.GameTick <= 0 || sample.MonotonicMs < 0 ||
                    !Finite(sample.X) || !Finite(sample.Y)) Fail("invalid_observation");
                if (_world == null) _world = sample.WorldId;
                if (sample.WorldId != _world) Fail("world_changed");
                if (sample.Gameplay == null) Fail("gameplay_observation_not_enabled");
                GameplayObservation g = sample.Gameplay;
                if (g.SelectedSlot < 0 || g.SelectedSlot >= 50) Fail("invalid_selected_slot");
                foreach (int slot in new[] { g.PickaxeSlot, g.SwordSlot, g.BowSlot, g.PlatformSlot, g.TorchSlot })
                    if (slot < -1 || slot >= 50) Fail("invalid_gameplay_slot");
                foreach (int count in new[] { g.Wood, g.Stone, g.Dirt, g.Gel, g.Torches, g.WoodPlatforms,
                    g.WoodenArrows, g.WoodenBows, g.WoodenSwords, g.WorkBenches })
                    if (count < 0 || count > 10000000) Fail("invalid_inventory_count");
                foreach (VisibleTarget[] targets in new[] { g.StoneTargets, g.GoneStoneTargets,
                    g.WorkBenchTargets, g.PlatformPlacementTargets, g.PlatformTargets, g.TorchPlacementTargets, g.TorchTargets })
                    ValidateTargets(targets);
                ValidateTargets(g.DirtTargets, GameplayObservation.MaxDirtTargetsPerKind);
                ValidateTargets(g.GoneDirtTargets, GameplayObservation.MaxDirtTargetsPerKind);
                if (g.Enemies != null)
                {
                    if (g.Enemies.Length > GameplayObservation.MaxVisibleEnemies) Fail("too_many_visible_enemies");
                    foreach (VisibleEnemy e in g.Enemies)
                        if (e == null || e.Id < 0 || e.Id > 10000 || (e.Kind != "slime" && e.Kind != "zombie" && e.Kind != "demon_eye" && e.Kind != "ghost") ||
                            !Finite(e.X) || !Finite(e.Y) || !Finite(e.VelocityX) || !Finite(e.VelocityY) ||
                            e.TileX < 0 || e.TileY < 0 || e.TileX > 1000000 || e.TileY > 1000000)
                            Fail("invalid_visible_enemy");
                }
                if (_current != null && (sample.Sequence < _current.Sequence || sample.GameTick < _current.GameTick ||
                    sample.MonotonicMs < _current.MonotonicMs)) Fail("observation_regressed");
                _cleanupAllowed = true;
                if (!update) return;
                if (_current == null || Newer(sample, _current))
                { _lastAdvance = _clock.ElapsedMilliseconds; _current = sample; }
            }

            private void CheckFresh()
            {
                if (_current == null || _clock.ElapsedMilliseconds - _lastAdvance > ProtocolLimits.MaxObservationAgeMs)
                    Fail("observation_stalled");
            }
            private void CheckCancelled()
            {
                if (_cancelled()) throw new OperationCanceledException("cancelled");
                if (_clock.ElapsedMilliseconds > 180000) Fail("stage_c_total_timeout_180s");
            }
            private void Begin(string skill, string conditions)
            { CheckForage(); _skill = skill; _skillClock = Stopwatch.StartNew(); _skillStart = _current; _target = null; _product = null; _attempts = 1; _record("stage_c_skill_start", skill + ":" + conditions, _current); }
            private void Complete(string status, string reason, OwnObservation before = null)
            { _results.Add(Result(status, reason, before ?? _skillStart)); _record("stage_c_skill_result", _skill + ":" + status + ":" + reason, _current); }
            private StageCTaskResult Result(string status, string reason, OwnObservation before)
            {
                GameplayObservation b = before == null ? null : before.Gameplay, a = _current == null ? null : _current.Gameplay;
                return new StageCTaskResult { Skill = _skill, Status = status, Reason = reason,
                    ElapsedMs = _skillClock == null ? 0 : _skillClock.ElapsedMilliseconds, Attempts = _attempts,
                    StartSequence = before == null ? 0 : before.Sequence, EndSequence = _current == null ? 0 : _current.Sequence,
                    TileX = _target == null ? -1 : _target.TileX, TileY = _target == null ? -1 : _target.TileY,
                    WoodBefore = b == null ? 0 : b.Wood, WoodAfter = a == null ? 0 : a.Wood,
                    StoneBefore = b == null ? 0 : b.Stone, StoneAfter = a == null ? 0 : a.Stone,
                    DirtBefore = b == null ? 0 : b.Dirt, DirtAfter = a == null ? 0 : a.Dirt,
                    ProductBefore = b == null ? 0 : ProductCount(b, _product), ProductAfter = a == null ? 0 : ProductCount(a, _product),
                    HealthBefore = before == null ? 0 : before.Health, HealthAfter = _current == null ? 0 : _current.Health,
                    HealthTarget = _skill == "recover_health" ? _healthTarget : 0,
                    StepLeftReason = a == null ? "unobserved" : a.StepLeftReason,
                    StepRightReason = a == null ? "unobserved" : a.StepRightReason,
                    KillVerified = false };
            }
            private void TryRecord(string name, string detail) { try { _record(name, detail, _current); } catch { } }
            private static void Fail(string reason) { throw new InvalidOperationException(reason); }
            private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
            private static bool Newer(OwnObservation a, OwnObservation b)
            { return a != null && b != null && a.Sequence > b.Sequence && a.GameTick > b.GameTick && a.MonotonicMs > b.MonotonicMs; }
            private static void RequireHotbar(int slot, string reason) { if (slot < 0 || slot > 9) Fail(reason); }
            private static void ValidateTargets(VisibleTarget[] targets, int maximum = GameplayObservation.MaxTargetsPerKind)
            {
                if (targets == null) return;
                if (targets.Length > maximum) Fail("too_many_visible_targets");
                foreach (VisibleTarget t in targets)
                    if (t == null || t.TileX < 0 || t.TileY < 0 || t.TileX > 1000000 || t.TileY > 1000000) Fail("invalid_visible_target");
            }
            private static bool Contains(VisibleTarget[] targets, VisibleTarget target)
            {
                if (targets == null || target == null) return false;
                foreach (VisibleTarget t in targets) if (Same(t, target)) return true;
                return false;
            }
            private static bool Same(VisibleTarget a, VisibleTarget b) { return a != null && b != null && a.TileX == b.TileX && a.TileY == b.TileY; }
            private bool Local(VisibleTarget t) { return t != null && Math.Abs(CenterX(t) - (_current.X + 10)) <= 640 && Math.Abs(CenterY(t) - (_current.Y + 21)) <= 96; }
            private bool InReach(VisibleTarget t, int reach) { return Local(t) && Math.Abs(CenterX(t) - (_current.X + 10)) <= reach && Math.Abs(CenterY(t) - (_current.Y + 21)) <= 80; }
            private VisibleTarget Nearest(VisibleTarget[] targets, List<VisibleTarget> excluded, bool placement, int candidateReach = 64)
            {
                VisibleTarget best = null; double distance = double.MaxValue;
                if (targets == null) return null;
                foreach (VisibleTarget t in targets)
                {
                    if (!Local(t) || (placement && !InReach(t, candidateReach)) || (excluded != null && excluded.Exists(e => Same(e, t)))) continue;
                    if (placement && candidateReach > 64)
                    {
                        double dx = CenterX(t) - (_current.X + 10), dy = CenterY(t) - (_current.Y + 21);
                        if (dx * dx + dy * dy > candidateReach * candidateReach) continue;
                    }
                    if (!placement && _product == "stone" && Math.Abs(CenterX(t) - (_current.X + 10)) < 16 && CenterY(t) >= _current.Y + 40) continue;
                    double d = Math.Abs(CenterX(t) - (_current.X + 10)) + Math.Abs(CenterY(t) - (_current.Y + 21));
                    if (d < distance) { distance = d; best = t.Copy(); }
                }
                return best;
            }
            private static double CenterX(VisibleTarget t) { return t.TileX * 16.0 + 8; }
            private static double CenterY(VisibleTarget t) { return t.TileY * 16.0 + 8; }
            private bool CanCraft(string recipe)
            {
                if (_current.Gameplay.CanCraftRecipes == null) return false;
                foreach (string id in _current.Gameplay.CanCraftRecipes) if (id == recipe) return true;
                return false;
            }
            private static bool NeedsTable(string id) { return id == GameplayRecipeIds.WoodenBow || id == GameplayRecipeIds.WoodenSword || id == GameplayRecipeIds.WoodenArrow; }
            private static int WoodCost(string id)
            { if (id == GameplayRecipeIds.WoodenBow || id == GameplayRecipeIds.WorkBench) return 10; if (id == GameplayRecipeIds.WoodenSword) return 7; return 1; }
            private static int ProductCount(GameplayObservation g, string id)
            {
                if (id == "stone") return g.Stone;
                if (id == "dirt") return g.Dirt;
                if (id == GameplayRecipeIds.WoodenBow) return g.WoodenBows;
                if (id == GameplayRecipeIds.WoodenSword) return g.WoodenSwords;
                if (id == GameplayRecipeIds.WoodenArrow) return g.WoodenArrows;
                if (id == GameplayRecipeIds.WoodPlatform) return g.WoodPlatforms;
                if (id == GameplayRecipeIds.Torch) return g.Torches;
                if (id == GameplayRecipeIds.WorkBench) return g.WorkBenches;
                return 0;
            }
            private static bool CraftDelta(GameplayObservation before, GameplayObservation after, string id)
            {
                int output = id == GameplayRecipeIds.WoodenArrow ? 25 : id == GameplayRecipeIds.WoodPlatform ? 2 : id == GameplayRecipeIds.Torch ? 3 : 1;
                return after.Wood == before.Wood - WoodCost(id) && after.Stone == before.Stone - (id == GameplayRecipeIds.WoodenArrow ? 1 : 0) &&
                    after.Gel == before.Gel - (id == GameplayRecipeIds.Torch ? 1 : 0) && ProductCount(after, id) == ProductCount(before, id) + output;
            }
            private static bool RetainedCraftDelta(GameplayObservation before, GameplayObservation after, string id)
            {
                int output = id == GameplayRecipeIds.WoodenArrow ? 25 : id == GameplayRecipeIds.WoodPlatform ? 2 : id == GameplayRecipeIds.Torch ? 3 : 1;
                return after.Wood >= before.Wood - WoodCost(id) && after.Stone >= before.Stone - (id == GameplayRecipeIds.WoodenArrow ? 1 : 0) &&
                    after.Gel >= before.Gel - (id == GameplayRecipeIds.Torch ? 1 : 0) && ProductCount(after, id) == ProductCount(before, id) + output;
            }
            private int PlaceSlot(bool torch) { return torch ? _current.Gameplay.TorchSlot : _current.Gameplay.PlatformSlot; }
            private int PlaceCount(bool torch) { return torch ? _current.Gameplay.Torches : _current.Gameplay.WoodPlatforms; }
            private VisibleTarget[] PlaceCandidates(bool torch) { return torch ? _current.Gameplay.TorchPlacementTargets : _current.Gameplay.PlatformPlacementTargets; }
            private VisibleTarget[] PlacedTargets(bool torch) { return torch ? _current.Gameplay.TorchTargets : _current.Gameplay.PlatformTargets; }
            private VisibleEnemy NearestEnemy()
            {
                VisibleEnemy best = null; double distance = double.MaxValue;
                if (_current.Gameplay.Enemies == null) return null;
                foreach (VisibleEnemy e in _current.Gameplay.Enemies)
                {
                    double d = Math.Abs(e.X - (_current.X + 10)) + Math.Abs(e.Y - (_current.Y + 21));
                    if (d < distance) { best = e; distance = d; }
                }
                return best;
            }
            private VisibleEnemy FindEnemy(int id, string kind)
            {
                if (_current.Gameplay.Enemies == null) return null;
                foreach (VisibleEnemy e in _current.Gameplay.Enemies) if (e.Id == id && e.Kind == kind) return e;
                return null;
            }
        }
    }
}
