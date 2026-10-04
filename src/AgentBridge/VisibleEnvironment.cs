using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ObjectData;
using TerrariaAgent.Protocol;

namespace TerrariaAgent.Bridge
{
    // Call only from the game update thread, and only when B is explicitly
    // enabled. Only previously emitted legal tree coordinates survive a Capture;
    // history is bounded and belongs to one ActiveWorldFileData reference.
    public static class VisibleEnvironment
    {
        private const float MinimumBrightness = 0.20f;
        private const int HorizontalCandidateRadius = 48;
        private const int VerticalCandidateRadius = 10;
        private const int MaxRememberedTrees = 16;
        private static object _observedWorld;
        private static readonly List<VisibleTarget> RememberedTrees = new List<VisibleTarget>(MaxRememberedTrees);

        public static void ClearHistory()
        {
            _observedWorld = null;
            RememberedTrees.Clear();
        }

        public static GameplayObservation Capture(Player player)
        {
            object world = Main.gameMenu || Main.netMode != 0 ? null : Main.ActiveWorldFileData;
            if (!object.ReferenceEquals(world, _observedWorld))
            { RememberedTrees.Clear(); _observedWorld = world; }
            var result = new GameplayObservation();
            if (player == null || player.inventory == null) return result;
            SummarizeInventory(player, result);
            // Conservative initial B scope: single-player, ordinary gravity and
            // the player's own camera. Other controlled entities are unsupported.
            if (world == null || Main.gameMenu || Main.netMode != 0 || Main.tile == null || Main.Camera == null ||
                !player.active || player.dead || player.ghost || player.spectating >= 0 ||
                player.isOperatingAnotherEntity || player.isControlledByFilm || player.gravDir != 1f)
                return result;

            Vector2 position = Main.Camera.ScaledPosition;
            Vector2 size = Main.Camera.ScaledSize;
            if (!Finite(position.X) || !Finite(position.Y) || !Finite(size.X) || !Finite(size.Y) ||
                size.X <= 0f || size.Y <= 0f || size.X > 8192f || size.Y > 8192f ||
                !Finite(player.Center.X) || !Finite(player.Center.Y)) return result;
            var visibility = new CaptureVisibility(player.Center, position, size);
            if (!visibility.ContainsPoint(player.Center)) return result;

            int centerX = (int)Math.Floor(player.Center.X / 16f);
            int centerY = (int)Math.Floor(player.Center.Y / 16f);
            int minX = Math.Max(1, centerX - HorizontalCandidateRadius);
            int maxX = Math.Min(Main.maxTilesX - 2, centerX + HorizontalCandidateRadius);
            int minY = Math.Max(1, centerY - VerticalCandidateRadius);
            int maxY = Math.Min(Main.maxTilesY - 2, centerY + VerticalCandidateRadius);
            var trees = new List<VisibleTarget>(GameplayObservation.MaxTargetsPerKind);
            var placements = new List<VisibleTarget>(GameplayObservation.MaxTargetsPerKind);
            var benches = new List<VisibleTarget>(GameplayObservation.MaxTargetsPerKind);
            var goneTrees = new List<VisibleTarget>(GameplayObservation.MaxTargetsPerKind);
            TileObjectData benchData = TileObjectData.GetTileData(TileID.WorkBenches, 0, 0);
            bool knownBenchShape = benchData != null && benchData.Width == 2 && benchData.Height == 1 &&
                benchData.CoordinateWidth > 0 && benchData.CoordinatePadding >= 0 &&
                benchData.CoordinateFullWidth == 2 * (benchData.CoordinateWidth + benchData.CoordinatePadding) &&
                benchData.CoordinateFullHeight > 0;

            // This bounded local rectangle is intersected with actual viewport
            // visibility before each tile read. It is not a world/map scan.
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    Tile tile;
                    if (!visibility.TryReadVisibleTile(x, y, false, out tile)) continue;
                    if (IsOrdinaryTreeRoot(visibility, x, y, tile))
                        AddNearest(trees, x, y, centerX, centerY);
                    if (!knownBenchShape) continue;
                    if (IsCompleteBench(visibility, x, y, tile, benchData))
                        AddNearest(benches, x, y, centerX, centerY);
                    if (!player.IsInTileInteractionRange(x, y, TileReachCheckSettings.Simple, 0) ||
                        !player.IsInTileInteractionRange(x + 1, y, TileReachCheckSettings.Simple, 0) ||
                        tile.active()) continue;
                    Tile right, supportLeft, supportRight;
                    if (!visibility.TryReadVisibleTile(x + 1, y, false, out right) || right.active() ||
                        !visibility.TryReadVisibleTile(x, y + 1, true, out supportLeft) ||
                        !visibility.TryReadVisibleTile(x + 1, y + 1, true, out supportRight) ||
                        !FullSolidSupport(supportLeft) || !FullSolidSupport(supportRight)) continue;
                    // A placement candidate is only geometry/reach eligibility;
                    // normal ItemCheck remains responsible for actual placement.
                    AddNearest(placements, x, y, centerX, centerY);
                }
            result.TreeTargets = trees.ToArray();
            result.PlacementTargets = placements.ToArray();
            result.WorkBenchTargets = benches.ToArray();
            // A missing/truncated candidate is not evidence of a chopped tree.
            // Recheck only remembered positions which are currently observable.
            foreach (VisibleTarget remembered in RememberedTrees)
            {
                Tile current;
                if (visibility.TryReadVisibleTile(remembered.TileX, remembered.TileY, false, out current) &&
                    (!current.active() || current.type != TileID.Trees))
                    AddNearest(goneTrees, remembered.TileX, remembered.TileY, centerX, centerY);
            }
            result.GoneTreeTargets = goneTrees.ToArray();
            foreach (VisibleTarget tree in result.TreeTargets) RememberTree(tree);
            // Crafting qualification is supplied separately by the normal recipe
            // checks. A geometry/inventory snapshot must never invent it.
            return result;
        }

        private static void RememberTree(VisibleTarget target)
        {
            foreach (VisibleTarget existing in RememberedTrees)
                if (existing.TileX == target.TileX && existing.TileY == target.TileY) return;
            if (RememberedTrees.Count == MaxRememberedTrees) RememberedTrees.RemoveAt(0);
            RememberedTrees.Add(target.Copy());
        }

        private static void SummarizeInventory(Player player, GameplayObservation result)
        {
            result.SelectedSlot = player.selectedItem;
            int count = Math.Min(player.inventory.Length, 58);
            for (int i = 0; i < count; i++)
            {
                Item item = player.inventory[i];
                bool empty = item != null && item.IsAir;
                if (i < 50 && empty) result.HasFreeSlot = true;
                if (item == null || empty || item.stack <= 0) continue;
                if (item.type == ItemID.Wood) result.Wood = AddCount(result.Wood, item.stack);
                if (item.type == ItemID.WorkBench) result.WorkBenches = AddCount(result.WorkBenches, item.stack);
                // Initial B operates normal hotbar selection only. An axe/bench
                // elsewhere in the inventory is not claimed to be selectable.
                if (i < 10 && item.axe > 0 && result.AxeSlot < 0) result.AxeSlot = i;
                if (i < 10 && item.type == ItemID.WorkBench && result.WorkBenchSlot < 0) result.WorkBenchSlot = i;
            }
        }

        private static bool IsOrdinaryTreeRoot(CaptureVisibility visibility, int x, int y, Tile tile)
        {
            if (!ActiveVisible(tile, TileID.Trees) || tile.frameX < 0 || tile.frameY < 0 ||
                tile.frameX % 22 != 0 || tile.frameY % 22 != 0) return false;
            int frameX = tile.frameX / 22, frameY = tile.frameY / 22;
            // These are GetTreeBottom's branch/root-side redirections, verified
            // in 1.4.5.8 IL. Reject them rather than following any tree off screen.
            if ((frameX == 3 && frameY <= 2) || (frameX == 4 && frameY >= 3 && frameY <= 5) ||
                ((frameX == 1 || frameX == 2) && frameY >= 6 && frameY <= 8) ||
                ((frameX == 2 || frameX == 3) && frameY >= 9)) return false;
            Tile support;
            // Grass-rooted ordinary Trees produce ordinary Wood. Other biomes
            // and tree types are outside this first task and are not classified.
            return visibility.TryReadVisibleTile(x, y + 1, true, out support) &&
                FullSolidSupport(support) && support.type == TileID.Grass;
        }

        private static bool IsCompleteBench(CaptureVisibility visibility, int x, int y, Tile left, TileObjectData data)
        {
            if (!ActiveVisible(left, TileID.WorkBenches) || left.frameX < 0 || left.frameY < 0 ||
                left.frameX % data.CoordinateFullWidth != 0 || left.frameY % data.CoordinateFullHeight != 0)
                return false;
            Tile right;
            return visibility.TryReadVisibleTile(x + 1, y, false, out right) &&
                ActiveVisible(right, TileID.WorkBenches) && right.frameY == left.frameY &&
                right.frameX == left.frameX + data.CoordinateWidth + data.CoordinatePadding;
        }

        private static bool ActiveVisible(Tile tile, ushort type)
        { return tile != null && tile.active() && !tile.inActive() && !tile.invisibleBlock() && tile.type == type; }

        private static bool FullSolidSupport(Tile tile)
        {
            return tile != null && tile.active() && !tile.inActive() && !tile.invisibleBlock() &&
                !tile.halfBrick() && tile.slope() == 0 && KnownSolidType(tile.type) &&
                Main.tileSolid[tile.type] && !Main.tileSolidTop[tile.type];
        }

        private static bool KnownSolidType(ushort type)
        { return Main.tileSolid != null && Main.tileSolidTop != null && type < Main.tileSolid.Length && type < Main.tileSolidTop.Length; }

        private static bool Finite(float value)
        { return !float.IsNaN(value) && !float.IsInfinity(value); }

        private static int AddCount(int current, int addition)
        { return (int)Math.Min(int.MaxValue, (long)current + addition); }

        private static void AddNearest(List<VisibleTarget> targets, int x, int y, int centerX, int centerY)
        {
            int distance = (x - centerX) * (x - centerX) + (y - centerY) * (y - centerY);
            int index = 0;
            while (index < targets.Count)
            {
                VisibleTarget previous = targets[index];
                int previousDistance = (previous.TileX - centerX) * (previous.TileX - centerX) +
                    (previous.TileY - centerY) * (previous.TileY - centerY);
                if (distance < previousDistance) break;
                index++;
            }
            if (index >= GameplayObservation.MaxTargetsPerKind) return;
            targets.Insert(index, new VisibleTarget { TileX = x, TileY = y });
            if (targets.Count > GameplayObservation.MaxTargetsPerKind) targets.RemoveAt(targets.Count - 1);
        }

        private sealed class CaptureVisibility
        {
            private readonly Vector2 _eye;
            private readonly Vector2 _position;
            private readonly Vector2 _size;
            private readonly Dictionary<long, bool> _lighting = new Dictionary<long, bool>();

            internal CaptureVisibility(Vector2 eye, Vector2 position, Vector2 size)
            { _eye = eye; _position = position; _size = size; }

            internal bool ContainsPoint(Vector2 point)
            {
                return point.X >= _position.X && point.Y >= _position.Y &&
                    point.X < _position.X + _size.X && point.Y < _position.Y + _size.Y;
            }

            internal bool TryReadVisibleTile(int x, int y, bool supportTopSurface, out Tile tile)
            {
                tile = null;
                if (!InViewAndLit(x, y)) return false;
                // For a full support block only its exposed top surface is the
                // sight endpoint. Intermediate solids still block the ray.
                var target = new Vector2(x * 16f + 8f, y * 16f + (supportTopSurface ? 0.5f : 8f));
                if (!ClearVisibleRay(target)) return false;
                tile = Main.tile[x, y];
                return tile != null && !tile.invisibleBlock();
            }

            private bool InViewAndLit(int x, int y)
            {
                if (x < 1 || y < 1 || x >= Main.maxTilesX - 1 || y >= Main.maxTilesY - 1 ||
                    x * 16f < _position.X || y * 16f < _position.Y ||
                    (x + 1) * 16f > _position.X + _size.X || (y + 1) * 16f > _position.Y + _size.Y)
                    return false;
                long key = ((long)x << 32) | (uint)y;
                bool lit;
                if (_lighting.TryGetValue(key, out lit)) return lit;
                float brightness = Lighting.Brightness(x, y);
                lit = Finite(brightness) && brightness >= MinimumBrightness;
                _lighting.Add(key, lit);
                return lit;
            }

            // Conservative optical supercover: guard each traversed tile with
            // viewport + light BEFORE reading it, stop at the first obstruction,
            // and reject unknowns. No read occurs behind a blocking foreground
            // tile. Slopes/half blocks count as full obstructions for this stage.
            private bool ClearVisibleRay(Vector2 target)
            {
                return VisibleRay.IsClear(_eye.X, _eye.Y, target.X, target.Y, RayCellClear);
            }

            private bool RayCellClear(int x, int y, bool endpoint)
            {
                if (!InViewAndLit(x, y)) return false;
                if (endpoint) return true; // Only the facing endpoint may be solid.
                Tile tile = Main.tile[x, y];
                if (tile == null || tile.invisibleBlock()) return false;
                if (!tile.active() || tile.inActive()) return true;
                if (!KnownSolidType(tile.type)) return false;
                return !Main.tileSolid[tile.type] || Main.tileSolidTop[tile.type];
            }
        }
    }
}
