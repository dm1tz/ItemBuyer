using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace ItemBuyer;

[SuppressMessage("Microsoft.Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Used during JSON deserialization")]
internal sealed class ItemBuyerConfig {
	internal const byte DefaultBuyItemLimiterDelay = 1;

	[JsonInclude]
	internal byte BuyItemLimiterDelay { get; private init; } = DefaultBuyItemLimiterDelay;

	[JsonConstructor]
	private ItemBuyerConfig() { }
}
