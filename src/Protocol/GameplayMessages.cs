using System;
using System.Runtime.Serialization;

namespace TerrariaAgent.Protocol
{
    public static class GameplayRecipeIds
    {
        public const string WorkBench = "workbench";
        public const string WoodenBow = "wooden_bow";
        public const string WoodenArrow = "wooden_arrow";
        public const string WoodenSword = "wooden_sword";
        public const string WoodPlatform = "wood_platform";
        public const string Torch = "torch";
        public const int MaxRecipes = 6;

        public static bool IsKnown(string value)
        {
            return value == WorkBench || value == WoodenBow || value == WoodenArrow ||
                value == WoodenSword || value == WoodPlatform || value == Torch;
        }

        public static string[] All()
        { return new[] { WorkBench, WoodenBow, WoodenArrow, WoodenSword, WoodPlatform, Torch }; }
    }

    [DataContract]
    public sealed class VisibleTarget
    {
        [DataMember(Name = "tileX", Order = 0)] public int TileX { get; set; }
        [DataMember(Name = "tileY", Order = 1)] public int TileY { get; set; }

        public VisibleTarget Copy()
        { return new VisibleTarget { TileX = TileX, TileY = TileY }; }
    }

    // A current, identifiable appearance only. Id is not retained as an enemy
    // history: the slot may be reused once the object leaves the visible list.
    // X/Y denote the visible center. No life, AI, target or off-screen data.
    [DataContract]
    public sealed class VisibleEnemy
    {
        [DataMember(Name = "id", Order = 0)] public int Id { get; set; }
        [DataMember(Name = "kind", Order = 1)] public string Kind { get; set; }
        [DataMember(Name = "tileX", Order = 2)] public int TileX { get; set; }
        [DataMember(Name = "tileY", Order = 3)] public int TileY { get; set; }
        [DataMember(Name = "x", Order = 4)] public float X { get; set; }
        [DataMember(Name = "y", Order = 5)] public float Y { get; set; }
        [DataMember(Name = "velocityX", Order = 6)] public float VelocityX { get; set; }
        [DataMember(Name = "velocityY", Order = 7)] public float VelocityY { get; set; }

        public VisibleEnemy Copy()
        { return new VisibleEnemy { Id = Id, Kind = Kind, TileX = TileX, TileY = TileY,
            X = X, Y = Y, VelocityX = VelocityX, VelocityY = VelocityY }; }
    }

    // Only filtered, currently visible candidates and the local player's own
    // inventory summary cross the bridge. No Tile/Item/world objects are retained.
    [DataContract]
    public sealed class GameplayObservation
    {
        public const int MaxTargetsPerKind = 4;
        public const int MaxDirtTargetsPerKind = 2;
        public const int MaxVisibleEnemies = 2;
        public const int MaxStepReasonLength = 48;
        [DataMember(Name = "wood", Order = 0)] public int Wood { get; set; }
        [DataMember(Name = "workBenches", Order = 1)] public int WorkBenches { get; set; }
        [DataMember(Name = "axeSlot", Order = 2)] public int AxeSlot { get; set; } = -1;
        [DataMember(Name = "workBenchSlot", Order = 3)] public int WorkBenchSlot { get; set; } = -1;
        [DataMember(Name = "selectedSlot", Order = 4)] public int SelectedSlot { get; set; } = -1;
        [DataMember(Name = "hasFreeSlot", Order = 5)] public bool HasFreeSlot { get; set; }
        [DataMember(Name = "treeTargets", Order = 6)] public VisibleTarget[] TreeTargets { get; set; } = new VisibleTarget[0];
        [DataMember(Name = "placementTargets", Order = 7)] public VisibleTarget[] PlacementTargets { get; set; } = new VisibleTarget[0];
        [DataMember(Name = "workBenchTargets", Order = 8)] public VisibleTarget[] WorkBenchTargets { get; set; } = new VisibleTarget[0];
        [DataMember(Name = "canCraftWorkBench", Order = 9)] public bool CanCraftWorkBench { get; set; }
        [DataMember(Name = "goneTreeTargets", Order = 10)] public VisibleTarget[] GoneTreeTargets { get; set; } = new VisibleTarget[0];
        [DataMember(Name = "stone", Order = 11)] public int Stone { get; set; }
        [DataMember(Name = "gel", Order = 12)] public int Gel { get; set; }
        [DataMember(Name = "torches", Order = 13)] public int Torches { get; set; }
        [DataMember(Name = "woodPlatforms", Order = 14)] public int WoodPlatforms { get; set; }
        [DataMember(Name = "woodenArrows", Order = 15)] public int WoodenArrows { get; set; }
        [DataMember(Name = "woodenBows", Order = 16)] public int WoodenBows { get; set; }
        [DataMember(Name = "woodenSwords", Order = 17)] public int WoodenSwords { get; set; }
        [DataMember(Name = "pickaxeSlot", Order = 18)] public int PickaxeSlot { get; set; } = -1;
        [DataMember(Name = "swordSlot", Order = 19)] public int SwordSlot { get; set; } = -1;
        [DataMember(Name = "bowSlot", Order = 20)] public int BowSlot { get; set; } = -1;
        [DataMember(Name = "platformSlot", Order = 21)] public int PlatformSlot { get; set; } = -1;
        [DataMember(Name = "torchSlot", Order = 22)] public int TorchSlot { get; set; } = -1;
        [DataMember(Name = "stoneTargets", Order = 23)] public VisibleTarget[] StoneTargets { get; set; } = new VisibleTarget[0];
        [DataMember(Name = "goneStoneTargets", Order = 24)] public VisibleTarget[] GoneStoneTargets { get; set; } = new VisibleTarget[0];
        [DataMember(Name = "platformPlacementTargets", Order = 25)] public VisibleTarget[] PlatformPlacementTargets { get; set; } = new VisibleTarget[0];
        [DataMember(Name = "platformTargets", Order = 26)] public VisibleTarget[] PlatformTargets { get; set; } = new VisibleTarget[0];
        [DataMember(Name = "torchPlacementTargets", Order = 27)] public VisibleTarget[] TorchPlacementTargets { get; set; } = new VisibleTarget[0];
        [DataMember(Name = "torchTargets", Order = 28)] public VisibleTarget[] TorchTargets { get; set; } = new VisibleTarget[0];
        [DataMember(Name = "enemies", Order = 29)] public VisibleEnemy[] Enemies { get; set; } = new VisibleEnemy[0];
        [DataMember(Name = "canCraftRecipes", Order = 30)] public string[] CanCraftRecipes { get; set; } = new string[0];
        [DataMember(Name = "dirt", Order = 31)] public int Dirt { get; set; }
        [DataMember(Name = "dirtTargets", Order = 32)] public VisibleTarget[] DirtTargets { get; set; } = new VisibleTarget[0];
        [DataMember(Name = "goneDirtTargets", Order = 33)] public VisibleTarget[] GoneDirtTargets { get; set; } = new VisibleTarget[0];
        [DataMember(Name = "canStepLeft", Order = 34)] public bool CanStepLeft { get; set; }
        [DataMember(Name = "canStepRight", Order = 35)] public bool CanStepRight { get; set; }
        // Constant failure categories from the existing visible-geometry guards.
        // No coordinates, tile types, hidden contents or extra reads are added.
        [DataMember(Name = "stepLeftReason", Order = 36)] public string StepLeftReason { get; set; } = "unobserved";
        [DataMember(Name = "stepRightReason", Order = 37)] public string StepRightReason { get; set; } = "unobserved";
        [DataMember(Name = "stepRequiresUpLeft", Order = 38)] public bool StepRequiresUpLeft { get; set; }
        [DataMember(Name = "stepRequiresUpRight", Order = 39)] public bool StepRequiresUpRight { get; set; }

        [OnDeserializing]
        private void NeutralDefaults(StreamingContext context)
        {
            AxeSlot = -1; WorkBenchSlot = -1; SelectedSlot = -1;
            TreeTargets = new VisibleTarget[0];
            PlacementTargets = new VisibleTarget[0];
            WorkBenchTargets = new VisibleTarget[0];
            GoneTreeTargets = new VisibleTarget[0];
            PickaxeSlot = SwordSlot = BowSlot = PlatformSlot = TorchSlot = -1;
            StoneTargets = GoneStoneTargets = PlatformPlacementTargets = PlatformTargets =
                TorchPlacementTargets = TorchTargets = new VisibleTarget[0];
            Enemies = new VisibleEnemy[0];
            CanCraftRecipes = new string[0];
            DirtTargets = GoneDirtTargets = new VisibleTarget[0];
            CanStepLeft = CanStepRight = false;
            StepRequiresUpLeft = StepRequiresUpRight = false;
            StepLeftReason = StepRightReason = "unobserved";
        }

        public GameplayObservation Copy()
        {
            return new GameplayObservation { Wood = Wood, WorkBenches = WorkBenches,
                AxeSlot = AxeSlot, WorkBenchSlot = WorkBenchSlot, SelectedSlot = SelectedSlot,
                HasFreeSlot = HasFreeSlot, CanCraftWorkBench = CanCraftWorkBench,
                TreeTargets = CopyTargets(TreeTargets), PlacementTargets = CopyTargets(PlacementTargets),
                WorkBenchTargets = CopyTargets(WorkBenchTargets), GoneTreeTargets = CopyTargets(GoneTreeTargets),
                Stone = Stone, Gel = Gel, Torches = Torches, WoodPlatforms = WoodPlatforms,
                WoodenArrows = WoodenArrows, WoodenBows = WoodenBows, WoodenSwords = WoodenSwords,
                PickaxeSlot = PickaxeSlot, SwordSlot = SwordSlot, BowSlot = BowSlot, PlatformSlot = PlatformSlot, TorchSlot = TorchSlot,
                StoneTargets = CopyTargets(StoneTargets), GoneStoneTargets = CopyTargets(GoneStoneTargets),
                PlatformPlacementTargets = CopyTargets(PlatformPlacementTargets), PlatformTargets = CopyTargets(PlatformTargets),
                TorchPlacementTargets = CopyTargets(TorchPlacementTargets), TorchTargets = CopyTargets(TorchTargets),
                Enemies = CopyEnemies(Enemies), CanCraftRecipes = CopyRecipes(CanCraftRecipes), Dirt = Dirt,
                DirtTargets = CopyTargets(DirtTargets, MaxDirtTargetsPerKind),
                GoneDirtTargets = CopyTargets(GoneDirtTargets, MaxDirtTargetsPerKind),
                CanStepLeft = CanStepLeft, CanStepRight = CanStepRight,
                StepRequiresUpLeft = StepRequiresUpLeft, StepRequiresUpRight = StepRequiresUpRight,
                StepLeftReason = CopyStepReason(StepLeftReason), StepRightReason = CopyStepReason(StepRightReason) };
        }

        private static string CopyStepReason(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "unobserved";
            string bounded = reason.Substring(0, Math.Min(reason.Length, MaxStepReasonLength));
            // Runtime diagnoses are fixed ASCII categories. Keep the byte bound
            // used by the transport/log checks even for an invalid future source.
            foreach (char c in bounded)
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'))
                    return "unobserved";
            return bounded;
        }

        private static VisibleEnemy[] CopyEnemies(VisibleEnemy[] enemies)
        {
            if (enemies == null || enemies.Length == 0) return new VisibleEnemy[0];
            int length = Math.Min(enemies.Length, MaxVisibleEnemies);
            var copy = new VisibleEnemy[length];
            for (int i = 0; i < length; ++i) copy[i] = enemies[i] == null ? null : enemies[i].Copy();
            return copy;
        }

        private static string[] CopyRecipes(string[] recipes)
        {
            if (recipes == null || recipes.Length == 0) return new string[0];
            var result = new System.Collections.Generic.List<string>(GameplayRecipeIds.MaxRecipes);
            foreach (string recipe in recipes)
                if (GameplayRecipeIds.IsKnown(recipe) && !result.Contains(recipe))
                {
                    result.Add(recipe);
                    if (result.Count == GameplayRecipeIds.MaxRecipes) break;
                }
            return result.ToArray();
        }

        private static VisibleTarget[] CopyTargets(VisibleTarget[] targets, int maximum = MaxTargetsPerKind)
        {
            if (targets == null || targets.Length == 0) return new VisibleTarget[0];
            int length = Math.Min(targets.Length, maximum);
            var copies = new VisibleTarget[length];
            for (int i = 0; i < length; i++)
                copies[i] = targets[i] == null ? null : targets[i].Copy();
            return copies;
        }
    }
}
