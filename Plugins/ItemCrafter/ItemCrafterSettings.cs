namespace ItemCrafter
{
    using System.Collections.Generic;
    using ClickableTransparentOverlay.Win32;
    using GameHelper.Plugin;

    public sealed class CraftCond
    {
        public bool And = true;
        public string Mod = string.Empty;
    }

    public sealed class CraftExpr
    {
        public string Mod = string.Empty;
        public bool All = true;
        public List<CraftExpr> Items = new();
    }

    public sealed class CraftIf
    {
        public CraftExpr When = new();
        public List<CraftCond> Conds = new();
        public List<CraftStep> Then = new();
        public List<CraftStep> Else = new();
    }

    public sealed class CraftStep
    {
        public string InternalName = Catalog.Alchemy;
        public int UntilAffixes = 6;
        public CraftIf? If;
    }

    public sealed class CraftRecipe
    {
        public string Name = "新配方";
        public string Target = Catalog.DefaultTarget;
        public List<string> TargetIds = new();
        public List<CraftStep> Steps = new();
    }

    public sealed class ItemCrafterSettings : IPSettings
    {
        public VK ToggleKey = VK.F6;
        public int ClickDelayMs = 200;
        public int MouseAbortPx = 20;
        public bool ShowDebugWindow;
        public bool ShowLogWindow;
        public int AffixLanguage;
        public List<CraftRecipe> Recipes = new();
        public int SelectedRecipe;
    }
}
