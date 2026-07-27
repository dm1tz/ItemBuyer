## Installation
- Download the latest `ItemBuyer.zip` archive from the [release page](https://github.com/dm1tz/ItemBuyer/releases/latest).
- Extract archive contents into the ASF `plugins` directory.

## Configuration

ItemBuyer configuration is appended to `ASF.json` and has the following structure:
```json
{
	...
	"ItemBuyerPlugin": {
		"BuyItemLimiterDelay": 1
	}
}
```

All options are explained below:

### `BuyItemLimiterDelay`
`byte` type with the default value of `1`. This property defines, in seconds, the minimum delay between each buy request.

## Commands

Command | Alias | Access | Description
--- | --- | --- | ---
`buyitem [Bots] <AppID> <ItemDefID> <Quantity>` | `bi`, `ibb` | `Master` | Purchases specified item for given bot instances.
`checkprice [Bots] <AppID> <ItemDefID> <Quantity>` | `cp`, `ibc` | `FamilySharing` | Reports specified item's total price for given bot instances.
`ibversion` | `ibv` | `FamilySharing` | Prints the plugin version.
