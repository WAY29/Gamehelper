namespace ItemCrafter
{
    using System.Collections.Generic;
    using ClickableTransparentOverlay.Win32;
    using GameHelper.Plugin;

    public sealed class CraftStep
    {
        public string InternalName = Catalog.Alchemy;
        public int UntilAffixes = 6;
    }

    public sealed class CraftRecipe
    {
        public string Name = "新配方";
        public List<CraftStep> Steps = new();
    }

    public sealed class ItemCrafterSettings : IPSettings
    {
        public VK ToggleKey = VK.F6;
        public int ClickDelayMs = 200;
        public int MouseAbortPx = 20;
        public bool ShowDebugWindow;
        public bool ShowLogWindow;
        public List<CraftRecipe> Recipes = new();
        public int SelectedRecipe;
    }
}
