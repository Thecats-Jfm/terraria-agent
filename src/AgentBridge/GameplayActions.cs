using System;
using Microsoft.Xna.Framework;
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

    // Called only on the game update thread, and only for an explicitly enabled
    // B test. No tile destruction, placement, item grants or stack writes here.
    internal static class GameplayActions
    {
        private static string _lastCraftSession;
        private static long _lastCraftSequence;

        internal static bool CanCraftWorkBench(Player player)
        {
            Recipe recipe;
            return TryWorkbenchRecipe(player, out recipe);
        }

        internal static void Apply(Player player, InputState input, LeaseSnapshot lease, LeaseGate gate,
            Action<string, string> diagnostic)
        {
            if (input.SelectedSlot >= 0)
            {
                if (player.inventory == null || input.SelectedSlot >= Math.Min(50, player.inventory.Length))
                    throw new InvalidOperationException("gameplay:invalid_inventory_slot");
                Item requested = player.inventory[input.SelectedSlot];
                // A last workbench is normally consumed by placement before the
                // controller can observe it. Release the residual lease's use.
                if (requested == null || requested.IsAir) { player.controlUseItem = false; return; }
                if (requested.axe <= 0 && requested.type != ItemID.WorkBench)
                    throw new GameplayActionFailure("gameplay:unsupported_selected_item");
                if (player.selectedItem != input.SelectedSlot)
                    player.selectedItemState.Select(input.SelectedSlot);
            }

            if (input.CraftWorkBench &&
                (!string.Equals(_lastCraftSession, lease.SessionId, StringComparison.Ordinal) ||
                 lease.LastSequence != _lastCraftSequence))
            {
                // Spend the command before invoking vanilla. A thrown exception or
                // a renewed update cannot retry a craft that may have consumed wood.
                _lastCraftSession = lease.SessionId;
                _lastCraftSequence = lease.LastSequence;
                Recipe recipe;
                if (!TryWorkbenchRecipe(player, out recipe))
                    throw new GameplayActionFailure("craft:materials_environment_or_space_unavailable");
                // Snapshot reenters the same commit lock and expires a lease
                // if normal recipe qualification took longer than its deadline.
                if (gate.Snapshot().State != ControlState.Agent)
                    throw new GameplayActionFailure("craft:lease_expired_before_normal_craft");
                int woodBefore = Count(player, ItemID.Wood);
                int benchBefore = Count(player, ItemID.WorkBench);
                CraftingRequests.CraftItem(recipe, 1, true);
                int woodAfter = Count(player, ItemID.Wood);
                int benchAfter = Count(player, ItemID.WorkBench);
                diagnostic("craft_result", "normalCraft=True;woodBefore=" + woodBefore + ";woodAfter=" + woodAfter +
                    ";benchBefore=" + benchBefore + ";benchAfter=" + benchAfter + ";actionSeq=" + lease.LastSequence);
                if (woodAfter != woodBefore - 10 || benchAfter != benchBefore + 1)
                    throw new GameplayActionFailure("craft:normal_result_did_not_match_inventory_delta");
            }

            if (!input.UseItem) return;
            if (input.SelectedSlot < 0 || player.selectedItem != input.SelectedSlot) return;
            Item selected = player.inventory[input.SelectedSlot];
            if (selected == null || selected.IsAir) { player.controlUseItem = false; return; }
            GameplayObservation visible = VisibleEnvironment.Capture(player);
            bool axe = selected.axe > 0;
            bool bench = selected.type == ItemID.WorkBench && selected.createTile == TileID.WorkBenches;
            if (!axe && !bench) throw new GameplayActionFailure("tool:unsupported_item");
            VisibleTarget[] candidates = axe ? visible.TreeTargets : visible.PlacementTargets;
            if (!Contains(candidates, input.AimTileX, input.AimTileY))
            {
                // Targets can legitimately disappear during chopping/placement.
                // Release use and let the controller verify the actual result.
                player.controlUseItem = false;
                return;
            }
            int aimX = input.AimTileX, aimY = input.AimTileY;
            if (bench)
            {
                TileObjectData data = TileObjectData.GetTileData(TileID.WorkBenches, selected.placeStyle, 0);
                if (data == null || data.Width != 2 || data.Height != 1)
                    throw new GameplayActionFailure("place:unverified_workbench_shape");
                aimX += data.Origin.X;
                aimY += data.Origin.Y;
            }
            // Verified vanilla Player.Update computes tileTarget after CopyInto,
            // using this world-to-screen mouse projection. ItemCheck performs the
            // normal reach, support, tool, animation and material checks itself.
            Main.mouseX = (int)(aimX * 16 + 8 - Main.screenPosition.X);
            Main.mouseY = (int)(aimY * 16 + 8 - Main.screenPosition.Y);
            player.controlUseItem = true;
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
            for (int i = 0; i < Math.Min(50, player.inventory.Length); ++i)
            {
                Item item = player.inventory[i];
                if (item != null && item.type == type && item.stack > 0) total += item.stack;
            }
            return total;
        }

        private static bool TryWorkbenchRecipe(Player player, out Recipe result)
        {
            result = null;
            if (player == null || player.dead || player.chest >= 0 || player.itemAnimation != 0 ||
                Main.mouseItem == null || !Main.mouseItem.IsAir || Count(player, ItemID.Wood) < 10 ||
                player.inventory == null) return false;
            bool hasSpace = false;
            for (int i = 0; i < Math.Min(50, player.inventory.Length); ++i)
                if (player.inventory[i] == null || player.inventory[i].IsAir) { hasSpace = true; break; }
            if (!hasSpace) return false;
            player.AdjTiles();
            Recipe.UpdateRecipeList();
            if (Main.recipe == null || Main.availableRecipe == null) return false;
            for (int i = 0; i < Math.Min(Main.numAvailableRecipes, Main.availableRecipe.Length); ++i)
            {
                int index = Main.availableRecipe[i];
                if (index < 0 || index >= Main.recipe.Length) continue;
                Recipe recipe = Main.recipe[index];
                if (recipe == null || recipe.createItem == null || recipe.createItem.type != ItemID.WorkBench ||
                    recipe.createItem.stack != 1 || recipe.requiredTile >= 0 || recipe.requiredItem == null) continue;
                int ingredients = 0;
                bool exactlyWood = true;
                foreach (Item ingredient in recipe.requiredItem)
                {
                    if (ingredient == null || ingredient.IsAir) continue;
                    ++ingredients;
                    if (ingredient.type != ItemID.Wood || ingredient.stack != 10) exactlyWood = false;
                }
                if (ingredients != 1 || !exactlyWood ||
                    !recipe.PlayerMeetsEnvironmentConditions(player, null) ||
                    !Recipe.CollectedEnoughItemsToCraft(recipe) || !Main.CursorHasSpaceToCraftRecipe(recipe)) continue;
                result = recipe;
                return true;
            }
            return false;
        }
    }
}
