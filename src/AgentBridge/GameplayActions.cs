using System;
using System.Collections.Generic;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ObjectData;
using TerrariaAgent.Protocol;

namespace TerrariaAgent.Bridge
{
    internal sealed class GameplayActionFailure : InvalidOperationException
    {
        internal GameplayActionFailure(string reason) : base(reason) { }
    }

    // Game-thread, explicitly enabled preview only. Crafting runs inside
    // Startup's STOP/gate transaction. No terrain, stack or damage writes.
    internal static class GameplayActions
    {
        private static string _lastCraftSession;
        private static long _lastCraftSequence;
        private sealed class RecipeSpec
        {
            internal readonly string Id;
            internal readonly int Output, Stack, Station, Wood, Stone, Gel;
            internal RecipeSpec(string id, int output, int stack, int station, int wood, int stone = 0, int gel = 0)
            { Id = id; Output = output; Stack = stack; Station = station; Wood = wood; Stone = stone; Gel = gel; }
        }
        // Verified SetupRecipes in 1.4.5.8: bow/sword/arrow need a Workbench.
        // Arrow and Torch preserve vanilla Wood groups. This preview also
        // requires these ordinary own material counts before requesting a craft.
        private static readonly RecipeSpec[] Recipes = {
            new RecipeSpec(GameplayRecipeIds.WorkBench, ItemID.WorkBench, 1, -1, 10),
            new RecipeSpec(GameplayRecipeIds.WoodenBow, ItemID.WoodenBow, 1, TileID.WorkBenches, 10),
            new RecipeSpec(GameplayRecipeIds.WoodenArrow, ItemID.WoodenArrow, 25, TileID.WorkBenches, 1, 1),
            new RecipeSpec(GameplayRecipeIds.WoodenSword, ItemID.WoodenSword, 1, TileID.WorkBenches, 7),
            new RecipeSpec(GameplayRecipeIds.WoodPlatform, ItemID.WoodPlatform, 2, -1, 1),
            new RecipeSpec(GameplayRecipeIds.Torch, ItemID.Torch, 3, -1, 1, 0, 1)
        };
        internal static bool CanCraftWorkBench(Player player)
        { Recipe recipe; return TryRecipe(player, GameplayRecipeIds.WorkBench, out recipe); }
        internal static string[] CanCraftRecipes(Player player)
        {
            var result = new List<string>(GameplayRecipeIds.MaxRecipes);
            if (!PrepareRecipes(player)) return result.ToArray();
            foreach (RecipeSpec spec in Recipes)
            { Recipe recipe; if (FindRecipe(player, spec, out recipe)) result.Add(spec.Id); }
            return result.ToArray();
        }
        internal static void Apply(Player player, InputState input, LeaseSnapshot lease, LeaseGate gate,
            Action<string, string> diagnostic, Func<Action, bool> commitTurn)
        {
            if (input.SelectedSlot >= 0)
            {
                if (player.inventory == null || input.SelectedSlot >= Math.Min(50, player.inventory.Length))
                    throw new GameplayActionFailure("gameplay:invalid_inventory_slot");
                Item requested = player.inventory[input.SelectedSlot];
                if (requested == null || requested.IsAir) { player.controlUseItem = false; return; }
                if (!SupportedItem(requested)) throw new GameplayActionFailure("gameplay:unsupported_selected_item");
                if (player.selectedItem != input.SelectedSlot) player.selectedItemState.Select(input.SelectedSlot);
            }
            if (input.CraftWorkBench && !string.IsNullOrEmpty(input.CraftRecipe))
                throw new GameplayActionFailure("craft:conflicting_recipe_request");
            string recipeId = input.CraftWorkBench ? GameplayRecipeIds.WorkBench : input.CraftRecipe;
            if (!string.IsNullOrEmpty(recipeId) && (!string.Equals(_lastCraftSession, lease.SessionId, StringComparison.Ordinal) || lease.LastSequence != _lastCraftSequence))
            {
                // Spend before invoking vanilla, including ambiguous failures.
                _lastCraftSession = lease.SessionId; _lastCraftSequence = lease.LastSequence;
                RecipeSpec spec = FindSpec(recipeId); Recipe recipe;
                if (spec == null || !TryRecipe(player, recipeId, out recipe))
                    throw new GameplayActionFailure("craft:materials_environment_or_space_unavailable");
                LeaseSnapshot current = gate.Snapshot();
                if (current.State != ControlState.Agent || current.LastSequence != lease.LastSequence ||
                    current.SessionId != lease.SessionId || current.WorldId != lease.WorldId)
                    throw new GameplayActionFailure("craft:lease_revoked_before_normal_craft");
                int woodBefore = Count(player, ItemID.Wood), outputBefore = Count(player, spec.Output);
                CraftingRequests.CraftItem(recipe, 1, true);
                int woodAfter = Count(player, ItemID.Wood), outputAfter = Count(player, spec.Output);
                diagnostic("craft_result", "normalCraft=True;recipe=" + spec.Id + ";woodBefore=" + woodBefore +
                    ";woodAfter=" + woodAfter + ";outputBefore=" + outputBefore + ";outputAfter=" + outputAfter +
                    ";outputStack=" + spec.Stack + ";actionSeq=" + lease.LastSequence);
                if (outputAfter != outputBefore + spec.Stack || (recipeId == GameplayRecipeIds.WorkBench && woodAfter != woodBefore - 10))
                    throw new GameplayActionFailure("craft:normal_result_did_not_match_inventory_delta");
            }
            if (!input.UseItem || input.SelectedSlot < 0 || player.selectedItem != input.SelectedSlot) return;
            Item selected = player.inventory[input.SelectedSlot];
            if (selected == null || selected.IsAir) { player.controlUseItem = false; return; }
            GameplayObservation visible = VisibleEnvironment.Capture(player);
            bool bench = selected.type == ItemID.WorkBench && selected.createTile == TileID.WorkBenches;
            bool platform = selected.type == ItemID.WoodPlatform && selected.createTile == TileID.Platforms;
            bool torch = selected.type == ItemID.Torch && selected.createTile == TileID.Torches;
            bool bow = ArrowBow(selected);
            bool allowed = selected.pick > 0 ? (Contains(visible.StoneTargets, input.AimTileX, input.AimTileY) ||
                Contains(visible.DirtTargets, input.AimTileX, input.AimTileY)) :
                selected.axe > 0 ? Contains(visible.TreeTargets, input.AimTileX, input.AimTileY) :
                bench ? Contains(visible.PlacementTargets, input.AimTileX, input.AimTileY) :
                platform ? Contains(visible.PlatformPlacementTargets, input.AimTileX, input.AimTileY) :
                torch ? Contains(visible.TorchPlacementTargets, input.AimTileX, input.AimTileY) :
                (MeleeWeapon(selected) || bow) && VisibleEnemyAim(visible.Enemies, input.AimTileX, input.AimTileY) &&
                    VisibleEnvironment.CanAimAt(player, input.AimTileX, input.AimTileY);
            if (!allowed || (bow && !player.HasAmmo(selected, true)))
            { player.controlUseItem = false; return; }
            int aimX = input.AimTileX, aimY = input.AimTileY;
            if (MeleeWeapon(selected) && selected.useStyle == 1)
            {
                // 1.4.5.8 ordinary swing hitboxes use Entity.direction. Wooden
                // Sword has useTurn=false: mouse aim alone does not turn it.
                // Turn only toward this freshly filtered visible enemy, between
                // swings. ChangeDir's pulley branch can move the player, so
                // reject all special movement before calling the normal method.
                int facing = aimX * 16 + 8 < player.Center.X ? -1 : 1;
                if (player.direction != facing)
                {
                    player.controlUseItem = false;
                    if (player.itemAnimation != 0) return;
                    if (player.pulley || player.grapCount > 0 || player.sandStorm ||
                        (player.mount != null && player.mount.Active))
                        throw new GameplayActionFailure("combat:turn_requires_ordinary_unmounted_state");
                    if (commitTurn == null || !commitTurn(() => player.ChangeDir(facing))) return;
                }
            }
            if (bench || platform || torch)
            {
                TileObjectData data = TileObjectData.GetTileData(selected.createTile, selected.placeStyle, 0);
                if (data == null || data.Width != (bench ? 2 : 1) || data.Height != 1)
                    throw new GameplayActionFailure("place:unverified_object_shape");
                aimX += data.Origin.X; aimY += data.Origin.Y;
            }
            // ItemCheck owns reach, mining, ammo, animation and damage. Normal
            // release ticks are required; releaseUseItem/itemTime are untouched.
            Main.mouseX = (int)(aimX * 16 + 8 - Main.screenPosition.X);
            Main.mouseY = (int)(aimY * 16 + 8 - Main.screenPosition.Y);
            player.controlUseItem = true;
        }
        private static bool SupportedItem(Item item)
        { return item.pick > 0 || item.axe > 0 || MeleeWeapon(item) || ArrowBow(item) ||
            item.type == ItemID.WorkBench || item.type == ItemID.WoodPlatform || item.type == ItemID.Torch; }
        internal static bool MeleeWeapon(Item item)
        { return item != null && !item.IsAir && item.melee && !item.noMelee && item.damage > 0 && item.mana == 0 && item.pick <= 0 && item.axe <= 0; }
        internal static bool ArrowBow(Item item)
        { return item != null && !item.IsAir && item.ranged && item.useAmmo == AmmoID.Arrow &&
            item.shoot > 0 && item.damage > 0 && item.mana == 0 && item.pick <= 0 && item.axe <= 0; }
        private static bool VisibleEnemyAim(VisibleEnemy[] enemies, int x, int y)
        {
            if (enemies == null) return false;
            foreach (VisibleEnemy enemy in enemies)
                if (enemy != null && Math.Abs(enemy.TileX - x) <= 1 && Math.Abs(enemy.TileY - y) <= 1) return true;
            return false;
        }
        private static bool Contains(VisibleTarget[] candidates, int x, int y)
        {
            if (candidates == null) return false;
            foreach (VisibleTarget candidate in candidates)
                if (candidate != null && candidate.TileX == x && candidate.TileY == y) return true;
            return false;
        }
        private static int Count(Player player, int type)
        {
            int total = 0;
            if (player.inventory == null) return 0;
            for (int i = 0; i < Math.Min(58, player.inventory.Length); ++i)
            { Item item = player.inventory[i]; if (item != null && item.type == type && item.stack > 0)
                total = (int)Math.Min(int.MaxValue, (long)total + item.stack); }
            return total;
        }
        private static RecipeSpec FindSpec(string id)
        { foreach (RecipeSpec spec in Recipes) if (spec.Id == id) return spec; return null; }
        private static bool TryRecipe(Player player, string id, out Recipe result)
        { result = null; RecipeSpec spec = FindSpec(id); return spec != null && PrepareRecipes(player) && FindRecipe(player, spec, out result); }
        private static bool PrepareRecipes(Player player)
        {
            if (player == null || player.dead || player.chest >= 0 || player.itemAnimation != 0 || Main.mouseItem == null || !Main.mouseItem.IsAir || player.inventory == null) return false;
            bool hasSpace = false;
            for (int i = 0; i < Math.Min(50, player.inventory.Length); ++i)
                if (player.inventory[i] == null || player.inventory[i].IsAir) { hasSpace = true; break; }
            if (!hasSpace) return false;
            player.AdjTiles(); Recipe.UpdateRecipeList();
            return Main.recipe != null && Main.availableRecipe != null;
        }
        private static bool FindRecipe(Player player, RecipeSpec spec, out Recipe result)
        {
            result = null;
            if (Count(player, ItemID.Wood) < spec.Wood || Count(player, ItemID.StoneBlock) < spec.Stone || Count(player, ItemID.Gel) < spec.Gel) return false;
            for (int i = 0; i < Math.Min(Main.numAvailableRecipes, Main.availableRecipe.Length); ++i)
            {
                int index = Main.availableRecipe[i]; if (index < 0 || index >= Main.recipe.Length) continue;
                Recipe recipe = Main.recipe[index];
                if (recipe == null || recipe.createItem == null || recipe.createItem.type != spec.Output || recipe.createItem.stack != spec.Stack || recipe.requiredTile != spec.Station || recipe.requiredItem == null) continue;
                int ingredients = 0; bool exact = true;
                foreach (Item ingredient in recipe.requiredItem)
                {
                    if (ingredient == null || ingredient.IsAir) continue;
                    ++ingredients;
                    int required = ingredient.type == ItemID.Wood ? spec.Wood : ingredient.type == ItemID.StoneBlock ? spec.Stone : ingredient.type == ItemID.Gel ? spec.Gel : 0;
                    if (required <= 0 || ingredient.stack != required) exact = false;
                }
                int kinds = (spec.Wood > 0 ? 1 : 0) + (spec.Stone > 0 ? 1 : 0) + (spec.Gel > 0 ? 1 : 0);
                if (!exact || ingredients != kinds || !recipe.PlayerMeetsEnvironmentConditions(player, null) ||
                    !Recipe.CollectedEnoughItemsToCraft(recipe) || !Main.CursorHasSpaceToCraftRecipe(recipe)) continue;
                result = recipe; return true;
            }
            return false;
        }
    }
}
