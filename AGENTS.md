# Agent notes

## ItemCrafter 加通货

1. 在 [poe2db.tw](https://poe2db.tw) 用 **同一 slug** 打开 us / cn / tw 三页（例如 `Scroll_of_Wisdom`）。
2. 记下：
   - `Type` / 内部名：`Metadata/Items/Currency/<InternalName>` 的最后一段（如 `CurrencyIdentification`）
   - 英文名、简中名、繁中名（`BaseType`）
3. `Plugins/ItemCrafter/Catalog.cs` 的 `Catalog.All` 用 **InternalName**，必须和背包物品的 `Base.InternalName`（或 Path 最后一段）一致。
4. `Plugins/ItemCrafter/Localization/{en-US,zh-CN,zh-Hant}.json` 加 `item.<InternalName>`。
5. 点击规则不是现有 `StepKind` 时，补 `Kind` + `IsEligible` / `Clicks`，并在 `Catalog.SelfCheck` 加一条会失败的断言。

不要靠游戏内显示名匹配通货；显示名只给 UI。
