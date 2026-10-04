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
        private static readonly List<VisibleTarget> RememberedStones = new List<VisibleTarget>(MaxRememberedTrees);
        private static readonly List<VisibleTarget> RememberedDirt = new List<VisibleTarget>(MaxRememberedTrees);
        internal static string LastStepLeftReason { get; private set; } = "unobserved";
        internal static string LastStepRightReason { get; private set; } = "unobserved";

        public static void ClearHistory()
        {
            _observedWorld = null;
            RememberedTrees.Clear();
            RememberedStones.Clear();
            RememberedDirt.Clear();
            LastStepLeftReason = LastStepRightReason = "unobserved";
        }

        public static GameplayObservation Capture(Player player)
        {
            LastStepLeftReason = LastStepRightReason = "unobserved";
            object world = Main.gameMenu || Main.netMode != 0 ? null : Main.ActiveWorldFileData;
            if (!object.ReferenceEquals(world, _observedWorld))
            { RememberedTrees.Clear(); RememberedStones.Clear(); RememberedDirt.Clear(); _observedWorld = world; }
            var result = new GameplayObservation();
            if (player == null || player.inventory == null) return StepUnavailable(result, "own_inventory_unavailable");
            SummarizeInventory(player, result);
            // Conservative initial B scope: single-player, ordinary gravity and
            // the player's own camera. Other controlled entities are unsupported.
            if (world == null || Main.gameMenu || Main.netMode != 0 || Main.tile == null || Main.Camera == null)
                return StepUnavailable(result, "world_or_camera_unavailable");
            if (!player.active || player.dead || player.ghost || player.spectating >= 0 ||
                player.isOperatingAnotherEntity || player.isControlledByFilm || player.gravDir != 1f ||
                !WorldUiUnobstructed(player))
                return StepUnavailable(result, !WorldUiUnobstructed(player) ? "world_ui_obstructed" : "unsupported_player_context");

            Vector2 position = Main.Camera.ScaledPosition;
            Vector2 size = Main.Camera.ScaledSize;
            if (!Finite(position.X) || !Finite(position.Y) || !Finite(size.X) || !Finite(size.Y) ||
                size.X <= 0f || size.Y <= 0f || size.X > 8192f || size.Y > 8192f ||
                !Finite(player.Center.X) || !Finite(player.Center.Y)) return StepUnavailable(result, "invalid_camera_geometry");
            var visibility = new CaptureVisibility(player.Center, position, size);
            if (!visibility.ContainsPoint(player.Center)) return StepUnavailable(result, "own_center_hud_obstructed");
            // Only current, fully observed local geometry. These flags do not
            // authorize a travel distance or promise next-tick/inertia safety.
            string leftReason, rightReason;
            bool leftRequiresUp, rightRequiresUp;
            result.CanStepLeft = CanStepWithSlopeFallback(player, visibility, -1, out leftRequiresUp, out leftReason);
            result.CanStepRight = CanStepWithSlopeFallback(player, visibility, 1, out rightRequiresUp, out rightReason);
            result.StepRequiresUpLeft = result.CanStepLeft && leftRequiresUp;
            result.StepRequiresUpRight = result.CanStepRight && rightRequiresUp;
            LastStepLeftReason = result.StepLeftReason = leftReason;
            LastStepRightReason = result.StepRightReason = rightReason;

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
            var stones = new List<VisibleTarget>(GameplayObservation.MaxTargetsPerKind);
            var goneStones = new List<VisibleTarget>(GameplayObservation.MaxTargetsPerKind);
            var dirt = new List<VisibleTarget>(GameplayObservation.MaxDirtTargetsPerKind);
            var goneDirt = new List<VisibleTarget>(GameplayObservation.MaxDirtTargetsPerKind);
            var platformPlacements = new List<VisibleTarget>(GameplayObservation.MaxTargetsPerKind);
            var platforms = new List<VisibleTarget>(GameplayObservation.MaxTargetsPerKind);
            var torchPlacements = new List<VisibleTarget>(GameplayObservation.MaxTargetsPerKind);
            var torches = new List<VisibleTarget>(GameplayObservation.MaxTargetsPerKind);
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
                    // Only a proven exposed top beside the player's protected
                    // support footprint is a dig candidate. Read visible air or
                    // whitelisted grass plants first, then guard the top-face ray.
                    // This never inspects a buried layer to find a route/ore.
                    Tile above, surface;
                    if (SafeSideDigPosition(player, x, y) &&
                        player.IsInTileInteractionRange(x, y, TileReachCheckSettings.Simple, 0) &&
                        visibility.TryReadVisibleTile(x, y - 1, false, out above) && SafeDirtAbove(above) &&
                        visibility.TryReadVisibleDirtSurface(x, y, player.position.Y + player.height, out surface))
                        AddNearest(dirt, x, y, centerX, centerY, GameplayObservation.MaxDirtTargetsPerKind);
                    Tile tile;
                    if (!visibility.TryReadVisibleTile(x, y, false, out tile)) continue;
                    if (IsOrdinaryTreeRoot(visibility, x, y, tile))
                        AddNearest(trees, x, y, centerX, centerY);
                    if (ActiveVisible(tile, TileID.Stone)) AddNearest(stones, x, y, centerX, centerY);
                    if (ActiveVisible(tile, TileID.Platforms)) AddNearest(platforms, x, y, centerX, centerY);
                    if (ActiveVisible(tile, TileID.Torches)) AddNearest(torches, x, y, centerX, centerY);
                    bool empty = !tile.active();
                    bool replaceablePlant = !empty && SafeDirtAbove(tile) && player.PlaceThing_IsReplaceableBlock(tile);
                    if ((empty || replaceablePlant) && player.IsInTileInteractionRange(x, y, TileReachCheckSettings.Simple, 0))
                    {
                        Tile support;
                        bool bottomSolid = visibility.TryReadVisibleTile(x, y + 1, true, out support) && FullSolidSupport(support);
                        // Vanilla PlaceTile(type=Torches)/CheckTorch accepts a
                        // tree side only with tree cells above, beside and below.
                        // Plants replacement uses the normal public predicate.
                        bool treeSide = empty && !bottomSolid &&
                            (VisibleTorchTreeAnchor(visibility, x - 1, y) || VisibleTorchTreeAnchor(visibility, x + 1, y));
                        if (bottomSolid || treeSide) AddNearest(torchPlacements, x, y, centerX, centerY);
                        if (empty)
                        {
                            Tile leftSupport, rightSupport;
                            bool leftAnchor = visibility.TryReadVisibleTile(x - 1, y, false, out leftSupport) &&
                                (FullSolidSupport(leftSupport) || ActiveVisible(leftSupport, TileID.Platforms));
                            bool rightAnchor = visibility.TryReadVisibleTile(x + 1, y, false, out rightSupport) &&
                                (FullSolidSupport(rightSupport) || ActiveVisible(rightSupport, TileID.Platforms));
                            if (bottomSolid || leftAnchor || rightAnchor) AddNearest(platformPlacements, x, y, centerX, centerY);
                        }
                    }
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
            result.StoneTargets = stones.ToArray();
            result.DirtTargets = dirt.ToArray();
            result.PlatformPlacementTargets = platformPlacements.ToArray();
            result.PlatformTargets = platforms.ToArray();
            result.TorchPlacementTargets = torchPlacements.ToArray();
            result.TorchTargets = torches.ToArray();
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
            foreach (VisibleTarget remembered in RememberedStones)
            {
                Tile current;
                if (visibility.TryReadVisibleTile(remembered.TileX, remembered.TileY, false, out current) &&
                    (!current.active() || current.type != TileID.Stone))
                    AddNearest(goneStones, remembered.TileX, remembered.TileY, centerX, centerY);
            }
            result.GoneStoneTargets = goneStones.ToArray();
            foreach (VisibleTarget remembered in RememberedDirt)
            {
                Tile current;
                if (visibility.TryReadVisibleTile(remembered.TileX, remembered.TileY, true, out current) &&
                    (!current.active() || (current.type != TileID.Dirt && current.type != TileID.Grass)))
                    AddNearest(goneDirt, remembered.TileX, remembered.TileY, centerX, centerY,
                        GameplayObservation.MaxDirtTargetsPerKind);
            }
            // Clearing grass into active dirt is still the same soil target,
            // and must not be reported as a removed block.
            result.GoneDirtTargets = goneDirt.ToArray();
            foreach (VisibleTarget tree in result.TreeTargets) RememberTree(tree);
            foreach (VisibleTarget stone in result.StoneTargets) RememberTarget(RememberedStones, stone);
            foreach (VisibleTarget soil in result.DirtTargets) RememberTarget(RememberedDirt, soil);
            result.Enemies = CaptureEnemies(visibility, player.Center);
            // Crafting qualification is supplied separately by the normal recipe
            // checks. A geometry/inventory snapshot must never invent it.
            return result;
        }

        private static void RememberTree(VisibleTarget target)
        { RememberTarget(RememberedTrees, target); }

        private static void RememberTarget(List<VisibleTarget> history, VisibleTarget target)
        {
            foreach (VisibleTarget existing in history)
                if (existing.TileX == target.TileX && existing.TileY == target.TileY) return;
            if (history.Count == MaxRememberedTrees) history.RemoveAt(0);
            history.Add(target.Copy());
        }

        private static void SummarizeInventory(Player player, GameplayObservation result)
        {
            result.SelectedSlot = player.selectedItem;
            bool swordSwings = false, bowHasAmmo = false;
            double swordScore = -1, bowScore = -1;
            int count = Math.Min(player.inventory.Length, 58);
            for (int i = 0; i < count; i++)
            {
                Item item = player.inventory[i];
                bool empty = item != null && item.IsAir;
                if (i < 50 && empty) result.HasFreeSlot = true;
                if (item == null || empty || item.stack <= 0) continue;
                if (item.type == ItemID.Wood) result.Wood = AddCount(result.Wood, item.stack);
                if (item.type == ItemID.WorkBench) result.WorkBenches = AddCount(result.WorkBenches, item.stack);
                if (item.type == ItemID.StoneBlock) result.Stone = AddCount(result.Stone, item.stack);
                if (item.type == ItemID.DirtBlock) result.Dirt = AddCount(result.Dirt, item.stack);
                if (item.type == ItemID.Gel) result.Gel = AddCount(result.Gel, item.stack);
                if (item.type == ItemID.Torch) result.Torches = AddCount(result.Torches, item.stack);
                if (item.type == ItemID.WoodPlatform) result.WoodPlatforms = AddCount(result.WoodPlatforms, item.stack);
                if (item.type == ItemID.WoodenArrow) result.WoodenArrows = AddCount(result.WoodenArrows, item.stack);
                if (item.type == ItemID.WoodenBow) result.WoodenBows = AddCount(result.WoodenBows, item.stack);
                if (item.type == ItemID.WoodenSword) result.WoodenSwords = AddCount(result.WoodenSwords, item.stack);
                // Initial B operates normal hotbar selection only. An axe/bench
                // elsewhere in the inventory is not claimed to be selectable.
                if (i < 10 && item.axe > 0 && result.AxeSlot < 0) result.AxeSlot = i;
                if (i < 10 && item.type == ItemID.WorkBench && result.WorkBenchSlot < 0) result.WorkBenchSlot = i;
                if (i < 10 && item.pick > 0 && result.PickaxeSlot < 0) result.PickaxeSlot = i;
                if (i < 10 && GameplayActions.MeleeWeapon(item))
                {
                    // Prefer a normal swing over the initial thrusting short
                    // sword, then compare own item damage/use-time. This is a
                    // selection heuristic, not a promise of actual combat DPS.
                    bool swings = item.useStyle == 1;
                    double score = WeaponScore(item);
                    if (result.SwordSlot < 0 || (swings && !swordSwings) ||
                        (swings == swordSwings && score > swordScore))
                    { result.SwordSlot = i; swordSwings = swings; swordScore = score; }
                }
                if (i < 10 && GameplayActions.ArrowBow(item))
                {
                    // HasAmmo is vanilla's read-only check. Counts above still
                    // describe WoodenBow/WoodenArrow, not every bow/ammo type.
                    bool hasAmmo = player.HasAmmo(item, true);
                    double score = WeaponScore(item);
                    if (result.BowSlot < 0 || (hasAmmo && !bowHasAmmo) ||
                        (hasAmmo == bowHasAmmo && score > bowScore))
                    { result.BowSlot = i; bowHasAmmo = hasAmmo; bowScore = score; }
                }
                if (i < 10 && item.type == ItemID.WoodPlatform && result.PlatformSlot < 0) result.PlatformSlot = i;
                if (i < 10 && item.type == ItemID.Torch && result.TorchSlot < 0) result.TorchSlot = i;
            }
        }

        private static double WeaponScore(Item item)
        { return (double)item.damage / Math.Max(1, item.useTime); }

        // The requested aim is itself currently visible, including a one-tile
        // lead near an observed moving enemy. Never aim through an opaque tile.
        internal static bool CanAimAt(Player player, int x, int y)
        {
            if (player == null || Main.gameMenu || Main.netMode != 0 || Main.Camera == null || Main.tile == null ||
                !player.active || player.dead || player.gravDir != 1f || !WorldUiUnobstructed(player)) return false;
            Vector2 position = Main.Camera.ScaledPosition, size = Main.Camera.ScaledSize;
            if (!Finite(position.X) || !Finite(position.Y) || !Finite(size.X) || !Finite(size.Y) ||
                size.X <= 0 || size.Y <= 0 || size.X > 8192 || size.Y > 8192 ||
                !Finite(player.Center.X) || !Finite(player.Center.Y)) return false;
            var visibility = new CaptureVisibility(player.Center, position, size);
            if (!visibility.ContainsPoint(player.Center)) return false;
            Tile target;
            return visibility.TryReadVisibleTile(x, y, false, out target) &&
                (!target.active() || target.inActive() || (KnownSolidType(target.type) &&
                (!Main.tileSolid[target.type] || Main.tileSolidTop[target.type])));
        }

        private static VisibleEnemy[] CaptureEnemies(CaptureVisibility visibility, Vector2 eye)
        {
            var result = new List<VisibleEnemy>(GameplayObservation.MaxVisibleEnemies);
            if (Main.npc == null) return result.ToArray();
            for (int i = 0; i < Math.Min(Main.npc.Length, 256); ++i)
            {
                NPC npc = Main.npc[i];
                if (npc == null || !npc.active || !visibility.ContainsRectangle(npc.position, npc.width, npc.height)) continue;
                Vector2 center = npc.Center;
                if (!Finite(center.X) || !Finite(center.Y) || !Finite(npc.velocity.X) || !Finite(npc.velocity.Y)) continue;
                int x = (int)Math.Floor(center.X / 16f), y = (int)Math.Floor(center.Y / 16f);
                Tile foreground;
                if (!visibility.TryReadVisibleTile(x, y, false, out foreground) ||
                    (foreground.active() && !foreground.inActive() && KnownSolidType(foreground.type) &&
                    Main.tileSolid[foreground.type] && !Main.tileSolidTop[foreground.type])) continue;
                // Appearance/status are inspected only after geometry/light/LOS.
                // Ordinary blue/green slimes inherit type=BlueSlime and vanilla
                // alpha=175 (SetDefaults), despite being visibly rendered. The
                // green variant has netID=GreenSlime (-3), not type=-3. Other
                // slime variants are outside this appearance whitelist.
                if (npc.hide || npc.friendly || npc.dontTakeDamage) continue;
                bool ordinarySlime = npc.type == NPCID.BlueSlime &&
                    (npc.netID == NPCID.BlueSlime || npc.netID == NPCID.GreenSlime);
                string kind = ordinarySlime ? "slime" : npc.type == NPCID.Zombie ? "zombie" :
                    npc.type == NPCID.DemonEye ? "demon_eye" : npc.type == NPCID.Ghost ? "ghost" : null;
                if (!EnemyAppearanceVisible(kind, npc.alpha)) continue;
                var candidate = new VisibleEnemy { Id = i, Kind = kind, TileX = x, TileY = y,
                    X = center.X, Y = center.Y, VelocityX = npc.velocity.X, VelocityY = npc.velocity.Y };
                double distance = Vector2.DistanceSquared(center, eye);
                int index = 0;
                while (index < result.Count && Vector2.DistanceSquared(new Vector2(result[index].X, result[index].Y), eye) <= distance) ++index;
                if (index >= GameplayObservation.MaxVisibleEnemies) continue;
                result.Insert(index, candidate);
                if (result.Count > GameplayObservation.MaxVisibleEnemies) result.RemoveAt(result.Count - 1);
            }
            return result.ToArray();
        }

        private static bool EnemyAppearanceVisible(string kind, int alpha)
        {
            // Ghost316's normal SetDefaults alpha is100 in the inspected build.
            // This type-specific cap never makes an unknown appearance visible.
            int maximum = kind == "slime" ? 175 : kind == "ghost" ? 100 :
                (kind == "zombie" || kind == "demon_eye") ? 64 : -1;
            return alpha >= 0 && alpha <= maximum;
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

        private static bool OrdinaryDirt(Tile tile)
        { return ActiveVisible(tile, TileID.Dirt) || ActiveVisible(tile, TileID.Grass); }

        private static bool MineableDirtShape(Tile tile)
        {
            if (tile == null || !tile.active() || tile.inActive() || tile.invisibleBlock() || tile.liquid != 0 ||
                (tile.type != TileID.Dirt && tile.type != TileID.Grass) || !KnownSolidType(tile.type) ||
                !Main.tileSolid[tile.type] || Main.tileSolidTop[tile.type]) return false;
            int slope = tile.slope();
            return tile.halfBrick() ? slope == 0 : slope >= 0 && slope <= 2;
        }

        private static bool DirtSurfaceIsFirstHit(float eyeX, float eyeY, float faceX, float faceY,
            int x, int y, int slope, bool half)
        {
            if (!Finite(eyeX) || !Finite(eyeY) || !Finite(faceX) || !Finite(faceY) ||
                slope < 0 || slope > 2 || (half && slope != 0)) return false;
            double left = x * 16.0, top = y * 16.0;
            double dx = (double)faceX - eyeX, dy = (double)faceY - eyeY;
            double enter = 0.0, leave = 1.0;
            if (!ClipHalfAxis(eyeX, dx, left, left + 16.0, ref enter, ref leave) ||
                !ClipHalfAxis(eyeY, dy, top + (half ? 8.0 : 0.0), top + 16.0, ref enter, ref leave)) return false;
            // Exact installed 1.4.5.8 SlopeCollision: floor 1 has local v>=u,
            // floor 2 has v>=16-u; 3/4 are ceiling shapes and stay unsupported.
            // Clip only this already observed cell; no Collision/world scan.
            if (slope == 1 && !ClipDirtHalfSpace(eyeY - top - (eyeX - left), dy - dx, ref enter, ref leave))
                return false;
            if (slope == 2 && !ClipDirtHalfSpace(eyeY - top + eyeX - left - 16.0, dy + dx, ref enter, ref leave))
                return false;
            // The requested top face must be the first solid contact. A low-side
            // ray which enters the cell's solid triangle earlier cannot see it.
            return enter >= 1.0 - 0.0000001 && enter <= 1.0 && leave >= 1.0;
        }

        private static bool ClipDirtHalfSpace(double origin, double delta, ref double enter, ref double leave)
        {
            if (delta == 0.0) return origin >= 0.0;
            double boundary = -origin / delta;
            if (delta > 0.0) enter = Math.Max(enter, boundary);
            else leave = Math.Min(leave, boundary);
            return enter <= leave;
        }

        private static bool SafeDirtAbove(Tile tile)
        {
            // 1.4.5.8 PE constants: Plants=3, Plants2=73. TileFrameImportant
            // routes these to vanilla PlantCheck; runtime tileSolid confirms
            // the observed plant is non-solid. Piles/other objects stay rejected.
            return tile != null && !tile.invisibleBlock() && (!tile.active() ||
                (!tile.inActive() && (tile.type == TileID.Plants || tile.type == TileID.Plants2) &&
                KnownSolidType(tile.type) && !Main.tileSolid[tile.type] && !Main.tileSolidTop[tile.type]));
        }

        private static bool VisibleTorchTreeAnchor(CaptureVisibility visibility, int x, int y)
        {
            Tile beside, above, below;
            return visibility.TryReadVisibleTile(x, y, false, out beside) && ActiveVisible(beside, TileID.Trees) &&
                visibility.TryReadVisibleTile(x, y - 1, false, out above) && ActiveVisible(above, TileID.Trees) &&
                visibility.TryReadVisibleTile(x, y + 1, false, out below) && ActiveVisible(below, TileID.Trees);
        }

        private static bool SafeSideDigPosition(Player player, int x, int y)
        {
            // Keep the player's current support and one adjacent tile intact.
            // Only near the current foot level; no automatic deep shaft opening.
            float left = player.position.X - 16f;
            float right = player.position.X + player.width + 16f;
            if (x * 16f < right && (x + 1) * 16f > left) return false;
            float foot = player.position.Y + player.height;
            return Math.Abs(y * 16f - foot) <= 16f;
        }

        private static GameplayObservation StepUnavailable(GameplayObservation result, string reason)
        {
            LastStepLeftReason = result.StepLeftReason = reason;
            LastStepRightReason = result.StepRightReason = reason;
            return result;
        }

        private static bool CanStepWithSlopeFallback(Player player, CaptureVisibility visibility, int direction,
            out bool requiresUp, out string reason)
        {
            requiresUp = false;
            if (CanStepHorizontally(player, visibility, direction, out reason)) return true;
            if (reason == "up_solidtop_unsupported")
                return CanStepOnVisibleProfile(player, visibility, direction, true, out requiresUp, out reason);
            if (reason == "no_current_foot_support")
                return CanStepOnVisibleProfile(player, visibility, direction, true, out requiresUp, out reason, true);
            // An unrelated unsafe/unknown result cannot obtain a second route.
            if (!SlopeFallbackReason(reason)) return false;
            return CanStepOnVisibleProfile(player, visibility, direction, false, out requiresUp, out reason);
        }

        private static bool SlopeFallbackReason(string reason)
        {
            return reason == "foot_not_grid_aligned" || reason == "drop_space_slope" ||
                reason == "up_support_slope" || reason == "body_slope";
        }

        private static bool NormalProfileFloor(Tile tile, int x, int y, Player player, bool platformRoute)
        {
            if (tile == null || !tile.active() || tile.inActive() || tile.invisibleBlock() ||
                tile.liquid != 0 || tile.halfBrick() || tile.slope() > 2 || !KnownSolidType(tile.type) ||
                (platformRoute && tile.slope() != 0)) return false;
            bool ordinary = (tile.type == TileID.Dirt || tile.type == TileID.Grass || tile.type == TileID.Stone ||
                tile.type == TileID.WoodBlock) && Main.tileSolid[tile.type] && !Main.tileSolidTop[tile.type];
            bool platform = platformRoute && tile.type == TileID.Platforms && tile.frameY == 0 && Main.tileSolidTop[tile.type];
            return (ordinary || platform) && !Collision.CanTileHurt(tile.type, x, y, player);
        }

        private static bool NormalWorkbenchTop(Tile tile, int x, int y, Player player)
        {
            // Installed 1.4.5.8 TileCollision IL0109-0122 accepts solidTop
            // frameY0 as a landing surface; initialization sets top[18]=true.
            return ActiveVisible(tile, TileID.WorkBenches) && !tile.halfBrick() && tile.slope() == 0 &&
                tile.liquid == 0 && tile.frameY == 0 && KnownSolidType(tile.type) &&
                !Main.tileSolid[tile.type] && Main.tileSolidTop[tile.type] &&
                !Collision.CanTileHurt(tile.type, x, y, player);
        }

        private static bool CompleteWalkingWorkbench(CaptureVisibility visibility, int x, int y,
            Tile observed, Player player)
        {
            if (!NormalWorkbenchTop(observed, x, y, player) || observed.frameX < 0) return false;
            TileObjectData data = TileObjectData.GetTileData(TileID.WorkBenches, 0, 0);
            if (data == null || data.Width != 2 || data.Height != 1 || data.CoordinateWidth <= 0 ||
                data.CoordinatePadding < 0 || data.CoordinateFullWidth !=
                2 * (data.CoordinateWidth + data.CoordinatePadding) || data.CoordinateFullHeight <= 0) return false;
            int localFrame = observed.frameX % data.CoordinateFullWidth;
            int stride = data.CoordinateWidth + data.CoordinatePadding;
            if (localFrame != 0 && localFrame != stride) return false;
            int leftX = x - (localFrame == stride ? 1 : 0);
            Tile left, right;
            // Same-Capture primary view/light/eye LOS for both complete pieces.
            // No walking secondary proof or buried tile contributes support.
            return visibility.TryReadVisibleTile(leftX, y, false, out left) &&
                visibility.TryReadVisibleTile(leftX + 1, y, false, out right) &&
                NormalWorkbenchTop(left, leftX, y, player) && NormalWorkbenchTop(right, leftX + 1, y, player) &&
                object.ReferenceEquals(observed, x == leftX ? left : right) &&
                IsCompleteBench(visibility, leftX, y, left, data);
        }

        private static bool CanStepOnVisibleProfile(Player player, CaptureVisibility visibility, int direction,
            bool platformRoute, out bool requiresUp, out string reason, bool requireCurrentFurniture = false)
        {
            requiresUp = false;
            reason = "ok";
            string prefix = requireCurrentFurniture ? "platform_foot_" : platformRoute ? "platform_up_" : "slope_";
            if (direction != -1 && direction != 1) return RejectStep(out reason, prefix + "invalid_direction");
            if (!Finite(player.position.X) || !Finite(player.position.Y) ||
                !Finite(player.velocity.X) || !Finite(player.velocity.Y) ||
                player.width <= 0 || player.width > 64 || player.height <= 0 || player.height > 96)
                return RejectStep(out reason, prefix + "invalid_own_geometry");
            if (Math.Abs(player.velocity.Y) > 0.1f) return RejectStep(out reason, prefix + "own_vertical_motion");
            if (player.gravDir != 1f || player.gravControl || player.gravControl2 || player.confused ||
                player.shimmering || player.controlDown || player.pulley || player.grapCount > 0 || player.sandStorm ||
                player.grappling == null || player.grappling.Length == 0 || player.grappling[0] != -1 ||
                (player.mount != null && player.mount.Active) || player.IsRidingTracks)
                return RejectStep(out reason, prefix + "special_movement");

            const int lookahead = 32;
            float feet = player.position.Y + player.height;
            float left = player.position.X + (direction < 0 ? -lookahead : 0);
            float right = player.position.X + player.width + (direction > 0 ? lookahead : 0);
            if (!Finite(feet) || !visibility.ContainsRectangle(new Vector2(left, player.position.Y),
                player.width + lookahead, player.height))
                return RejectStep(out reason, prefix + "body_sweep_hud_obstructed");
            int firstX = (int)Math.Floor(left / 16f), lastX = (int)Math.Ceiling(right / 16f) - 1;
            int firstY = (int)Math.Floor(player.position.Y / 16f);
            int minimumY = (int)Math.Floor((player.position.Y - 16f) / 16f);
            int lastY = (int)Math.Floor((feet + 16f) / 16f);
            if (minimumY < 1 || lastX < firstX || lastX - firstX + 1 > WalkingSlopeGeometry.MaxFirstFloorCells ||
                lastY - minimumY > 9)
                return RejectStep(out reason, prefix + "cell_budget");

            var scope = new CaptureVisibility.SlopeScope(firstX, lastX, minimumY, lastY);
            var floors = new List<WalkingFloorCell>(lastX - firstX + 1);
            bool hasSlope = false, hasRaisedPlatform = false, hasCurrentFurniture = false;
            for (int x = firstX; x <= lastX; ++x)
            {
                bool found = false;
                for (int y = firstY; y <= lastY; ++y)
                {
                    Tile observed;
                    if (!visibility.TryReadSlopePrimaryTile(x, y, true, scope, out observed))
                        return RejectStep(out reason, prefix + "column_visibility");
                    bool workbenchFloor = requireCurrentFurniture && observed.active() && observed.type == TileID.WorkBenches;
                    if (workbenchFloor && (y * 16f < feet - 1f ||
                        !CompleteWalkingWorkbench(visibility, x, y, observed, player)))
                        return RejectStep(out reason, "platform_foot_workbench_unverified");
                    if (NormalProfileFloor(observed, x, y, player, platformRoute) || workbenchFloor)
                    {
                        if (!visibility.ProveSlopeFloorFace(x, y, feet, observed, scope))
                            return RejectStep(out reason, prefix + "surface_visibility");
                        if (observed.type == TileID.Platforms)
                        {
                            Tile above;
                            if (!visibility.TryReadSlopePrimaryTile(x, y - 1, false, scope, out above) ||
                                !WalkBodyCell(above, x, y - 1, player))
                                return RejectStep(out reason, "platform_up_upper_space_unverified");
                            if (above.active() && (!KnownSolidType(above.type) || Main.tileSolid[above.type] || Main.tileSolidTop[above.type]))
                                return RejectStep(out reason, "platform_up_upper_solid_or_solidtop");
                            hasRaisedPlatform |= y * 16f < feet;
                        }
                        // Nearby furniture is not current support. The complete
                        // first floor must overlap the actual feet within 1px;
                        // all ordinary face/body/profile proofs still apply.
                        hasCurrentFurniture |= (observed.type == TileID.Platforms || workbenchFloor) &&
                            x * 16f < player.position.X + player.width && (x + 1) * 16f > player.position.X &&
                            Math.Abs(y * 16f - feet) <= 1f;
                        var floor = new WalkingFloorCell(x, y, observed.slope(), true);
                        scope.Floors.Add(x, new CaptureVisibility.SlopeFloorProof(observed, floor));
                        floors.Add(floor);
                        hasSlope |= floor.Shape != 0;
                        found = true;
                        break; // A proven first floor never authorizes a read below it.
                    }
                    if (!WalkBodyCell(observed, x, y, player))
                        return RejectStep(out reason, prefix + "first_foreground_unsupported");
                }
                if (!found) return RejectStep(out reason, prefix + "first_floor_missing");
            }
            if (requireCurrentFurniture && (!platformRoute || !hasCurrentFurniture))
                return RejectStep(out reason, "platform_foot_current_solidtop_not_proven");
            if ((!platformRoute && !hasSlope) || (platformRoute && !requireCurrentFurniture && !hasRaisedPlatform))
                return RejectStep(out reason, platformRoute ? "platform_up_no_raised_platform" : "slope_no_proven_floor_slope");

            var actualBody = new WalkingBodyRect(player.position.X, player.position.Y, player.width, player.height);
            WalkingSlopeResult profile;
            if (!WalkingSlopeGeometry.TryEvaluate(actualBody, direction, floors, out profile, lookahead))
                return RejectStep(out reason, prefix + profile.Reason);
            if (platformRoute && !requireCurrentFurniture &&
                (profile.MinFloor >= feet || profile.MaxFloor > feet + 0.000001))
                return RejectStep(out reason, "platform_up_requires_only_up_profile");
            foreach (WalkingFloorCell floor in floors)
            {
                bool overlap; string overlapReason;
                if (!WalkingSlopeGeometry.TrySolidInteriorOverlap(actualBody, floor, out overlap, out overlapReason) || overlap)
                    return RejectStep(out reason, prefix + "actual_body_intersects_floor");
            }

            // The minimum contact envelope keeps each predicted standing body
            // above every first-floor solid. It does not make whole slope cells
            // air, or imply that the full union rectangle is collision-free.
            double headTop = Math.Floor(profile.MinFloor - player.height);
            int envelopeHeight = (int)Math.Ceiling(profile.MaxFloor - headTop);
            if (!visibility.ContainsRectangle(new Vector2(left, (float)headTop),
                player.width + lookahead, envelopeHeight))
                return RejectStep(out reason, prefix + "head_body_hud_obstructed");
            int headRow = (int)Math.Floor(headTop / 16.0);
            if (headRow < minimumY || envelopeHeight <= 0 || envelopeHeight > player.height + 18)
                return RejectStep(out reason, prefix + "head_body_budget");
            scope.BodyProfileProven = true;
            for (int x = firstX; x <= lastX; ++x)
            {
                CaptureVisibility.SlopeFloorProof floor = scope.Floors[x];
                for (int y = headRow; y < floor.Cell.TileY; ++y)
                {
                    Tile body;
                    if (!visibility.TryReadSlopeBodyTile(x, y, scope, out body))
                        return RejectStep(out reason, prefix + "head_body_visibility");
                    if (!WalkBodyCell(body, x, y, player))
                        return RejectStep(out reason, prefix + "head_body_unsafe");
                }
            }
            // Flat or descending current-platform routes do not need Up.
            // Commit an ascent signal only after every proof succeeded.
            requiresUp = platformRoute && hasRaisedPlatform;
            return true;
        }

        private static bool SlopeSegmentHitsSolid(float eyeX, float eyeY, float targetX, float targetY,
            int x, int y, int shape)
        {
            if (!Finite(eyeX) || !Finite(eyeY) || !Finite(targetX) || !Finite(targetY) || shape < 0 || shape > 2)
                return true;
            double left = x * 16.0, top = y * 16.0;
            double dx = (double)targetX - eyeX, dy = (double)targetY - eyeY;
            double enter = 0.0, leave = 1.0;
            if (!ClipHalfAxis(eyeX, dx, left, left + 16.0, ref enter, ref leave) ||
                !ClipHalfAxis(eyeY, dy, top, top + 16.0, ref enter, ref leave)) return false;
            if (shape == 1) return ClipDirtHalfSpace(eyeY - top - (eyeX - left), dy - dx, ref enter, ref leave);
            if (shape == 2) return ClipDirtHalfSpace(eyeY - top + eyeX - left - 16.0, dy + dx, ref enter, ref leave);
            return true;
        }

        private static bool CanStepHorizontally(Player player, CaptureVisibility visibility, int direction, out string reason)
        {
            reason = "ok";
            const float lookahead = 32f;
            if (direction != -1 && direction != 1) return RejectStep(out reason, "invalid_direction");
            if (!Finite(player.position.X) || !Finite(player.position.Y) ||
                !Finite(player.velocity.X) || !Finite(player.velocity.Y) ||
                player.width <= 0 || player.width > 64 || player.height <= 0 || player.height > 96)
                return RejectStep(out reason, "invalid_own_geometry");
            if (Math.Abs(player.velocity.Y) > 0.1f) return RejectStep(out reason, "own_vertical_motion");

            float actualFeet = player.position.Y + player.height;
            float baseFeet = (float)Math.Round(actualFeet / 8f) * 8f;
            if (Math.Abs(actualFeet - baseFeet) > 1f) return RejectStep(out reason, "foot_not_grid_aligned");
            float left = player.position.X + (direction < 0 ? -lookahead : 0f);
            float right = player.position.X + player.width + (direction > 0 ? lookahead : 0f);
            if (!visibility.ContainsRectangle(new Vector2(left, player.position.Y),
                player.width + (int)lookahead, player.height)) return RejectStep(out reason, "body_sweep_hud_obstructed");
            int firstX = (int)Math.Floor(left / 16f);
            int lastX = (int)Math.Ceiling(right / 16f) - 1;
            int firstY = (int)Math.Floor(player.position.Y / 16f);
            int nearFloorY = (int)Math.Floor((baseFeet - 16f) / 16f);
            int footRow = (int)Math.Floor(baseFeet / 16f);
            int lastProbeY = (int)Math.Floor((baseFeet + 16f) / 16f);
            if (firstY < 1 || lastX < firstX || lastX - firstX > 6 || lastProbeY - firstY > 9)
                return RejectStep(out reason, "body_cell_budget");

            int currentFirstX = (int)Math.Floor(player.position.X / 16f);
            int currentLastX = (int)Math.Ceiling((player.position.X + player.width) / 16f) - 1;
            var supportLevels = new int[lastX - firstX + 1];
            bool currentDirectSupport = false, hasUpStep = false, hasDownStep = false;
            int lowestLevel = 0;

            // Scan downward only through legally visible passable cells. The
            // first ordinary full/half floor terminates this column immediately;
            // its buried lower tile is never queried. Levels are in 8px units.
            for (int x = firstX; x <= lastX; ++x)
            {
                bool foundFloor = false;
                for (int y = firstY; y <= lastProbeY; ++y)
                {
                    Tile top = null;
                    bool topVisible = y >= nearFloorY && visibility.TryReadWalkingTile(x, y, true, baseFeet, player, out top);
                    if (topVisible && WalkSupportCell(top, x, y, player))
                    {
                        float surface = y * 16f + (top.halfBrick() ? 8f : 0f);
                        if (surface < baseFeet - 16f || surface > baseFeet + 16f)
                            return RejectStep(out reason, "support_height_budget");
                        int level = (int)Math.Round((surface - baseFeet) / 8f);
                        if (x >= currentFirstX && x <= currentLastX && level < 0)
                            return RejectStep(out reason, "up_current_footprint_overlap");
                        // SolidTop surfaces do not auto-step without controlUp.
                        if (level < 0 && Main.tileSolidTop[top.type])
                            return RejectStep(out reason, "up_solidtop_unsupported");
                        if (top.halfBrick())
                        {
                            if (!visibility.ConfirmWalkingHalfFloor(x, y, top, player))
                                return RejectStep(out reason, "half_surface_visibility");
                            Tile upperSpace;
                            if (!visibility.TryReadWalkingBodyTile(x, y, surface, baseFeet, player, out upperSpace))
                                return RejectStep(out reason, "half_upper_space_visibility");
                            if (!WalkBodySection(upperSpace, x, y, surface, player, visibility))
                                return RejectStep(out reason, "half_upper_space_unsafe");
                        }
                        supportLevels[x - firstX] = level;
                        if (level == 0 && x >= currentFirstX && x <= currentLastX) currentDirectSupport = true;
                        if (level < 0) { hasUpStep = true; lowestLevel = Math.Min(lowestLevel, level); }
                        if (level > 0) hasDownStep = true;
                        foundFloor = true;
                        break;
                    }
                    Tile space;
                    if (!visibility.TryReadWalkingTile(x, y, false, baseFeet, player, out space))
                        return RejectStep(out reason, y >= footRow ? "drop_space_visibility" : "body_visibility");
                    if (!WalkBodyCell(space, x, y, player))
                    {
                        if (y >= footRow) return RejectStep(out reason, "drop_space_" + UnsafeBodyCategory(space));
                        if (y >= nearFloorY && topVisible && top != null && top.active())
                            return RejectStep(out reason, "up_support_" + UnsafeSupportCategory(top));
                        return RejectStep(out reason, "body_" + UnsafeBodyCategory(space));
                    }
                }
                if (!foundFloor) return RejectStep(out reason, "support_missing_within_16px");
            }
            if (!currentDirectSupport) return RejectStep(out reason, "no_current_foot_support");
            if (hasUpStep && hasDownStep) return RejectStep(out reason, "mixed_up_down_levels");
            int previousLevel = 0;
            int leadingX = direction > 0 ? currentLastX : currentFirstX;
            int endX = direction > 0 ? lastX : firstX;
            for (int x = leadingX; direction > 0 ? x <= endX : x >= endX; x += direction)
            {
                int level = supportLevels[x - firstX];
                if ((hasDownStep && level < previousLevel) || (hasUpStep && level > previousLevel))
                    return RejectStep(out reason, "nonmonotonic_support_levels");
                previousLevel = level;
            }
            if (hasUpStep)
            {
                float rise = lowestLevel * 8f;
                float raisedY = player.position.Y + rise;
                float raisedFeet = actualFeet + rise;
                if (!visibility.ContainsRectangle(new Vector2(left, raisedY),
                    player.width + (int)lookahead, player.height)) return RejectStep(out reason, "raised_headroom_hud_obstructed");
                int raisedFirstY = (int)Math.Floor(raisedY / 16f);
                int raisedLastY = (int)Math.Ceiling(raisedFeet / 16f) - 1;
                if (raisedFirstY < 1 || raisedLastY - raisedFirstY > 7)
                    return RejectStep(out reason, "raised_headroom_cell_budget");
                for (int x = firstX; x <= lastX; ++x)
                    for (int y = raisedFirstY; y <= raisedLastY; ++y)
                    {
                        Tile raisedBody;
                        if (!visibility.TryReadVisibleBodyTile(x, y, raisedFeet, out raisedBody))
                            return RejectStep(out reason, "raised_body_visibility");
                        if (!WalkBodySection(raisedBody, x, y, raisedFeet, player, visibility))
                            return RejectStep(out reason, "raised_body_" + UnsafeBodyCategory(raisedBody));
                    }
            }
            return true;
        }

        private static bool WalkBodySection(Tile tile, int x, int y, float bodyBottom,
            Player player, CaptureVisibility visibility)
        {
            if (WalkBodyCell(tile, x, y, player)) return true;
            // The half's lower 8px remain solid. Only a proven body section
            // entirely above its already observed surface is admitted.
            return tile != null && tile.halfBrick() && WalkSupportCell(tile, x, y, player) &&
                (visibility.IsObservedHalfFloor(x, y) || visibility.IsObservedWalkingHalfFloor(x, y)) &&
                bodyBottom <= y * 16f + 8f;
        }

        private static bool HalfSegmentHitsSolid(float eyeX, float eyeY, float targetX, float targetY, int x, int y)
        {
            if (!Finite(eyeX) || !Finite(eyeY) || !Finite(targetX) || !Finite(targetY)) return true;
            double enter = 0.0, leave = 1.0;
            // Inclusive bounds: touching the solid half or either corner blocks
            // the ray. These are the verified vanilla 8px lower-half bounds.
            return ClipHalfAxis(eyeX, (double)targetX - eyeX, x * 16.0, (x + 1.0) * 16.0, ref enter, ref leave) &&
                ClipHalfAxis(eyeY, (double)targetY - eyeY, y * 16.0 + 8.0, (y + 1.0) * 16.0, ref enter, ref leave);
        }

        private static bool ClipHalfAxis(double origin, double delta, double minimum, double maximum,
            ref double enter, ref double leave)
        {
            if (delta == 0.0) return origin >= minimum && origin <= maximum;
            double first = (minimum - origin) / delta, last = (maximum - origin) / delta;
            if (first > last) { double swap = first; first = last; last = swap; }
            enter = Math.Max(enter, first); leave = Math.Min(leave, last);
            return enter <= leave;
        }

        private static bool RejectStep(out string reason, string value)
        { reason = value; return false; }

        // These finite categories inspect only the already visibility-guarded
        // object used by the failed predicate. No additional tile, ray, hidden
        // coordinate or world scan is performed for diagnostics.
        private static string UnsafeBodyCategory(Tile tile)
        {
            if (tile == null) return "null";
            if (tile.invisibleBlock()) return "invisible";
            if (tile.inActive()) return "inactive";
            if (tile.liquid != 0) return "liquid";
            if (tile.halfBrick()) return "half";
            if (tile.slope() != 0) return "slope";
            if (!KnownSolidType(tile.type)) return "unknown_solidity";
            if (tile.type == TileID.WorkBenches)
            {
                if (Main.tileSolid[tile.type]) return "solid";
                if (!Main.tileSolidTop[tile.type]) return "bench_not_solidtop";
                if (tile.frameY != 0) return "frame";
                return "hurt";
            }
            if (tile.type != TileID.Plants && tile.type != TileID.Plants2 &&
                tile.type != TileID.Trees && tile.type != TileID.Torches &&
                tile.type != TileID.SmallPiles && tile.type != TileID.LargePiles && tile.type != TileID.LargePiles2 && tile.type != TileID.Tombstones)
                return Main.tileSolidTop[tile.type] ? "solidtop" :
                    Main.tileSolid[tile.type] ? "solid" : "unknown_kind";
            if (Main.tileSolid[tile.type]) return "solid";
            if (Main.tileSolidTop[tile.type]) return "solidtop";
            // Reached only after WalkBodyCell failed its normal pure hurt check.
            return "hurt";
        }

        private static string UnsafeSupportCategory(Tile tile)
        {
            if (tile == null) return "null";
            if (!tile.active()) return "empty";
            if (tile.invisibleBlock()) return "invisible";
            if (tile.inActive()) return "inactive";
            if (tile.liquid != 0) return "liquid";
            if (tile.halfBrick()) return "half";
            if (tile.slope() != 0) return "slope";
            if (!KnownSolidType(tile.type)) return "unknown_solidity";
            bool ordinaryFloor = tile.type == TileID.Dirt || tile.type == TileID.Grass ||
                tile.type == TileID.Stone || tile.type == TileID.WoodBlock;
            if (ordinaryFloor)
            {
                if (!Main.tileSolid[tile.type]) return "not_solid";
                if (Main.tileSolidTop[tile.type]) return "solidtop";
                return "hurt";
            }
            if (tile.type == TileID.Platforms)
            {
                if (!Main.tileSolidTop[tile.type]) return "not_solidtop";
                if (tile.frameY != 0) return "frame";
                return "hurt";
            }
            return "unknown_kind";
        }

        private static bool WalkBodyCell(Tile tile, int x, int y, Player player)
        {
            if (tile == null || tile.invisibleBlock() || tile.inActive() || tile.liquid != 0 ||
                tile.halfBrick() || tile.slope() != 0) return false;
            if (!tile.active()) return true;
            // Exact 1.4.5.8 Initialize_TileAndNPCData2 sets Plants/Torches/Trees
            // non-solid. Runtime tables still decide after the visibility guard.
            // SmallPiles/LargePiles/LargePiles2 retain false solid/top defaults
            // and have no CanTileHurt category in the verified initialization.
            // Walking through visible decoration does not classify it as stone.
            // TileCollision ignores horizontal/top collisions for solidTop.
            // Only a visible ordinary WorkBench top frame is admitted here;
            // its support/step-up role remains excluded from WalkSupportCell.
            if (tile.type == TileID.WorkBenches)
                return KnownSolidType(tile.type) && !Main.tileSolid[tile.type] &&
                    Main.tileSolidTop[tile.type] && tile.frameY == 0 &&
                    !Collision.CanTileHurt(tile.type, x, y, player);
            if (tile.type != TileID.Plants && tile.type != TileID.Plants2 &&
                tile.type != TileID.Trees && tile.type != TileID.Torches &&
                tile.type != TileID.SmallPiles && tile.type != TileID.LargePiles && tile.type != TileID.LargePiles2 && tile.type != TileID.Tombstones)
                return false;
            return KnownSolidType(tile.type) && !Main.tileSolid[tile.type] && !Main.tileSolidTop[tile.type] &&
                !Collision.CanTileHurt(tile.type, x, y, player);
        }

        private static bool WalkSupportCell(Tile tile, int x, int y, Player player)
        {
            if (tile == null || !tile.active() || tile.invisibleBlock() || tile.inActive() ||
                tile.liquid != 0 || tile.slope() != 0 || !KnownSolidType(tile.type))
                return false;
            bool ordinaryFloor = tile.type == TileID.Dirt || tile.type == TileID.Grass ||
                tile.type == TileID.Stone || tile.type == TileID.WoodBlock;
            bool supported = ordinaryFloor ? Main.tileSolid[tile.type] && !Main.tileSolidTop[tile.type] :
                tile.type == TileID.Platforms && Main.tileSolidTop[tile.type] && tile.frameY == 0 && !tile.halfBrick();
            // Verified CanTileHurt is a pure type/player check, with no tile or
            // coordinate reads; never call the broader collision/world scanners.
            return supported && !Collision.CanTileHurt(tile.type, x, y, player);
        }

        private static bool WorldUiUnobstructed(Player player)
        {
            // Verified vanilla: style 1 draws minimap, style 2 draws overlay;
            // mapEnabled=false prevents both through the normal configuration.
            // Inventory remains summarized, but covered world targets are empty.
            return !Main.mapFullscreen && !(Main.mapEnabled && Main.mapStyle != 0) &&
                !Main.playerInventory && !Main.ingameOptionsWindow && player.chest < 0;
        }

        private static bool KnownSolidType(ushort type)
        { return Main.tileSolid != null && Main.tileSolidTop != null && type < Main.tileSolid.Length && type < Main.tileSolidTop.Length; }

        private static bool Finite(float value)
        { return !float.IsNaN(value) && !float.IsInfinity(value); }

        private static int AddCount(int current, int addition)
        { return (int)Math.Min(int.MaxValue, (long)current + addition); }

        private static void AddNearest(List<VisibleTarget> targets, int x, int y, int centerX, int centerY,
            int maximum = GameplayObservation.MaxTargetsPerKind)
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
            if (index >= maximum) return;
            targets.Insert(index, new VisibleTarget { TileX = x, TileY = y });
            if (targets.Count > maximum) targets.RemoveAt(targets.Count - 1);
        }

        private sealed class CaptureVisibility
        {
            private readonly Vector2 _eye;
            private readonly Vector2 _position;
            private readonly Vector2 _size;
            private readonly float _safeTop;
            private readonly float _safeBottom;
            private readonly float _safeRight;
            private readonly Dictionary<long, bool> _lighting = new Dictionary<long, bool>();
            // Per-Capture only; a shape enters this set only after its actual
            // 8.5px surface endpoint passes the full view/light/LOS guard.
            private readonly HashSet<long> _observedHalfFloors = new HashSet<long>();
            private const int MaxObservedHalfFloors = 64;
            // A walking-only, screen-exposed column proof, not another eye LOS.
            // Direct eye observations provide one whole-air anchor per column;
            // secondary air can never provide an anchor. No other observer uses
            // these caches, and each Capture constructs a fresh instance.
            private const int MaxWalkingColumns = 16;
            private readonly Dictionary<int, WalkingAnchor> _walkingAnchors = new Dictionary<int, WalkingAnchor>();
            private readonly Dictionary<int, WalkingColumn> _walkingColumns = new Dictionary<int, WalkingColumn>();
            private readonly HashSet<long> _walkingHalfFloors = new HashSet<long>();

            private sealed class WalkingAnchor
            {
                internal int Y;
                internal float BaseFeet;
                internal Tile Air;
            }

            private sealed class WalkingColumn
            {
                internal int AnchorY, NextY, FloorY = -1;
                internal float BaseFeet;
                internal bool Ended;
                internal Tile Floor;
                internal readonly Dictionary<int, Tile> Air = new Dictionary<int, Tile>();
            }

            internal sealed class SlopeFloorProof
            {
                internal readonly Tile Observed;
                internal readonly ushort Type;
                internal readonly WalkingFloorCell Cell;
                internal SlopeFloorProof(Tile observed, WalkingFloorCell cell)
                { Observed = observed; Type = observed.type; Cell = cell; }
            }

            internal sealed class SlopeScope
            {
                internal readonly int FirstX, LastX, FirstY, LastY;
                internal readonly Dictionary<int, SlopeFloorProof> Floors = new Dictionary<int, SlopeFloorProof>();
                internal bool BodyProfileProven;
                internal SlopeScope(int firstX, int lastX, int firstY, int lastY)
                { FirstX = firstX; LastX = lastX; FirstY = firstY; LastY = lastY; }
            }

            internal CaptureVisibility(Vector2 eye, Vector2 position, Vector2 size)
            {
                _eye = eye; _position = position; _size = size;
                _safeTop = float.PositiveInfinity; _safeBottom = float.NegativeInfinity;
                _safeRight = float.NegativeInfinity;
                if (Main.GameViewMatrix == null || Main.screenWidth <= 0 || Main.screenHeight <= 0 ||
                    Main.GameViewMatrix.Effects != Microsoft.Xna.Framework.Graphics.SpriteEffects.None) return;
                Vector2 zoom = Main.GameViewMatrix.RenderZoom;
                float uiScale = Main.UIScale;
                if (!Finite(zoom.X) || !Finite(zoom.Y) || zoom.X <= 0 || zoom.Y <= 0 ||
                    !Finite(uiScale) || uiScale <= 0) return;
                // Verified Camera/SpriteViewMatrix IL: ScaledPosition is the
                // translated world origin, and RenderZoom is the actual rounded
                // world-to-screen scale. UIScale scales the HUD independently.
                // Keep top 120, bottom/right 64 world pixels, or the larger
                // screen HUD band converted through UI/world scales. Two pixels
                // cover viewport-translation rounding and the render offset.
                float hudScale = Math.Max(1f, uiScale);
                _safeTop = position.Y + Math.Max(120f, 120f * hudScale / zoom.Y) + 2f;
                _safeBottom = position.Y + size.Y - Math.Max(64f, 64f * hudScale / zoom.Y) - 2f;
                _safeRight = position.X + size.X - Math.Max(64f, 64f * hudScale / zoom.X) - 2f;
            }

            internal bool ContainsPoint(Vector2 point)
            {
                return point.X >= _position.X && point.Y >= _safeTop &&
                    point.X < _safeRight && point.Y < _safeBottom;
            }

            internal bool ContainsRectangle(Vector2 position, int width, int height)
            {
                return Finite(position.X) && Finite(position.Y) && width > 0 && height > 0 && width <= 512 && height <= 512 &&
                    position.X >= _position.X && position.Y >= _safeTop &&
                    position.X + width <= _safeRight && position.Y + height <= _safeBottom;
            }

            internal bool TryReadVisibleTile(int x, int y, bool supportTopSurface, out Tile tile)
            {
                return TryReadVisibleTilePoint(x, y, y * 16f + (supportTopSurface ? 0.5f : 8f), out tile);
            }

            internal bool TryReadVisibleBodyTile(int x, int y, float bodyBottom, out Tile tile)
            {
                float start = y * 16f, end = Math.Min((y + 1) * 16f, bodyBottom);
                if (!Finite(bodyBottom) || end <= start) { tile = null; return false; }
                return TryReadVisibleTilePoint(x, y, (start + end) * 0.5f, out tile);
            }

            private bool TryReadVisibleTilePoint(int x, int y, float pointY, out Tile tile)
            {
                tile = null;
                if (!InViewAndLit(x, y)) return false;
                // For a full support block only its exposed top surface is the
                // sight endpoint. Intermediate solids still block the ray.
                var target = new Vector2(x * 16f + 8f, pointY);
                if (!ClearVisibleRay(target)) return false;
                tile = Main.tile[x, y];
                return tile != null && !tile.invisibleBlock();
            }

            internal bool TryReadVisibleDirtSurface(int x, int y, float feet, out Tile tile)
            {
                tile = null;
                Tile observed;
                // Keep the original primary eye LOS and guard-before-read rule.
                // No walking-column fallback, secondary seed or shape cache.
                if (!Finite(feet) || !TryReadVisibleTile(x, y, true, out observed) || !MineableDirtShape(observed)) return false;
                int slope = observed.slope();
                bool half = observed.halfBrick();
                for (int sample = 0; sample < 3; ++sample)
                {
                    float localX = 2f + sample * 6f;
                    float surface = half ? 8f : slope == 1 ? localX : slope == 2 ? 16f - localX : 0f;
                    float faceX = x * 16f + localX, faceY = y * 16f + surface;
                    if (Math.Abs(faceY - feet) > 16f) continue;
                    // The endpoint lies just inside the real face, avoiding a
                    // tile-boundary endpoint while exposing no buried cell.
                    if (!InViewAndLit(x, y) || !ClearVisibleRay(new Vector2(faceX, faceY + 0.25f)) ||
                        !DirtSurfaceIsFirstHit(_eye.X, _eye.Y, faceX, faceY, x, y, slope, half)) continue;
                    tile = observed;
                    return true;
                }
                return false;
            }

            internal bool TryReadSlopePrimaryTile(int x, int y, bool top, SlopeScope scope, out Tile tile)
            { return TryReadSlopeTilePoint(x, y, y * 16f + (top ? 0.5f : 8f), scope, false, out tile); }

            internal bool TryReadSlopeBodyTile(int x, int y, SlopeScope scope, out Tile tile)
            { return TryReadSlopeTilePoint(x, y, y * 16f + 8f, scope, true, out tile); }

            private bool TryReadSlopeTilePoint(int x, int y, float pointY, SlopeScope scope,
                bool allowProvenUpperTriangle, out Tile tile)
            {
                tile = null;
                if (!SlopeScopeAllowsCell(x, y, scope) || !InViewAndLit(x, y)) return false;
                var target = new Vector2(x * 16f + 8f, pointY);
                if (!ClearSlopeRay(target, scope, allowProvenUpperTriangle)) return false;
                tile = Main.tile[x, y];
                return tile != null && !tile.invisibleBlock();
            }

            internal bool ProveSlopeFloorFace(int x, int y, float feet, Tile observed, SlopeScope scope)
            {
                if (!Finite(feet) || observed == null || observed.halfBrick() || observed.slope() > 2 ||
                    !SlopeScopeAllowsCell(x, y, scope) || !InViewAndLit(x, y)) return false;
                int shape = observed.slope();
                for (int sample = 0; sample < 3; ++sample)
                {
                    float localX = 2f + sample * 6f;
                    float offset = shape == 1 ? localX : shape == 2 ? 16f - localX : 0f;
                    float faceX = x * 16f + localX, faceY = y * 16f + offset;
                    if (Math.Abs(faceY - feet) > 16f ||
                        !ClearSlopeRay(new Vector2(faceX, faceY + 0.25f), scope, false) ||
                        !DirtSurfaceIsFirstHit(_eye.X, _eye.Y, faceX, faceY, x, y, shape, false)) continue;
                    return true;
                }
                return false;
            }

            private static bool SlopeScopeAllowsCell(int x, int y, SlopeScope scope)
            {
                if (scope == null || scope.LastX < scope.FirstX ||
                    scope.LastX - scope.FirstX + 1 > WalkingSlopeGeometry.MaxFirstFloorCells ||
                    scope.Floors.Count > WalkingSlopeGeometry.MaxFirstFloorCells ||
                    x < scope.FirstX || x > scope.LastX || y < scope.FirstY || y > scope.LastY) return false;
                SlopeFloorProof floor;
                return !scope.Floors.TryGetValue(x, out floor) || y <= floor.Cell.TileY;
            }

            private bool ClearSlopeRay(Vector2 target, SlopeScope scope, bool allowProvenUpperTriangle)
            {
                if (allowProvenUpperTriangle && (scope == null || !scope.BodyProfileProven)) return false;
                return VisibleRay.IsClear(_eye.X, _eye.Y, target.X, target.Y,
                    (x, y, endpoint) => SlopeRayCellClear(x, y, endpoint, target, scope, allowProvenUpperTriangle));
            }

            private bool SlopeRayCellClear(int x, int y, bool endpoint, Vector2 target, SlopeScope scope,
                bool allowProvenUpperTriangle)
            {
                // The column's first proven solid is a hard lower read bound,
                // even when another target ray might enter that column sideways.
                if (!SlopeScopeAllowsCell(x, y, scope) || !InViewAndLit(x, y)) return false;
                SlopeFloorProof floor;
                if (allowProvenUpperTriangle && scope.BodyProfileProven &&
                    scope.Floors.TryGetValue(x, out floor) && floor.Cell.TileY == y && floor.Cell.Shape != 0)
                {
                    Tile current = Main.tile[x, y];
                    if (!object.ReferenceEquals(current, floor.Observed) || current.type != floor.Type ||
                        !current.active() || current.inActive() || current.invisibleBlock() || current.liquid != 0 ||
                        current.halfBrick() || current.slope() != floor.Cell.Shape || !KnownSolidType(current.type) ||
                        !Main.tileSolid[current.type] || Main.tileSolidTop[current.type]) return false;
                    return !SlopeSegmentHitsSolid(_eye.X, _eye.Y, target.X, target.Y, x, y, floor.Cell.Shape);
                }
                // All other cells, and every floor-proof ray, use the original
                // own-eye optical predicate. No secondary column seed is used.
                return RayCellClear(x, y, endpoint, target);
            }

            internal bool ConfirmHalfFloor(int x, int y, Tile tile, Player player)
            {
                if (tile == null || !tile.halfBrick() || !WalkSupportCell(tile, x, y, player) || !InViewAndLit(x, y)) return false;
                long key = ((long)x << 32) | (uint)y;
                if (!_observedHalfFloors.Contains(key) && _observedHalfFloors.Count >= MaxObservedHalfFloors) return false;
                if (!ClearVisibleRay(new Vector2(x * 16f + 8f, y * 16f + 8.5f))) return false;
                _observedHalfFloors.Add(key);
                return true;
            }

            internal bool IsObservedHalfFloor(int x, int y)
            { return _observedHalfFloors.Contains(((long)x << 32) | (uint)y); }

            internal bool TryReadWalkingTile(int x, int y, bool supportTopSurface, float baseFeet,
                Player player, out Tile tile)
            {
                if (TryReadVisibleTile(x, y, supportTopSurface, out tile))
                {
                    RecordWalkingDirectAir(x, y, baseFeet, tile);
                    return true;
                }
                return TryReadExposedColumnTile(x, y, y * 16f + (supportTopSurface ? 0.5f : 8f),
                    baseFeet, player, out tile);
            }

            internal bool TryReadWalkingBodyTile(int x, int y, float bodyBottom, float baseFeet,
                Player player, out Tile tile)
            {
                if (TryReadVisibleBodyTile(x, y, bodyBottom, out tile)) return true;
                float start = y * 16f, end = Math.Min((y + 1) * 16f, bodyBottom);
                if (!Finite(bodyBottom) || end <= start) { tile = null; return false; }
                return TryReadExposedColumnTile(x, y, (start + end) * 0.5f, baseFeet, player, out tile);
            }

            private void RecordWalkingDirectAir(int x, int y, float baseFeet, Tile tile)
            {
                // The fixed anchor is entirely above the current feet even for
                // an 8px half-floor offset. Never seed from secondary results.
                if (!Finite(baseFeet) || y != (int)Math.Floor(baseFeet / 16f) - 1 ||
                    !WalkingWholeAir(tile)) return;
                WalkingAnchor previous;
                if (_walkingAnchors.TryGetValue(x, out previous)) return;
                if (_walkingAnchors.Count >= MaxWalkingColumns) return;
                _walkingAnchors.Add(x, new WalkingAnchor { Y = y, BaseFeet = baseFeet, Air = tile });
            }

            private static bool WalkingWholeAir(Tile tile)
            {
                return tile != null && !tile.active() && !tile.invisibleBlock() && !tile.inActive() &&
                    tile.liquid == 0 && !tile.halfBrick() && tile.slope() == 0;
            }

            private bool TryReadExposedColumnTile(int x, int y, float pointY, float baseFeet,
                Player player, out Tile tile)
            {
                tile = null;
                WalkingAnchor anchor;
                if (!Finite(baseFeet) || !Finite(pointY) || y < (int)Math.Floor(baseFeet / 16f) ||
                    !_walkingAnchors.TryGetValue(x, out anchor) || anchor.BaseFeet != baseFeet ||
                    !WalkingWholeAir(anchor.Air) || !InViewAndLit(x, anchor.Y) ||
                    y <= anchor.Y || y > anchor.Y + 2) return false;
                WalkingColumn column;
                if (!_walkingColumns.TryGetValue(x, out column))
                {
                    if (_walkingColumns.Count >= MaxWalkingColumns) return false;
                    column = new WalkingColumn { AnchorY = anchor.Y, BaseFeet = baseFeet, NextY = anchor.Y + 1 };
                    column.Air.Add(anchor.Y, anchor.Air);
                    _walkingColumns.Add(x, column);
                }
                if (column.AnchorY != anchor.Y || column.BaseFeet != baseFeet) return false;
                while (!column.Ended && column.NextY <= y && column.NextY <= anchor.Y + 2)
                {
                    int probeY = column.NextY;
                    // Read only the next face after the known-air prefix has
                    // passed viewport/HUD/light and a straight downward ray.
                    if (!InViewAndLit(x, probeY) ||
                        !ClearWalkingColumnRay(x, probeY * 16f + 0.5f, column))
                    { column.Ended = true; break; }
                    Tile candidate = Main.tile[x, probeY];
                    if (WalkingWholeAir(candidate))
                    {
                        column.Air.Add(probeY, candidate);
                        ++column.NextY;
                        continue;
                    }
                    // Any first foreground, null, liquid or unknown terminates
                    // the column. Never scan through it to find a safe floor.
                    column.Ended = true;
                    if (!WalkSupportCell(candidate, x, probeY, player)) break;
                    float surface = probeY * 16f + (candidate.halfBrick() ? 8f : 0f);
                    if (surface < baseFeet || surface > baseFeet + 16f) break;
                    if (candidate.halfBrick())
                    {
                        if (!ClearWalkingColumnRay(x, probeY * 16f + 8.5f, column)) break;
                        _walkingHalfFloors.Add(((long)x << 32) | (uint)probeY);
                    }
                    column.FloorY = probeY;
                    column.Floor = candidate;
                }
                Tile cached;
                bool air = column.Air.TryGetValue(y, out cached);
                if (air ? !WalkingWholeAir(cached) :
                    (y != column.FloorY || !WalkSupportCell(column.Floor, x, y, player))) return false;
                if (!air) cached = column.Floor;
                if (!InViewAndLit(x, y) || !ClearWalkingColumnRay(x, pointY, column)) return false;
                tile = cached;
                return true;
            }

            private bool ClearWalkingColumnRay(int x, float pointY, WalkingColumn column)
            {
                float startY = column.AnchorY * 16f + 8f;
                if (!Finite(pointY) || pointY < startY || pointY >= (column.AnchorY + 3) * 16f) return false;
                return VisibleRay.IsClear(x * 16f + 8f, startY, x * 16f + 8f, pointY,
                    (rayX, rayY, endpoint) =>
                    {
                        if (rayX != x || rayY < column.AnchorY || rayY > column.AnchorY + 2 ||
                            !InViewAndLit(rayX, rayY)) return false;
                        if (endpoint) return true;
                        Tile knownAir;
                        return column.Air.TryGetValue(rayY, out knownAir) && WalkingWholeAir(knownAir);
                    });
            }

            internal bool ConfirmWalkingHalfFloor(int x, int y, Tile tile, Player player)
            {
                if (!IsObservedWalkingHalfFloor(x, y)) return ConfirmHalfFloor(x, y, tile, player);
                WalkingColumn column;
                return _walkingColumns.TryGetValue(x, out column) && column.FloorY == y &&
                    object.ReferenceEquals(column.Floor, tile) && WalkSupportCell(tile, x, y, player) &&
                    InViewAndLit(x, y) && ClearWalkingColumnRay(x, y * 16f + 8.5f, column);
            }

            internal bool IsObservedWalkingHalfFloor(int x, int y)
            { return _walkingHalfFloors.Contains(((long)x << 32) | (uint)y); }

            private bool InViewAndLit(int x, int y)
            {
                if (x < 1 || y < 1 || x >= Main.maxTilesX - 1 || y >= Main.maxTilesY - 1 ||
                    x * 16f < _position.X || y * 16f < _safeTop ||
                    (x + 1) * 16f > _safeRight || (y + 1) * 16f > _safeBottom)
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
            // tile. Slopes and unknown halves remain opaque. Only an already
            // observed ordinary half uses the exact lower-8px bounds below.
            private bool ClearVisibleRay(Vector2 target)
            {
                return VisibleRay.IsClear(_eye.X, _eye.Y, target.X, target.Y,
                    (x, y, endpoint) => RayCellClear(x, y, endpoint, target));
            }

            private bool RayCellClear(int x, int y, bool endpoint, Vector2 target)
            {
                if (!InViewAndLit(x, y)) return false;
                if (endpoint) return true; // Only the facing endpoint may be solid.
                Tile tile = Main.tile[x, y];
                if (tile == null || tile.invisibleBlock()) return false;
                if (tile.halfBrick())
                {
                    // Unknown, inactive, wet, sloped or unsupported half shapes
                    // remain fully opaque. An observed half admits only rays
                    // which never touch its solid lower 8px.
                    bool ordinaryFloor = tile.type == TileID.Dirt || tile.type == TileID.Grass ||
                        tile.type == TileID.Stone || tile.type == TileID.WoodBlock;
                    return IsObservedHalfFloor(x, y) && ordinaryFloor && tile.active() && !tile.inActive() &&
                        tile.liquid == 0 && tile.slope() == 0 && KnownSolidType(tile.type) &&
                        Main.tileSolid[tile.type] && !Main.tileSolidTop[tile.type] &&
                        !HalfSegmentHitsSolid(_eye.X, _eye.Y, target.X, target.Y, x, y);
                }
                if (!tile.active() || tile.inActive()) return true;
                if (!KnownSolidType(tile.type)) return false;
                return !Main.tileSolid[tile.type] || Main.tileSolidTop[tile.type];
            }
        }
    }
}
