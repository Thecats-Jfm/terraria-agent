namespace TerrariaAgent.Protocol
{
    // Pure admission predicate. Geometry remains inside the filtered game-thread
    // capture; neither this predicate nor the controller reads Terraria tiles.
    public static class PlatformStepGuard
    {
        public static bool CanApply(InputState input, GameplayObservation current)
        {
            if (!CompatibleUp(input) || current == null) return false;
            return input.Left ? current.CanStepLeft && current.StepRequiresUpLeft :
                current.CanStepRight && current.StepRequiresUpRight;
        }

        // The engine may already have completed the ascent during this lease.
        // A fresh, still-proven route then releases only Up. It cannot renew the
        // action, change direction, authorize missing ground, or alter the gate.
        public static bool TryResolve(InputState requested, GameplayObservation current, out InputState applied)
        {
            applied = new InputState();
            if (!CompatibleUp(requested) || current == null ||
                !(requested.Left ? current.CanStepLeft : current.CanStepRight)) return false;
            applied = requested.Copy();
            applied.Up = requested.Left ? current.StepRequiresUpLeft : current.StepRequiresUpRight;
            return true;
        }

        private static bool CompatibleUp(InputState input)
        {
            return input != null && input.Up && (input.Left ^ input.Right) &&
                !input.Jump && !input.UseItem && !input.CraftWorkBench && input.CraftRecipe == null &&
                input.AimTileX == -1 && input.AimTileY == -1;
        }
    }
}
