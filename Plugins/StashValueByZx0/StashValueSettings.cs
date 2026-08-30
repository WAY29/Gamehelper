// <copyright file="StashValueSettings.cs" company="zx0CF1">
// Copyright (c) zx0CF1. All rights reserved.
// </copyright>

namespace StashValue
{
    using System.Numerics;
    using GameHelper.Plugin;

    /// <summary>
    /// <see cref="StashValue"/> plugin settings.
    /// </summary>
    public sealed class StashValueSettings : IPSettings
    {
        /// <summary>Draw value labels over stash items.</summary>
        public bool ShowOverlay = true;

        /// <summary>Draw value labels over inventory items.</summary>
        public bool ShowInventoryOverlay = false;

        /// <summary>Draw debug information and boxes.</summary>
        public bool ShowDebugInfo = false;

        /// <summary>Minimum value (in Exalted) for an item to get a label.</summary>
        public float MinValueEx = 0.0f;

        /// <summary>Hide pricing overlays when hovering over the item slot.</summary>
        public bool HidePriceOnHover = true;

        /// <summary>Display currency: 0 = Divine, 1 = Exalted, 2 = Chaos.</summary>
        public int DisplayCurrency = 1;

        /// <summary>Font size scale for price labels.</summary>
        public float PriceFontScale = 1.0f;

        /// <summary>Horizontal pixel offset of the label inside the item slot.</summary>
        public float PriceOffsetX = 5f;

        /// <summary>Vertical pixel offset of the label inside the item slot.</summary>
        public float PriceOffsetY = -5f;

        /// <summary>Label text color (RGBA 0-1).</summary>
        public Vector4 TextColor = new Vector4(1f, 235f / 255f, 140f / 255f, 1f);
    }
}
