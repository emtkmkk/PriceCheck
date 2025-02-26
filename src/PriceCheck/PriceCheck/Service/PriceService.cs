﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using CheapLoc;
using Dalamud.DrunkenToad;
using Dalamud.Game.Text;
using Dalamud.Interface.Colors;
using Lumina.Excel.GeneratedSheets;

namespace PriceCheck
{
    /// <summary>
    /// Pricing service.
    /// </summary>
    public class PriceService
    {
        private readonly PriceCheckPlugin plugin;
        private readonly List<PricedItem> pricedItems = new();
        private readonly object locker = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="PriceService"/> class.
        /// </summary>
        /// <param name="plugin">price check plugin.</param>
        public PriceService(PriceCheckPlugin plugin)
        {
            this.plugin = plugin;
            this.LastPriceCheck = DateUtil.CurrentTime();
        }

        /// <summary>
        /// Gets or sets last price check conducted in unix timestamp.
        /// </summary>
        public long LastPriceCheck { get; set; }

        /// <summary>
        /// Get priced items.
        /// </summary>
        /// <returns>list of priced items.</returns>
        public IEnumerable<PricedItem> GetItems()
        {
            lock (this.locker)
            {
                return this.pricedItems.ToList();
            }
        }

        /// <summary>
        /// Clear all items.
        /// </summary>
        public void ClearItems()
        {
            lock (this.locker)
            {
                this.pricedItems.Clear();
            }
        }

        /// <summary>
        /// Conduct price check.
        /// </summary>
        /// <param name="itemId">item id to lookup.</param>
        /// <param name="isHQ">indicator if item is hq.</param>
        public void ProcessItemAsync(uint itemId, bool isHQ)
        {
            try
            {
                if (!this.plugin.ShouldPriceCheck()) return;

                // reject if invalid itemId
                if (itemId == 0)
                {
                    this.plugin.ItemCancellationTokenSource = null;
                    return;
                }

                // cancel if in-flight request
                if (this.plugin.ItemCancellationTokenSource != null)
                {
                    if (!this.plugin.ItemCancellationTokenSource.IsCancellationRequested)
                        this.plugin.ItemCancellationTokenSource.Cancel();
                    this.plugin.ItemCancellationTokenSource.Dispose();
                }

                // create new cancel token
                this.plugin.ItemCancellationTokenSource =
                    new CancellationTokenSource(this.plugin.Configuration.RequestTimeout * 2);

                // run price check
                Task.Run(async () =>
                {
                    await Task.Delay(
                                  this.plugin.Configuration.HoverDelay * 1000,
                                  this.plugin.ItemCancellationTokenSource!.Token)
                              .ConfigureAwait(false);
                    this.plugin.PriceService.ProcessItem(itemId, isHQ);
                });
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to process item.");
                this.plugin.ItemCancellationTokenSource = null;
                this.plugin.HoveredItemManager.ItemId = 0;
            }
        }

        private void ProcessItem(uint itemId, bool isHQ)
        {
            // reject invalid item id
            if (itemId == 0) return;

            // create priced item
            Logger.LogDebug($"Pricing itemId={itemId} hq={isHQ}");
            var pricedItem = new PricedItem
            {
                ItemId = itemId,
                IsHQ = isHQ,
            };

            // run price check
            this.PriceCheck(pricedItem);

            // check for existing entry for this itemId
            for (var i = 0; i < this.pricedItems.Count; i++)
            {
                if (this.pricedItems[i].ItemId != pricedItem.ItemId) continue;
                this.pricedItems.RemoveAt(i);
                break;
            }

            // determine message and colors
            this.SetFieldsByResult(pricedItem);

            // add to overlay
            if (this.plugin.Configuration.ShowOverlay)
            {
                // remove items over max
                while (this.pricedItems.Count >= this.plugin.Configuration.MaxItemsInOverlay)
                {
                    this.pricedItems.RemoveAt(this.pricedItems.Count - 1);
                }

                // add item depending on result
                switch (pricedItem.Result)
                {
                    case ItemResult.None:
                        break;
                    case ItemResult.Success:
                        if (this.plugin.Configuration.ShowSuccessInOverlay) this.AddItemToOverlay(pricedItem);
                        break;
                    case ItemResult.FailedToProcess:
                        if (this.plugin.Configuration.ShowFailedToProcessInOverlay) this.AddItemToOverlay(pricedItem);
                        break;
                    case ItemResult.FailedToGetData:
                        if (this.plugin.Configuration.ShowFailedToGetDataInOverlay) this.AddItemToOverlay(pricedItem);
                        break;
                    case ItemResult.NoDataAvailable:
                        if (this.plugin.Configuration.ShowNoDataAvailableInOverlay) this.AddItemToOverlay(pricedItem);
                        break;
                    case ItemResult.NoRecentDataAvailable:
                        if (this.plugin.Configuration.ShowNoRecentDataAvailableInOverlay) this.AddItemToOverlay(pricedItem);
                        break;
                    case ItemResult.BelowVendor:
                        if (this.plugin.Configuration.ShowBelowVendorInOverlay) this.AddItemToOverlay(pricedItem);
                        break;
                    case ItemResult.BelowMinimum:
                        if (this.plugin.Configuration.ShowBelowMinimumInOverlay) this.AddItemToOverlay(pricedItem);
                        break;
                    case ItemResult.Unmarketable:
                        if (this.plugin.Configuration.ShowUnmarketableInOverlay) this.AddItemToOverlay(pricedItem);
                        break;
                    default:
                        Logger.LogError("Unrecognized item result.");
                        break;
                }
            }

            // send chat message
            if (this.plugin.Configuration.ShowInChat)
            {
                switch (pricedItem.Result)
                {
                    case ItemResult.None:
                        break;
                    case ItemResult.Success:
                        if (this.plugin.Configuration.ShowSuccessInChat) this.plugin.PrintItemMessage(pricedItem);
                        break;
                    case ItemResult.FailedToProcess:
                        if (this.plugin.Configuration.ShowFailedToProcessInChat) this.plugin.PrintItemMessage(pricedItem);
                        break;
                    case ItemResult.FailedToGetData:
                        if (this.plugin.Configuration.ShowFailedToGetDataInChat) this.plugin.PrintItemMessage(pricedItem);
                        break;
                    case ItemResult.NoDataAvailable:
                        if (this.plugin.Configuration.ShowNoDataAvailableInChat) this.plugin.PrintItemMessage(pricedItem);
                        break;
                    case ItemResult.NoRecentDataAvailable:
                        if (this.plugin.Configuration.ShowNoRecentDataAvailableInChat) this.plugin.PrintItemMessage(pricedItem);
                        break;
                    case ItemResult.BelowVendor:
                        if (this.plugin.Configuration.ShowBelowVendorInChat) this.plugin.PrintItemMessage(pricedItem);
                        break;
                    case ItemResult.BelowMinimum:
                        if (this.plugin.Configuration.ShowBelowMinimumInChat) this.plugin.PrintItemMessage(pricedItem);
                        break;
                    case ItemResult.Unmarketable:
                        if (this.plugin.Configuration.ShowUnmarketableInChat) this.plugin.PrintItemMessage(pricedItem);
                        break;
                    default:
                        Logger.LogError("Unrecognized item result.");
                        break;
                }
            }

            // send toast
            if (this.plugin.Configuration.ShowToast)
            {
                PriceCheckPlugin.SendToast(pricedItem);
            }
        }

        private void AddItemToOverlay(PricedItem pricedItem)
        {
            this.plugin.WindowManager.MainWindow!.IsOpen = true;
            this.pricedItems.Insert(0, pricedItem);
        }

        private void SetFieldsByResult(PricedItem pricedItem)
        {
            switch (pricedItem.Result)
            {
                case ItemResult.Success:
                    pricedItem.Message = this.plugin.Configuration.ShowPrices
                                              ? pricedItem.MarketPrice.ToString("N0", CultureInfo.InvariantCulture)
                                              : Loc.Localize("SellOnMarketboard", "Sell on marketboard");
                    pricedItem.OverlayColor = ImGuiColors.HealerGreen;
                    pricedItem.ChatColor = 45;
                    break;
                case ItemResult.None:
                    pricedItem.Message = Loc.Localize("FailedToGetData", "Failed to get data");
                    pricedItem.OverlayColor = ImGuiColors.DPSRed;
                    pricedItem.ChatColor = 17;
                    break;
                case ItemResult.FailedToProcess:
                    pricedItem.Message = Loc.Localize("FailedToProcess", "Failed to process item");
                    pricedItem.OverlayColor = ImGuiColors.DPSRed;
                    pricedItem.ChatColor = 17;
                    break;
                case ItemResult.FailedToGetData:
                    pricedItem.Message = Loc.Localize("FailedToGetData", "Failed to get data");
                    pricedItem.OverlayColor = ImGuiColors.DPSRed;
                    pricedItem.ChatColor = 17;
                    break;
                case ItemResult.NoDataAvailable:
                    pricedItem.Message = Loc.Localize("NoDataAvailable", "No data available");
                    pricedItem.OverlayColor = ImGuiColors.DPSRed;
                    pricedItem.ChatColor = 17;
                    break;
                case ItemResult.NoRecentDataAvailable:
                    pricedItem.Message = Loc.Localize("NoRecentDataAvailable", "No recent data");
                    pricedItem.OverlayColor = ImGuiColors.DPSRed;
                    pricedItem.ChatColor = 17;
                    break;
                case ItemResult.BelowVendor:
                    pricedItem.Message = Loc.Localize("BelowVendor", "Sell to vendor");
                    pricedItem.OverlayColor = ImGuiColors.DalamudYellow;
                    pricedItem.ChatColor = 25;
                    break;
                case ItemResult.BelowMinimum:
                    pricedItem.Message = Loc.Localize("BelowMinimum", "Below minimum price");
                    pricedItem.OverlayColor = ImGuiColors.DalamudYellow;
                    pricedItem.ChatColor = 25;
                    break;
                case ItemResult.Unmarketable:
                    pricedItem.Message = Loc.Localize("Unmarketable", "Can't sell on marketboard");
                    pricedItem.OverlayColor = ImGuiColors.DalamudYellow;
                    pricedItem.ChatColor = 25;
                    break;
                default:
                    pricedItem.Message = Loc.Localize("FailedToProcess", "Failed to process item");
                    pricedItem.OverlayColor = ImGuiColors.DPSRed;
                    pricedItem.ChatColor = 17;
                    break;
            }

            Logger.LogDebug($"Message={pricedItem.Message}");
        }

        private void PriceCheck(PricedItem pricedItem)
        {
            // record current time for window visibility
            this.LastPriceCheck = DateUtil.CurrentTime();
            Logger.LogDebug($"LastPriceCheck={this.LastPriceCheck}");

            // look up item game data
            var item = PriceCheckPlugin.DataManager.GameData.Excel.GetSheet<Item>()?.GetRow(pricedItem.ItemId);

            if (item == null)
            {
                pricedItem.Result = ItemResult.FailedToProcess;
                Logger.LogError($"Failed to retrieve game data for itemId {pricedItem.ItemId}.");
                return;
            }

            // set fields from game data
            pricedItem.ItemName = pricedItem.IsHQ ? item.Name + " " + (char)SeIconChar.HighQuality : item.Name;
            Logger.LogDebug($"ItemName={pricedItem.ItemName}");
            pricedItem.IsMarketable = item.ItemSearchCategory.Row != 0;
            Logger.LogDebug($"IsMarketable={pricedItem.IsMarketable}");
            pricedItem.VendorPrice = item.PriceLow;
            Logger.LogDebug($"VendorPrice={pricedItem.VendorPrice}");

            // check if marketable
            if (!pricedItem.IsMarketable)
            {
                pricedItem.Result = ItemResult.Unmarketable;
                return;
            }

            // set worldId
            var worldId = PriceCheckPlugin.ClientState.LocalPlayer?.HomeWorld.Id ?? 0;
            Logger.LogDebug($"worldId={worldId}");
            if (worldId == 0)
            {
                pricedItem.Result = ItemResult.FailedToProcess;
                return;
            }

            // lookup market data
            MarketBoardData? marketBoardData;
            try
            {
                marketBoardData = this.plugin.UniversalisClient.GetMarketBoard(worldId, pricedItem.ItemId);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Caught exception trying to get marketboard data.");
                marketBoardData = null;
            }

            // validate marketboard response
            if (marketBoardData == null)
            {
                pricedItem.Result = ItemResult.FailedToGetData;
                Logger.LogError("Failed to get marketboard data.");
                return;
            }

            // validate marketboard data
            if (marketBoardData.AveragePriceNQ == null || marketBoardData.LastCheckTime == 0)
            {
                pricedItem.Result = ItemResult.NoDataAvailable;
                return;
            }

            // set market price
            double? marketPrice = null;
            if (this.plugin.Configuration.PriceMode == PriceMode.AveragePrice.Index)
                marketPrice = pricedItem.IsHQ ? marketBoardData.AveragePriceHQ : marketBoardData.AveragePriceNQ;
            else if (this.plugin.Configuration.PriceMode == PriceMode.CurrentAveragePrice.Index)
                marketPrice = pricedItem.IsHQ ? marketBoardData.CurrentAveragePriceHQ : marketBoardData.CurrentAveragePriceNQ;
            else if (this.plugin.Configuration.PriceMode == PriceMode.MinimumPrice.Index)
                marketPrice = pricedItem.IsHQ ? marketBoardData.MinimumPriceHQ : marketBoardData.MinimumPriceNQ;
            else if (this.plugin.Configuration.PriceMode == PriceMode.MaximumPrice.Index)
                marketPrice = pricedItem.IsHQ ? marketBoardData.MaximumPriceHQ : marketBoardData.MaximumPriceNQ;
            else if (this.plugin.Configuration.PriceMode == PriceMode.CurrentMinimumPrice.Index)
                marketPrice = marketBoardData.CurrentMinimumPrice;
            if (marketPrice is null or 0)
            {
                pricedItem.Result = ItemResult.NoDataAvailable;
                return;
            }

            marketPrice = Math.Round((double)marketPrice);
            pricedItem.MarketPrice = (uint)marketPrice;
            Logger.LogDebug($"marketPrice={pricedItem.MarketPrice}");

            // compare with date threshold
            var diffInSeconds = DateUtil.CurrentTime() - pricedItem.LastUpdated;
            var diffInDays = diffInSeconds / 86400000;
            Logger.LogDebug($"Max Days Check: diffDays={diffInDays} >= maxUpload={this.plugin.Configuration.MaxUploadDays}");
            if (diffInDays >= this.plugin.Configuration.MaxUploadDays)
            {
                pricedItem.Result = ItemResult.NoRecentDataAvailable;
                return;
            }

            // compare with vendor price
            Logger.LogDebug($"Vendor Check: vendorPrice={pricedItem.VendorPrice} >= marketPrice={pricedItem.MarketPrice}");
            if (pricedItem.VendorPrice >= pricedItem.MarketPrice)
            {
                pricedItem.Result = ItemResult.BelowVendor;
                return;
            }

            // compare with price threshold
            Logger.LogDebug($"Min Check: marketPrice={pricedItem.MarketPrice} < minPrice={this.plugin.Configuration.MinPrice}");
            if (pricedItem.MarketPrice < this.plugin.Configuration.MinPrice)
            {
                pricedItem.Result = ItemResult.BelowMinimum;
                return;
            }

            // made it - set as success
            pricedItem.Result = ItemResult.Success;
        }
    }
}
