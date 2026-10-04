using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using TerrariaAgent.Bridge;
using TerrariaAgent.Protocol;

internal static class Program
{
    // New C contract/admission boundaries only. Synthetic objects and a fake
    // monotonic clock: no game, socket, save, or successful gameplay is claimed.
    private static int _passed;
    private static string _evidenceDirectory;
    private static int Main(string[] args)
    {
        try
        {
            int evidenceOption = Array.IndexOf(args, "--evidence-directory");
            if (evidenceOption >= 0 && evidenceOption + 1 < args.Length)
                _evidenceDirectory = Path.GetFullPath(args[evidenceOption + 1]);
            Run("omitted_C_fields_are_neutral", NeutralDefaults);
            Run("all_C_candidates_are_bounded_and_copied", CopyBounds);
            Run("enemy_wire_contract_contains_only_visible_appearance", EnemyContract);
            Run("full_bounded_C_reply_fits_4096_and_roundtrips", FrameBudget);
            Run("full_C_observation_survives_real_event_log_envelope", LogEnvelope);
            if (!args.Contains("--dto-only"))
            {
                Run("new_recipe_commands_require_enabled_exact_whitelist", RecipeAdmission);
                Run("craft_recipe_cannot_mix_input_or_legacy_craft", RecipeConflicts);
                Run("unsafe_context_revokes_recipe_before_transaction", UnsafeTransaction);
                Run("stale_observation_cannot_authorize_new_recipe", StaleRecipe);
            }
            Console.WriteLine("{\"scope\":\"synthetic_C_contract_and_admission_only_no_game\",\"passed\":" +
                _passed + ",\"status\":\"passed\"}");
            return 0;
        }
        catch (Exception error)
        { Console.Error.WriteLine("FAIL " + error.Message); return 1; }
    }

    private static void Run(string name, Action check)
    { check(); ++_passed; Console.WriteLine("PASS " + name); }
    private static void Require(bool value, string reason)
    { if (!value) throw new InvalidOperationException(reason); }

    private static void NeutralDefaults()
    {
        byte[] omitted = Encoding.UTF8.GetBytes("{}");
        var observation = JsonCodec.Deserialize<GameplayObservation>(omitted);
        Require(observation.PickaxeSlot == -1 && observation.SwordSlot == -1 && observation.BowSlot == -1 &&
            observation.PlatformSlot == -1 && observation.TorchSlot == -1, "omitted C slot selected item zero");
        Require(CArrays(observation).All(array => array != null && array.Length == 0) &&
            DirtArrays(observation).All(array => array != null && array.Length == 0) &&
            observation.Enemies != null && observation.Enemies.Length == 0 &&
            observation.CanCraftRecipes != null && observation.CanCraftRecipes.Length == 0,
            "omitted C field invented a target or craft qualification");
        Require(observation.Stone == 0 && observation.Dirt == 0 && observation.Gel == 0 && observation.Torches == 0 &&
            observation.WoodPlatforms == 0 && observation.WoodenArrows == 0 &&
            observation.WoodenBows == 0 && observation.WoodenSwords == 0, "omitted inventory field grants materials");
        Require(!observation.CanStepLeft && !observation.CanStepRight,
            "omitted movement geometry grants a safe direction");
        Require(!observation.StepRequiresUpLeft && !observation.StepRequiresUpRight &&
            !JsonCodec.Deserialize<InputState>(omitted).Up && !JsonCodec.Deserialize<AgentRequest>(omitted).Up,
            "omitted platform fields injected Up");
        Require(observation.StepLeftReason == "unobserved" && observation.StepRightReason == "unobserved",
            "omitted movement diagnosis invented an observed result");
        Require(JsonCodec.Deserialize<AgentRequest>(omitted).CraftRecipe == null &&
            JsonCodec.Deserialize<InputState>(omitted).CraftRecipe == null, "omitted action requested a recipe");
        var own = JsonCodec.Deserialize<OwnObservation>(omitted);
        Require(own.Facing == 0 && own.ItemAnimation == 0,
            "omitted own state invented a facing direction or active swing");
    }

    private static void CopyBounds()
    {
        var original = Gameplay(32);
        original.CanStepLeft = original.CanStepRight = true;
        original.StepRequiresUpLeft = original.StepRequiresUpRight = true;
        original.StepLeftReason = new string('l', GameplayObservation.MaxStepReasonLength + 20);
        original.StepRightReason = "supported";
        original.Enemies = Enumerable.Range(0, 32).Select(index => Enemy(index)).ToArray();
        original.CanCraftRecipes = new[] { "hidden_or_unknown_recipe", GameplayRecipeIds.Torch, GameplayRecipeIds.Torch,
            GameplayRecipeIds.WorkBench, GameplayRecipeIds.WoodenBow, GameplayRecipeIds.WoodenArrow,
            GameplayRecipeIds.WoodenSword, GameplayRecipeIds.WoodPlatform, "" };
        var copied = original.Copy();
        Require(copied.CanStepLeft && copied.CanStepRight, "movement permission was lost during publisher copy");
        Require(copied.StepRequiresUpLeft && copied.StepRequiresUpRight, "platform Up proof was lost during copy");
        Require(copied.StepLeftReason.Length == GameplayObservation.MaxStepReasonLength &&
            copied.StepRightReason == "supported", "movement diagnosis bypassed its publication bound");
        original.CanStepLeft = original.CanStepRight = false;
        original.StepRequiresUpLeft = original.StepRequiresUpRight = false;
        original.StepLeftReason = original.StepRightReason = "changed";
        Require(copied.CanStepLeft && copied.CanStepRight, "published direction changed with a later source snapshot");
        var directionRoundtrip = JsonCodec.Deserialize<GameplayObservation>(JsonCodec.Serialize(copied));
        Require(directionRoundtrip.CanStepLeft && directionRoundtrip.CanStepRight,
            "explicitly observed direction permission was lost at wire roundtrip");
        Require(directionRoundtrip.StepRequiresUpLeft && directionRoundtrip.StepRequiresUpRight,
            "platform Up proof was lost or aliased at wire roundtrip");
        Require(directionRoundtrip.StepLeftReason == new string('l', GameplayObservation.MaxStepReasonLength) &&
            directionRoundtrip.StepRightReason == "supported", "movement diagnosis was lost or aliased at roundtrip");
        Require(new GameplayObservation { StepLeftReason = null, StepRightReason = "" }.Copy().StepLeftReason == "unobserved" &&
            new GameplayObservation { StepRightReason = "" }.Copy().StepRightReason == "unobserved",
            "empty diagnosis was published as an observed movement result");
        Require(new GameplayObservation { StepLeftReason = new string('\u754c', GameplayObservation.MaxStepReasonLength),
            StepRightReason = "unknown;payload" }.Copy().StepLeftReason == "unobserved" &&
            new GameplayObservation { StepRightReason = "unknown;payload" }.Copy().StepRightReason == "unobserved",
            "non-category text bypassed the diagnostic byte bound");
        Require(CArrays(copied).All(array => array.Length == 4) && copied.Enemies.Length == 2,
            "publisher copy retained unbounded C candidates");
        Require(DirtArrays(original).All(array => array.Length == 32) &&
            DirtArrays(copied).All(array => array.Length == GameplayObservation.MaxDirtTargetsPerKind),
            "dirt observation bypassed its independent two-target cap");
        Require(copied.CanCraftRecipes.Length == 6 && copied.CanCraftRecipes.Distinct().Count() == 6 &&
            copied.CanCraftRecipes.All(GameplayRecipeIds.IsKnown), "publisher leaked unknown or duplicate recipe values");
        foreach (var array in CArrays(original)) { array[0].TileX = -42; array[1] = null; }
        foreach (var array in DirtArrays(original)) { array[0].TileX = -42; array[1] = null; }
        original.Enemies[0].X = -42; original.Enemies[0].Kind = "hidden_ai_payload"; original.Enemies[1] = null;
        original.CanCraftRecipes[1] = "mutated_recipe";
        Require(CArrays(copied).All(array => array[0].TileX == 32767 && array[1] != null) &&
            DirtArrays(copied).All(array => array[0].TileX == 32767 && array[1] != null) &&
            copied.Enemies[0].X != -42 && copied.Enemies[0].Kind == "green_slime" && copied.Enemies[1] != null &&
            copied.CanCraftRecipes.Contains(GameplayRecipeIds.Torch), "C copy retained mutable source aliases");
        copied.StoneTargets[0].TileY = -42; copied.Enemies[0].Y = -42;
        copied.DirtTargets[0].TileY = -42;
        Require(original.StoneTargets[0].TileY == 32767 && original.DirtTargets[0].TileY == 32767 &&
            original.Enemies[0].Y != -42,
            "mutating published C snapshot reached source objects");
    }

    private static void EnemyContract()
    {
        var allowed = new HashSet<string> { "id", "kind", "tileX", "tileY", "x", "y", "velocityX", "velocityY" };
        using (JsonDocument document = JsonDocument.Parse(JsonCodec.Serialize(Enemy(199))))
        {
            var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            Require(names.Length == allowed.Count && names.All(allowed.Contains),
                "enemy wire contract grew hidden state such as health, AI, target, or a death claim");
        }
        // The enemy DTO intentionally has no kill flag. Empty Enemies means no
        // currently visible target, never evidence that a previously seen one died.
    }

    private static void FrameBudget()
    {
        var reply = MaximumReply();
        byte[] encoded = JsonCodec.Serialize(reply);
        Require(encoded.Length <= 4096, "all bounded C fields exceed transport frame");
        using (JsonDocument document = JsonDocument.Parse(encoded))
        {
            JsonElement own = document.RootElement.GetProperty("observation");
            JsonElement gameplay = own.GetProperty("gameplay");
            Require(!gameplay.GetProperty("canStepLeft").GetBoolean() && !gameplay.GetProperty("canStepRight").GetBoolean(),
                "conservative false movement geometry was omitted from the complete frame");
            Require(!gameplay.GetProperty("stepRequiresUpLeft").GetBoolean() &&
                !gameplay.GetProperty("stepRequiresUpRight").GetBoolean() && !own.GetProperty("inputs").GetProperty("up").GetBoolean(),
                "platform fields were omitted from the maximum complete frame");
            Require(gameplay.GetProperty("stepLeftReason").GetString().Length == GameplayObservation.MaxStepReasonLength &&
                gameplay.GetProperty("stepRightReason").GetString().Length == GameplayObservation.MaxStepReasonLength,
                "maximum movement diagnoses were omitted from the complete frame");
            Require(own.GetProperty("facing").GetInt32() == -1 &&
                own.GetProperty("itemAnimation").GetInt32() == int.MaxValue,
                "own facing or swing state was omitted from the complete frame");
        }
        Console.WriteLine("SYNTHETIC full C envelope: " + encoded.Length + " bytes (4096 maximum).");
        using (var stream = new MemoryStream())
        {
            Wire.WriteFrame(stream, encoded); stream.Position = 0;
            var decoded = JsonCodec.Deserialize<AgentReply>(Wire.ReadFrame(stream));
            Require(CArrays(decoded.Observation.Gameplay).All(array => array.Length == 4) &&
                DirtArrays(decoded.Observation.Gameplay).All(array => array.Length == GameplayObservation.MaxDirtTargetsPerKind) &&
                decoded.Observation.Gameplay.Enemies.Length == 2 && decoded.Observation.Gameplay.CanCraftRecipes.Length == 6 &&
                decoded.Observation.Gameplay.Stone == int.MaxValue && decoded.Observation.Gameplay.Dirt == int.MaxValue &&
                !decoded.Observation.Gameplay.CanStepLeft && !decoded.Observation.Gameplay.CanStepRight &&
                decoded.Observation.Gameplay.StepLeftReason.Length == GameplayObservation.MaxStepReasonLength &&
                decoded.Observation.Gameplay.StepRightReason.Length == GameplayObservation.MaxStepReasonLength &&
                decoded.Observation.Gameplay.PickaxeSlot == 49 && decoded.Observation.Facing == -1 &&
                decoded.Observation.ItemAnimation == int.MaxValue,
                "C fields were silently lost at frame roundtrip");
        }
    }

    private static AgentReply MaximumReply()
    {
        return new AgentReply { Type = "operator_arm", Status = "rejected",
            Reason = "materials_environment_or_space_unavailable", SessionId = new string('s', 32),
            WorldId = new string('w', 32), Sequence = long.MaxValue,
            Observation = new OwnObservation { Sequence = long.MaxValue, WorldId = new string('w', 32),
                X = float.MaxValue, Y = float.MaxValue, VelocityX = float.MinValue, VelocityY = float.MinValue,
                Health = int.MaxValue, MaxHealth = int.MaxValue, ControlState = "LatchedStop",
                Facing = -1, ItemAnimation = int.MaxValue,
                Reason = "transport_io_failed_or_timeout", GameTick = long.MaxValue, MonotonicMs = long.MaxValue,
                GamePaused = true, OptionsOpen = true, CanArm = true, CanOperatorArm = true,
                Inputs = MaximumInput(), LeaseInputs = MaximumInput(), Gameplay = Gameplay(4).Copy() } };
    }

    private static void LogEnvelope()
    {
        // Link the real EventLog rather than imitate its private wrapper. This
        // writes one clearly synthetic fixture under the provided evidence root.
        string root = _evidenceDirectory ?? Path.Combine(Path.GetTempPath(), "terraria-agent-c-log-fixtures");
        string directory = Path.Combine(root, "synthetic-c-envelope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        // combat_test is longer than main, so this covers the profile overhead
        // as well as both maximally populated two-target dirt arrays.
        using (var log = new EventLog(directory, "20261004T000000Z-ffffffff", "combat_test"))
            Require(log.Record("synthetic_C_envelope_boundary", new string('d', 256), MaximumReply().Observation),
                "full C observation was rejected by actual EventLog");
        string path = Path.Combine(directory, "bridge-00.jsonl");
        Require(File.Exists(path), "real EventLog did not flush the synthetic observation");
        string[] lines = File.ReadAllLines(path);
        Require(lines.Length == 1 && Encoding.UTF8.GetByteCount(lines[0]) <= 4096,
            "real log envelope exceeded codec limit or missed the observation");
        using (JsonDocument parsed = JsonDocument.Parse(lines[0]))
        {
            JsonElement own = parsed.RootElement.GetProperty("observation");
            JsonElement gameplay = own.GetProperty("gameplay");
            Require(parsed.RootElement.GetProperty("challenge").GetString() == "combat_test" &&
                own.GetProperty("facing").GetInt32() == -1 && own.GetProperty("itemAnimation").GetInt32() == int.MaxValue &&
                !gameplay.GetProperty("canStepLeft").GetBoolean() && !gameplay.GetProperty("canStepRight").GetBoolean() &&
                gameplay.GetProperty("stepLeftReason").GetString().Length == GameplayObservation.MaxStepReasonLength &&
                gameplay.GetProperty("stepRightReason").GetString().Length == GameplayObservation.MaxStepReasonLength &&
                gameplay.GetProperty("dirt").GetInt32() == int.MaxValue &&
                gameplay.GetProperty("dirtTargets").GetArrayLength() == GameplayObservation.MaxDirtTargetsPerKind &&
                gameplay.GetProperty("goneDirtTargets").GetArrayLength() == GameplayObservation.MaxDirtTargetsPerKind &&
                gameplay.GetProperty("enemies").GetArrayLength() == 2 &&
                gameplay.GetProperty("stoneTargets").GetArrayLength() == 4 &&
                gameplay.GetProperty("canCraftRecipes").GetArrayLength() == 6,
                "real EventLog silently lost its profile or bounded C/dirt fields");
        }
        var connection = new ConnectionInfo { Challenge = "combat_test" };
        Require(JsonCodec.Deserialize<ConnectionInfo>(JsonCodec.Serialize(connection)).Challenge == "combat_test",
            "local connection metadata lost explicit combat profile classification");
        Console.WriteLine("SYNTHETIC real EventLog envelope: " + Encoding.UTF8.GetByteCount(lines[0]) +
            " bytes with 256-byte diagnostic; fixture retained outside source.");
    }

    private static void RecipeAdmission()
    {
        foreach (string recipe in GameplayRecipeIds.All())
        {
            var disabled = new Fixture(false); string reason;
            Require(!disabled.Apply(disabled.Craft(recipe), out reason), "recipe bypassed default gameplay disable: " + recipe);
            Neutral(disabled.Gate);
            var enabled = new Fixture(true); AgentRequest request = enabled.Craft(recipe);
            Require(enabled.Apply(request, out reason), "whitelisted normal recipe refused: " + recipe + ":" + reason);
            request.CraftRecipe = "source_mutated";
            var snapshot = enabled.Gate.Snapshot();
            Require(snapshot.Inputs.CraftRecipe == recipe && !snapshot.Inputs.CraftWorkBench &&
                snapshot.Inputs.SelectedSlot == -1 && snapshot.Inputs.AimTileX == -1 && !snapshot.Inputs.UseItem,
                "accepted recipe was lost or acquired tool inputs");
            snapshot.Inputs.CraftRecipe = "snapshot_mutated";
            Require(enabled.Gate.PollInputs().CraftRecipe == recipe, "published snapshot mutates stored craft identity");
            enabled.Gate.Stop("c-owner"); Neutral(enabled.Gate);
        }
        foreach (string unknown in new[] { "", " ", "WOODEN_BOW", "wooden_bow ", "recipe_999", "teleport", "give_item" })
        {
            var fixture = new Fixture(true); string reason;
            Require(!fixture.Apply(fixture.Craft(unknown), out reason), "unknown recipe was accepted: " + unknown);
            Neutral(fixture.Gate);
        }
    }

    private static void RecipeConflicts()
    {
        foreach (string conflict in new[] { "left", "right", "jump", "use", "slot", "aim", "legacy" })
        {
            var fixture = new Fixture(true); var request = fixture.Craft(GameplayRecipeIds.WoodenArrow);
            if (conflict == "left") request.Left = true;
            if (conflict == "right") request.Right = true;
            if (conflict == "jump") request.Jump = true;
            if (conflict == "use") { request.UseItem = true; request.SelectedSlot = 1; request.AimTileX = request.AimTileY = 5; }
            if (conflict == "slot") request.SelectedSlot = 1;
            if (conflict == "aim") request.AimTileX = request.AimTileY = 5;
            if (conflict == "legacy") request.CraftWorkBench = true;
            string reason;
            Require(!fixture.Apply(request, out reason), "one-shot recipe mixed continuous or legacy input: " + conflict);
            Neutral(fixture.Gate);
        }
    }

    private static void UnsafeTransaction()
    {
        foreach (string context in new[] { "death", "menu", "text_or_UI", "new_world" })
        {
            var fixture = new Fixture(true); string reason;
            Require(fixture.Apply(fixture.Craft(GameplayRecipeIds.WoodPlatform), out reason), "setup recipe rejected");
            fixture.Gate.UpdateContext(context == "menu" ? null : context == "new_world" ? "replacement-world" : "c-world",
                context == "death", context == "menu", context == "text_or_UI");
            int submitted = 0;
            Require(!fixture.Gate.TryExecuteCurrent("c-owner", "c-world", 2, () => submitted++, out reason) && submitted == 0,
                "unsafe context admitted new recipe transaction: " + context);
            Neutral(fixture.Gate);
            Require(!fixture.Apply(fixture.Craft(GameplayRecipeIds.WoodPlatform, 3), out reason),
                "recipe renewal restored ownership after unsafe context: " + context);
        }
    }

    private static void StaleRecipe()
    {
        var fixture = new Fixture(true); string reason;
        for (int i = 1; i <= 6; i++)
        {
            fixture.Now += 200;
            fixture.Gate.RecordObservation(i + 1, fixture.Now);
            var renewal = fixture.Craft(null, i + 1); renewal.ObservationSequence = i + 1;
            Require(fixture.Apply(renewal, out reason), "synthetic fresh renewal failed: " + reason);
        }
        var stale = fixture.Craft(GameplayRecipeIds.Torch, 8); stale.ObservationSequence = 1;
        Require(!fixture.Apply(stale, out reason) && reason == "stale_observation",
            "new recipe used a stale target/qualification reference: " + reason);
        Neutral(fixture.Gate);
    }

    private static GameplayObservation Gameplay(int count)
    {
        return new GameplayObservation { Wood = int.MaxValue, WorkBenches = int.MaxValue, Stone = int.MaxValue, Dirt = int.MaxValue,
            Gel = int.MaxValue, Torches = int.MaxValue, WoodPlatforms = int.MaxValue, WoodenArrows = int.MaxValue,
            WoodenBows = int.MaxValue, WoodenSwords = int.MaxValue, AxeSlot = 49, WorkBenchSlot = 49, SelectedSlot = 49,
            PickaxeSlot = 49, SwordSlot = 49, BowSlot = 49, PlatformSlot = 49, TorchSlot = 49,
            StepLeftReason = new string('l', GameplayObservation.MaxStepReasonLength),
            StepRightReason = new string('r', GameplayObservation.MaxStepReasonLength),
            HasFreeSlot = true, CanCraftWorkBench = true, TreeTargets = Targets(count), GoneTreeTargets = Targets(count),
            PlacementTargets = Targets(count), WorkBenchTargets = Targets(count), StoneTargets = Targets(count),
            GoneStoneTargets = Targets(count), PlatformPlacementTargets = Targets(count), PlatformTargets = Targets(count),
            TorchPlacementTargets = Targets(count), TorchTargets = Targets(count),
            DirtTargets = Targets(count), GoneDirtTargets = Targets(count),
            Enemies = new[] { Enemy(198), Enemy(199) }, CanCraftRecipes = GameplayRecipeIds.All() };
    }
    private static VisibleTarget[][] CArrays(GameplayObservation value)
    { return new[] { value.StoneTargets, value.GoneStoneTargets, value.PlatformPlacementTargets,
        value.PlatformTargets, value.TorchPlacementTargets, value.TorchTargets }; }
    private static VisibleTarget[][] DirtArrays(GameplayObservation value)
    { return new[] { value.DirtTargets, value.GoneDirtTargets }; }
    private static VisibleTarget[] Targets(int count)
    { return Enumerable.Range(0, count).Select(index => new VisibleTarget { TileX = 32767, TileY = 32767 }).ToArray(); }
    private static VisibleEnemy Enemy(int id)
    { return new VisibleEnemy { Id = id, Kind = "green_slime", TileX = 32767, TileY = 32767,
        X = float.MaxValue, Y = float.MaxValue, VelocityX = float.MinValue, VelocityY = float.MinValue }; }
    private static InputState MaximumInput()
        { return new InputState { Up = false, SelectedSlot = 49, AimTileX = 32767, AimTileY = 32767,
        CraftRecipe = GameplayRecipeIds.WoodenSword }; }
    private static void Neutral(LeaseGate gate)
    {
        foreach (InputState input in new[] { gate.PollInputs(), gate.Snapshot().Inputs })
            Require(!input.Up && !input.Left && !input.Right && !input.Jump && !input.UseItem && !input.CraftWorkBench &&
                input.CraftRecipe == null && input.SelectedSlot == -1 && input.AimTileX == -1 && input.AimTileY == -1,
                "revocation left continuous input or a new recipe armed");
    }

    private sealed class Fixture
    {
        internal long Now = 1000;
        internal readonly LeaseGate Gate;
        internal Fixture(bool enabled)
        {
            Gate = new LeaseGate(() => Now, false, enabled);
            string reason;
            Require(Gate.OpenSession("c-owner", out reason), "fixture session failed");
            Gate.UpdateContext("c-world", false, false, false); Gate.RecordObservation(1, Now);
            var permission = Gate.Snapshot();
            Require(Gate.PermitNextArm("c-owner", "c-world", permission.ControlEpoch, Now), "fixture consent failed");
            var arm = Craft(null, 1); arm.Type = "arm";
            Require(Gate.ExplicitArm(arm, out reason), "fixture arm failed: " + reason);
        }
        internal AgentRequest Craft(string recipe, long sequence = 2)
        { return new AgentRequest { Type = "action", SessionId = "c-owner", WorldId = "c-world",
            Sequence = sequence, ObservationSequence = 1, TtlMs = 250, CraftRecipe = recipe }; }
        internal bool Apply(AgentRequest request, out string reason)
        { return Gate.TryApplyAction(request, Now, out reason); }
    }
}
