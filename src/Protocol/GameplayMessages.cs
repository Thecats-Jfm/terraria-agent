using System;
using System.Runtime.Serialization;

namespace TerrariaAgent.Protocol
{
    [DataContract]
    public sealed class VisibleTarget
    {
        [DataMember(Name = "tileX", Order = 0)] public int TileX { get; set; }
        [DataMember(Name = "tileY", Order = 1)] public int TileY { get; set; }

        public VisibleTarget Copy()
        { return new VisibleTarget { TileX = TileX, TileY = TileY }; }
    }

    // Only filtered, currently visible candidates and the local player's own
    // inventory summary cross the bridge. No Tile/Item/world objects are retained.
    [DataContract]
    public sealed class GameplayObservation
    {
        public const int MaxTargetsPerKind = 4;
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

        [OnDeserializing]
        private void NeutralDefaults(StreamingContext context)
        {
            AxeSlot = -1; WorkBenchSlot = -1; SelectedSlot = -1;
            TreeTargets = new VisibleTarget[0];
            PlacementTargets = new VisibleTarget[0];
            WorkBenchTargets = new VisibleTarget[0];
            GoneTreeTargets = new VisibleTarget[0];
        }

        public GameplayObservation Copy()
        {
            return new GameplayObservation { Wood = Wood, WorkBenches = WorkBenches,
                AxeSlot = AxeSlot, WorkBenchSlot = WorkBenchSlot, SelectedSlot = SelectedSlot,
                HasFreeSlot = HasFreeSlot, CanCraftWorkBench = CanCraftWorkBench,
                TreeTargets = CopyTargets(TreeTargets), PlacementTargets = CopyTargets(PlacementTargets),
                WorkBenchTargets = CopyTargets(WorkBenchTargets), GoneTreeTargets = CopyTargets(GoneTreeTargets) };
        }

        private static VisibleTarget[] CopyTargets(VisibleTarget[] targets)
        {
            if (targets == null || targets.Length == 0) return new VisibleTarget[0];
            int length = Math.Min(targets.Length, MaxTargetsPerKind);
            var copies = new VisibleTarget[length];
            for (int i = 0; i < length; i++)
                copies[i] = targets[i] == null ? null : targets[i].Copy();
            return copies;
        }
    }
}
