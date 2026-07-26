using AngleSharp.Dom;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Helpers.Json;
using ArchiSteamFarm.Localization;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam.Integration;
using ArchiSteamFarm.Steam.Interaction;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Web.Responses;
using ArchiSteamFarm.Web;
using PluginLocale = ItemBuyer.Localization;
using SteamKit2;
using System.Collections.Generic;
using System.ComponentModel;
using System.Composition;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace ItemBuyer;

[Export(typeof(IPlugin))]
internal sealed class ItemBuyerPlugin : IASF, IGitHubPluginUpdates, IBotCommand2 {
	public string Name => nameof(ItemBuyerPlugin);
	public string RepositoryName => "dm1tz/ItemBuyer";
	public Version Version => typeof(ItemBuyerPlugin).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	internal static ItemBuyerConfig? Config { get; private set; }

	private static readonly SemaphoreSlim BuyItemSemaphore = new(1, 1);

	public Task OnASFInit(IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
		if (additionalConfigProperties == null) {
			return Task.CompletedTask;
		}

		foreach ((string configProperty, JsonElement configValue) in additionalConfigProperties) {
			if (configProperty != nameof(ItemBuyerPlugin)) {
				continue;
			}

			try {
				Config = configValue.ToJsonObject<ItemBuyerConfig>();
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
			}

			break;
		}

		return Task.CompletedTask;
	}

	public async Task<string?> OnBotCommand(Bot bot, EAccess access, string message, string[] args, ulong steamID = 0) {
		ArgumentNullException.ThrowIfNull(bot);

		if (!Enum.IsDefined(access)) {
			throw new InvalidEnumArgumentException(nameof(access), (int) access, typeof(EAccess));
		}

		ArgumentException.ThrowIfNullOrEmpty(message);

		if ((args == null) || (args.Length == 0)) {
			throw new ArgumentNullException(nameof(args));
		}

		if ((steamID != 0) && !new SteamID(steamID).IsIndividualAccount) {
			throw new ArgumentOutOfRangeException(nameof(steamID));
		}

		return args[0].ToUpperInvariant() switch {
			"BUYITEM" or "BI" when args.Length == 5 =>
				await ResponseBuyItem(access, args[1], args[2], args[3], args[4], steamID).ConfigureAwait(false),
			"BUYITEM" or "BI" when args.Length == 4 =>
				await ResponseBuyItem(bot, access, args[1], args[2], args[3]).ConfigureAwait(false),
			"CHECKPRICE" or "CP" when args.Length == 5 =>
				await ResponseCheckPrice(access, args[1], args[2], args[3], args[4], steamID).ConfigureAwait(false),
			"CHECKPRICE" or "CP" when args.Length == 4 =>
				await ResponseCheckPrice(bot, access, args[1], args[2], args[3]).ConfigureAwait(false),
			"BIVERSION" or "BIV" => ResponseVersion(access),
			_ => null
		};
	}

	private sealed record PurchaseResult(bool Success, string ItemName, string PriceText, string Error) {
		internal static PurchaseResult Failure(string error) => new(false, string.Empty, string.Empty, error);
	}

	private static async Task<string?> ResponseBuyItem(Bot bot, EAccess access, string targetAppID, string targetItemDefID, string targetQuantity) {
		if (!Enum.IsDefined(access)) {
			throw new InvalidEnumArgumentException(nameof(access), (int) access, typeof(EAccess));
		}

		if (access < EAccess.Master) {
			return access > EAccess.None ? bot.Commands.FormatBotResponse(Strings.ErrorAccessDenied) : null;
		}

		if (!bot.IsConnectedAndLoggedOn) {
			return bot.Commands.FormatBotResponse(Strings.BotNotConnected);
		}

		if (!uint.TryParse(targetAppID, out uint appID) || (appID == 0)) {
			return bot.Commands.FormatBotResponse(string.Format(CultureInfo.CurrentCulture, Strings.ErrorIsInvalid, nameof(appID)));
		}

		if (!ulong.TryParse(targetItemDefID, out ulong itemDefID) || (itemDefID == 0)) {
			return bot.Commands.FormatBotResponse(string.Format(CultureInfo.CurrentCulture, Strings.ErrorIsInvalid, nameof(itemDefID)));
		}

		if (!uint.TryParse(targetQuantity, out uint quantity) || (quantity == 0)) {
			return bot.Commands.FormatBotResponse(string.Format(CultureInfo.CurrentCulture, Strings.ErrorIsInvalid, nameof(quantity)));
		}

		PurchaseResult purchaseResult;

		await BuyItemSemaphore.WaitAsync().ConfigureAwait(false);

		try {
			purchaseResult = await BuyItem(bot, appID, itemDefID, quantity, checkPrice: false).ConfigureAwait(false);
		} catch (Exception e) {
			bot.ArchiLogger.LogGenericWarningException(e);

			purchaseResult = PurchaseResult.Failure(Strings.WarningFailed);
		} finally {
			await Task.Delay(TimeSpan.FromSeconds(Config?.BuyItemLimiterDelay ?? ItemBuyerConfig.DefaultBuyItemLimiterDelay)).ConfigureAwait(false);

			_ = BuyItemSemaphore.Release();
		}

		string response = purchaseResult.Success ? PluginLocale.Strings.FormatBotPurchaseSuccess(quantity, purchaseResult.ItemName, purchaseResult.PriceText) : purchaseResult.Error;

		return bot.Commands.FormatBotResponse(response);
	}

	private static async Task<string?> ResponseBuyItem(EAccess access, string botNames, string targetAppID, string targetItemDefID, string targetQuantity, ulong steamID = 0) {
		if (!Enum.IsDefined(access)) {
			throw new InvalidEnumArgumentException(nameof(access), (int) access, typeof(EAccess));
		}

		ArgumentException.ThrowIfNullOrEmpty(botNames);
		ArgumentException.ThrowIfNullOrEmpty(targetAppID);
		ArgumentException.ThrowIfNullOrEmpty(targetItemDefID);
		ArgumentException.ThrowIfNullOrEmpty(targetQuantity);

		if ((steamID != 0) && !new SteamID(steamID).IsIndividualAccount) {
			throw new ArgumentOutOfRangeException(nameof(steamID));
		}

		HashSet<Bot>? bots = Bot.GetBots(botNames);

		if ((bots == null) || (bots.Count == 0)) {
			return access >= EAccess.Owner ? Commands.FormatStaticResponse(string.Format(CultureInfo.CurrentCulture, Strings.BotNotFound, botNames)) : null;
		}

		IList<string?> results = await Utilities.InParallel(bots.Select(bot => ResponseBuyItem(bot, Commands.GetProxyAccess(bot, access, steamID), targetAppID, targetItemDefID, targetQuantity))).ConfigureAwait(false);

		List<string> responses = [.. results.Where(static result => !string.IsNullOrEmpty(result)).Select(static result => result!)];

		return responses.Count > 0 ? string.Join(Environment.NewLine, responses) : null;
	}

	private static async Task<string?> ResponseCheckPrice(Bot bot, EAccess access, string targetAppID, string targetItemDefID, string targetQuantity) {
		if (!Enum.IsDefined(access)) {
			throw new InvalidEnumArgumentException(nameof(access), (int) access, typeof(EAccess));
		}

		if (access < EAccess.FamilySharing) {
			return access > EAccess.None ? bot.Commands.FormatBotResponse(Strings.ErrorAccessDenied) : null;
		}

		if (!bot.IsConnectedAndLoggedOn) {
			return bot.Commands.FormatBotResponse(Strings.BotNotConnected);
		}

		if (!uint.TryParse(targetAppID, out uint appID) || (appID == 0)) {
			return bot.Commands.FormatBotResponse(string.Format(CultureInfo.CurrentCulture, Strings.ErrorIsInvalid, nameof(appID)));
		}

		if (!ulong.TryParse(targetItemDefID, out ulong itemDefID) || (itemDefID == 0)) {
			return bot.Commands.FormatBotResponse(string.Format(CultureInfo.CurrentCulture, Strings.ErrorIsInvalid, nameof(itemDefID)));
		}

		if (!uint.TryParse(targetQuantity, out uint quantity) || (quantity == 0)) {
			return bot.Commands.FormatBotResponse(string.Format(CultureInfo.CurrentCulture, Strings.ErrorIsInvalid, nameof(quantity)));
		}

		PurchaseResult purchaseResult;

		await BuyItemSemaphore.WaitAsync().ConfigureAwait(false);

		try {
			purchaseResult = await BuyItem(bot, appID, itemDefID, quantity, checkPrice: true).ConfigureAwait(false);
		} catch (Exception e) {
			bot.ArchiLogger.LogGenericWarningException(e);

			purchaseResult = PurchaseResult.Failure(Strings.WarningFailed);
		} finally {
			await Task.Delay(TimeSpan.FromSeconds(Config?.BuyItemLimiterDelay ?? ItemBuyerConfig.DefaultBuyItemLimiterDelay)).ConfigureAwait(false);

			_ = BuyItemSemaphore.Release();
		}

		string response = purchaseResult.Success ? PluginLocale.Strings.FormatBotPrice(quantity, purchaseResult.ItemName, purchaseResult.PriceText) : purchaseResult.Error;

		return bot.Commands.FormatBotResponse(response);
	}

	private static async Task<string?> ResponseCheckPrice(EAccess access, string botNames, string targetAppID, string targetItemDefID, string targetQuantity, ulong steamID = 0) {
		if (!Enum.IsDefined(access)) {
			throw new InvalidEnumArgumentException(nameof(access), (int) access, typeof(EAccess));
		}

		ArgumentException.ThrowIfNullOrEmpty(botNames);
		ArgumentException.ThrowIfNullOrEmpty(targetAppID);
		ArgumentException.ThrowIfNullOrEmpty(targetItemDefID);
		ArgumentException.ThrowIfNullOrEmpty(targetQuantity);

		if ((steamID != 0) && !new SteamID(steamID).IsIndividualAccount) {
			throw new ArgumentOutOfRangeException(nameof(steamID));
		}

		HashSet<Bot>? bots = Bot.GetBots(botNames);

		if ((bots == null) || (bots.Count == 0)) {
			return access >= EAccess.Owner ? Commands.FormatStaticResponse(string.Format(CultureInfo.CurrentCulture, Strings.BotNotFound, botNames)) : null;
		}

		IList<string?> results = await Utilities.InParallel(bots.Select(bot => ResponseCheckPrice(bot, Commands.GetProxyAccess(bot, access, steamID), targetAppID, targetItemDefID, targetQuantity))).ConfigureAwait(false);

		List<string> responses = [.. results.Where(static result => !string.IsNullOrEmpty(result)).Select(static result => result!)];

		return responses.Count > 0 ? string.Join(Environment.NewLine, responses) : null;
	}

	private static async Task<PurchaseResult> BuyItem(Bot bot, uint appID, ulong itemID, uint quantity, bool checkPrice) {
		Uri buyItemRequest = new(ArchiWebHandler.SteamStoreURL, $"/buyitem/{appID}/{itemID}/{quantity}");

		HtmlDocumentResponse? buyItemResponse = await bot.ArchiWebHandler.WebBrowser.UrlGetToHtmlDocument(buyItemRequest).ConfigureAwait(false);

		if (buyItemResponse?.Content == null) {
			return PurchaseResult.Failure(string.Format(CultureInfo.CurrentCulture, Strings.ErrorObjectIsNull, nameof(buyItemResponse)));
		}

		if (buyItemResponse.StatusCode != HttpStatusCode.OK) {
			return PurchaseResult.Failure(PluginLocale.Strings.FormatBotUnexpectedHttpStatus(nameof(buyItemResponse), (int) buyItemResponse.StatusCode, buyItemResponse.StatusCode));
		}

		string? pageError = buyItemResponse.Content.QuerySelector("#error_box .error")?.TextContent.Trim();

		if (!string.IsNullOrEmpty(pageError)) {
			return PurchaseResult.Failure(pageError);
		}

		PurchaseResult result = new(true, PluginLocale.Strings.FormatItemNameFallback(itemID), PluginLocale.Strings.PriceUnknown, string.Empty);

		string? itemName = buyItemResponse.Content.QuerySelector(".approvetxn_lineitem_description")?.TextContent.Trim();

		if (!string.IsNullOrEmpty(itemName)) {
			result = result with { ItemName = itemName };
		}

		string? priceText = buyItemResponse.Content.QuerySelector("#review_total_value, #review_subtotal_value")?.TextContent.Trim();

		if (!string.IsNullOrEmpty(priceText)) {
			result = result with { PriceText = priceText };
		}

		if (checkPrice) {
			return result;
		}

		// When the wallet balance is insufficient, Steam renders an "Add Funds" button linking to the wallet top-up page.
		if (buyItemResponse.Content.QuerySelector("#responsive_page_template_content a[href*='addfunds'], #form_authtxn a[href*='addfunds']") != null) {
			return PurchaseResult.Failure(PluginLocale.Strings.BotInsufficientBalance);
		}

		IElement? approvalForm = buyItemResponse.Content.QuerySelector("#form_authtxn");

		if (approvalForm == null) {
			return PurchaseResult.Failure(string.Format(CultureInfo.CurrentCulture, Strings.ErrorObjectIsNull, nameof(approvalForm)));
		}

		result = result with { Success = false };

		Dictionary<string, string> form = [];

		foreach (IElement input in approvalForm.QuerySelectorAll("input")) {
			string? name = input.GetAttribute("name");

			if (!string.IsNullOrEmpty(name)) {
				form[name] = input.GetAttribute("value") ?? string.Empty;
			}
		}

		if (!form.TryGetValue("transaction_id", out string? targetTransactionID) || !ulong.TryParse(targetTransactionID, out ulong transactionID) || (transactionID == 0)) {
			return PurchaseResult.Failure(string.Format(CultureInfo.CurrentCulture, Strings.ErrorIsInvalid, nameof(transactionID)));
		}

		if (!form.TryGetValue("returnurl", out string? returnURL) || string.IsNullOrEmpty(returnURL)) {
			return PurchaseResult.Failure(string.Format(CultureInfo.CurrentCulture, Strings.ErrorIsEmpty, nameof(returnURL)));
		}

		form["approved"] = "1";

		string encodedReturnURL = Uri.EscapeDataString(returnURL);
		string encodedCanceledURL = Uri.EscapeDataString(ArchiWebHandler.SteamStoreURL.AbsoluteUri);

		string? formAction = approvalForm.GetAttribute("action");

		Uri approveRequest = !string.IsNullOrEmpty(formAction) ? new Uri(ArchiWebHandler.SteamCheckoutURL, formAction) : new Uri(ArchiWebHandler.SteamCheckoutURL, "/checkout/approvetxnsubmit");

		Uri approveReferer = new(ArchiWebHandler.SteamCheckoutURL, $"/checkout/approvetxn/{transactionID}/?returnurl={encodedReturnURL}&canceledurl={encodedCanceledURL}");

		HtmlDocumentResponse? approveResponse = await bot.ArchiWebHandler.WebBrowser.UrlPostToHtmlDocument(approveRequest, data: form, referer: approveReferer, requestOptions: WebBrowser.ERequestOptions.ReturnRedirections).ConfigureAwait(false);

		if (approveResponse == null) {
			return PurchaseResult.Failure(string.Format(CultureInfo.CurrentCulture, Strings.ErrorObjectIsNull, nameof(approveResponse)));
		}

		if (approveResponse.StatusCode != HttpStatusCode.Found) {
			return PurchaseResult.Failure(PluginLocale.Strings.FormatBotUnexpectedHttpStatus(nameof(approveResponse), (int) approveResponse.StatusCode, approveResponse.StatusCode));
		}

		Uri finalizeRequest = approveResponse.FinalUri;

		HtmlDocumentResponse? finalizeResponse = await bot.ArchiWebHandler.WebBrowser.UrlGetToHtmlDocument(finalizeRequest, referer: approveRequest, requestOptions: WebBrowser.ERequestOptions.ReturnRedirections).ConfigureAwait(false);

		if (finalizeResponse == null) {
			return PurchaseResult.Failure(string.Format(CultureInfo.CurrentCulture, Strings.ErrorObjectIsNull, nameof(finalizeResponse)));
		}

		if (finalizeResponse.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.Found) {
			return PurchaseResult.Failure(PluginLocale.Strings.FormatBotUnexpectedHttpStatus(nameof(finalizeResponse), (int) finalizeResponse.StatusCode, finalizeResponse.StatusCode));
		}

		string? finalizeError = finalizeResponse.Content?.QuerySelector("#error_box .error, #error_box")?.TextContent.Trim();

		if (!string.IsNullOrEmpty(finalizeError)) {
			return PurchaseResult.Failure(finalizeError);
		}

		return result with { Success = true };
	}

	private static string? ResponseVersion(EAccess access) {
		if (!Enum.IsDefined(access)) {
			throw new InvalidEnumArgumentException(nameof(access), (int) access, typeof(EAccess));
		}

		if (access < EAccess.FamilySharing) {
			return access > EAccess.None ? Commands.FormatStaticResponse(Strings.ErrorAccessDenied) : null;
		}

		return Commands.FormatStaticResponse(string.Format(CultureInfo.CurrentCulture, Strings.BotVersion, nameof(ItemBuyerPlugin), typeof(ItemBuyerPlugin).Assembly.GetName().Version));
	}

	public Task OnLoaded() => Task.CompletedTask;
}
