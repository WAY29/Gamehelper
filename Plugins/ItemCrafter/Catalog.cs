namespace ItemCrafter
{
    using System;
    using GameHelper.RemoteEnums;

    internal enum StepKind
    {
        Alchemy,
        Regal,
        Exalt,
        Chaos,
        Vaal,
        Omen,
    }

    internal readonly record struct CurrencyInfo(string InternalName, string English, StepKind Kind);

    internal static class Catalog
    {
        public const string Alchemy = "CurrencyUpgradeToRare";
        public const string Exalted = "CurrencyAddModToRare";

        public static readonly CurrencyInfo[] All =
        {
            new("CurrencyUpgradeToRare", "Orb of Alchemy", StepKind.Alchemy),
            new("CurrencyUpgradeMagicToRare", "Regal Orb", StepKind.Regal),
            new("CurrencyUpgradeMagicToRare2", "Greater Regal Orb", StepKind.Regal),
            new("CurrencyUpgradeMagicToRare3", "Perfect Regal Orb", StepKind.Regal),
            new("CurrencyAddModToRare", "Exalted Orb", StepKind.Exalt),
            new("CurrencyAddModToRare2", "Greater Exalted Orb", StepKind.Exalt),
            new("CurrencyAddModToRare3", "Perfect Exalted Orb", StepKind.Exalt),
            new("CurrencyRerollRare", "Chaos Orb", StepKind.Chaos),
            new("CurrencyRerollRare2", "Greater Chaos Orb", StepKind.Chaos),
            new("CurrencyRerollRare3", "Perfect Chaos Orb", StepKind.Chaos),
            new("CurrencyCorrupt", "Vaal Orb", StepKind.Vaal),
            new("OmenOnChaosMapItemRarity", "Omen of Chaotic Rarity", StepKind.Omen),
            new("OmenOnChaosMapPackSize", "Omen of Chaotic Quantity", StepKind.Omen),
            new("OmenOnChaosMapMonsterEffectiveness", "Omen of Chaotic Effectiveness", StepKind.Omen),
            new("OmenOnChaosMapMonsterRarity", "Omen of Chaotic Monsters", StepKind.Omen),
        };

        public static bool TryGet(string? internalName, out CurrencyInfo info)
        {
            if (!string.IsNullOrEmpty(internalName))
            {
                foreach (var row in All)
                {
                    if (row.InternalName.Equals(internalName, StringComparison.OrdinalIgnoreCase))
                    {
                        info = row;
                        return true;
                    }
                }
            }

            info = default;
            return false;
        }

        public static bool IsWaystone(string path, string internalName)
        {
            if (path.Contains("Waystone", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("MapKey", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return internalName.Contains("Waystone", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsEligible(StepKind kind, Rarity rarity, int explicitCount, bool corrupted, int untilAffixes)
        {
            if (corrupted)
            {
                return false;
            }

            return kind switch
            {
                StepKind.Alchemy => rarity is Rarity.Normal or Rarity.Magic,
                StepKind.Regal => rarity == Rarity.Magic,
                StepKind.Exalt => rarity == Rarity.Rare && explicitCount < ClampUntil(untilAffixes),
                StepKind.Chaos => rarity == Rarity.Rare,
                StepKind.Vaal => rarity is Rarity.Magic or Rarity.Rare,
                StepKind.Omen => true,
                _ => false,
            };
        }

        public static int ExaltClicks(int explicitCount, int untilAffixes)
        {
            var n = ClampUntil(untilAffixes);
            var clicks = n - explicitCount;
            return clicks > 0 ? clicks : 0;
        }

        public static int ClampUntil(int untilAffixes) => Math.Clamp(untilAffixes, 3, 6);

        public static void SelfCheck()
        {
            if (!IsWaystone("Metadata/Items/Maps/Waystone11", "Waystone11") ||
                !TryGet("OmenOnChaosMapPackSize", out _) ||
                TryGet("CurrencyUpgradeToRare2", out _))
            {
                throw new InvalidOperationException("catalog");
            }

            if (IsEligible(StepKind.Alchemy, Rarity.Rare, 4, false, 6) ||
                !IsEligible(StepKind.Alchemy, Rarity.Normal, 0, false, 6) ||
                IsEligible(StepKind.Exalt, Rarity.Rare, 6, false, 6) ||
                !IsEligible(StepKind.Exalt, Rarity.Rare, 4, false, 6) ||
                IsEligible(StepKind.Vaal, Rarity.Rare, 4, true, 6) ||
                ExaltClicks(4, 6) != 2 ||
                ExaltClicks(6, 6) != 0)
            {
                throw new InvalidOperationException("craft rules");
            }
        }
    }
}
