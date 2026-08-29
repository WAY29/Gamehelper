namespace StashUtilityPlus
{
    using System.Collections.Generic;
    using System.Numerics;
    using GameHelper.Plugin;

    public sealed class RuleExpr
    {
        public string Mod = string.Empty;
        public bool Not;
        public bool All = true;
        public List<RuleExpr> Items = new();
    }

    public sealed class HighlightRule
    {
        public string Name = "新规则";
        public bool Enabled = true;
        public RuleExpr When = new();
        /// <summary>Empty = all tablet types.</summary>
        public List<string> TabletTypes = new();
        /// <summary>0 border, 1 TL, 2 TR, 3 BL, 4 BR.</summary>
        public int Action;
        public Vector4 Color = new(0f, 0.85f, 1f, 1f);
        public float Thickness = 3f;
        public float ArrowSize = 20f;
    }

    public sealed class StashUtilityPlusSettings : IPSettings
    {
        /// <summary>0 overlay, 1 English, 2 zh-CN, 3 zh-Hant.</summary>
        public int AffixLanguage;
        public List<HighlightRule> Rules = new();
    }
}
