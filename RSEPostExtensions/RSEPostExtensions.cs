using FrooxEngine;
using HarmonyLib;
using ResoniteModLoader;
using RSE = ResoniteScreenshotExtensions.ResoniteScreenshotExtensions;
using Metadata = ResoniteScreenshotExtensions.Metadata;
using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Concurrent;
using Elements.Core;





#if DEBUG
using ResoniteHotReloadLib;
#endif

namespace RSEPostExtensions;

public class RSEPostExtensions : ResoniteMod {
	internal const string VERSION_CONSTANT = "1.0.0"; //Changing the version here updates it in all locations needed
	public override string Name => "RSEPostExtensions";
	public override string Author => "Noble";
	public override string Version => VERSION_CONSTANT;
	public override string Link => "https://github.com/noblereign/ResoniteRSEPostExtensions/";

	const string harmonyId = "dog.glacier.RSEPostExtensions";

	public static ModConfiguration? Config;

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<bool> Enabled = new("Enabled", "Enables the mod.", () => true);

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<string> DiscordURLs = new("Discord URLs", "Comma-seperated list of Discord webhook urls. Add a query parameter to label them in the menu, like so: 'https://discord.com/api/webhooks/1234/key?<LABEL GOES HERE>'", () => "");


	static readonly Uri POST_TO_URI = new Uri("resdb:///b5dc11709108e26d9e9788401111a15000813a262e2c7ebee2109c4321a92ad1");
	const string MENU_ITEM_TAG = "RSE_POST_TO_DISCORD";
	private static readonly ConcurrentDictionary<string, Uri> _urlMappings = new();

	public override void OnEngineInit() {
#if DEBUG
		HotReloader.RegisterForHotReload(this);
#endif

		Config = GetConfiguration()!;
		Config!.Save(true);

		Engine.Current.RunPostInit(() =>
		{
			Setup();
		});
	}

	static void Setup() {
		PatchRSE();
	}

#if DEBUG
	// This is the method that should be used to unload your mod
	static void BeforeHotReload() {
		// Unpatch Harmony
		Harmony harmony = new Harmony(harmonyId);
		harmony.UnpatchAll(harmonyId);
	}

	// This is called in the newly loaded assembly
	static void OnHotReload(ResoniteMod modInstance) {
		// Get the config if needed
		Config = modInstance.GetConfiguration()!;
		Config!.Save(true);

		// Call setup method
		Setup();
	}
#endif


	public static void PatchRSE() {
		var targetPostfix = SymbolExtensions.GetMethodInfo(() =>
			RSE.PhotoMetadata_Patch.GenerateMenuItems_Postfix(null!, null!)
		);

		if (targetPostfix != null) {
			Harmony harmony = new Harmony(harmonyId);
			var myPrefix = new HarmonyMethod(typeof(RSEPostExtensions), nameof(RSEPostExtensions.ContextMenuHook));

			harmony.Patch(targetPostfix, prefix: myPrefix);
		}
	}

	static bool ContextMenuHook(PhotoMetadata __instance, ContextMenu menu) {
		if (!__instance.Enabled) return false;
		if (!Config!.GetValue(Enabled)) return true;

		var item = menu.Slot.GetComponentInChildren<ContextMenuItem>((i) => i.Slot.Tag == MENU_ITEM_TAG);
		if (item == null) {
			item = menu.AddItem("Post to...", POST_TO_URI, null);
			item.Slot.Tag = MENU_ITEM_TAG;
		}

		item.Button.LocalPressed += async (button, eventData) =>
		{
			// render like a submenu
			await new ToWorld();
			var newMenu = await __instance.LocalUser.OpenContextMenu(menu.CurrentSummoner, menu.Pointer.Target, options: new ContextMenuOptions { speedOverride = 12 });
			string discordWebhookUrlStringList = Config!.GetValue(DiscordURLs) ?? ModLoader.Mods().FirstOrDefault(m => m.Name == "ResoniteScreenshotExtensions")?.GetConfiguration()?.GetValue(RSE.DiscordWebhookUrlKey) ?? "";
			string[] discordWebhookUrls = discordWebhookUrlStringList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

			int webhookCount = 0;
			foreach (string url in discordWebhookUrls) {
				webhookCount++;
				string[] urlParts = url.Split('?');
				string baseUrl = urlParts[0];
				string webhookName = urlParts.Length > 1 ? urlParts[1] : $"#{webhookCount} aka {Util.GetMemorableName(baseUrl)}";
				// could technically automatically name it from discord, but i don't wanna deal with possible ratelimiting...

				ContextMenuItem menuItem = newMenu.AddItem(webhookName, RSE.PhotoMetadata_Patch.DISCORD_ICON_URI, null);
				menuItem.Button.LocalPressed += (button, eventData) => {
					PostToDiscord(__instance, new Uri(baseUrl));
				};
			}
		};
		return false;
	}

	static void PostToDiscord(PhotoMetadata photo, Uri webhookUri) {
		photo.LocalUser.CloseContextMenu(null!);
		Msg("Posting to Discord...");

		photo.StartGlobalTask(async () =>
		{
			var tex = photo.Slot.GetComponent<StaticTexture2D>();
			var url = tex?.URL.Value;
			if (url is null) return;
		
			await new ToBackground();
			var tmpPath = await photo.Engine.AssetManager.GatherAssetFile(url, 100f);
			if (tmpPath is null) return;
			_urlMappings[tmpPath] = webhookUri;
			RSE.PhotoMetadata_Patch.PostToDiscord(new Metadata(photo), tmpPath);
		});
	}

	[HarmonyPatch(typeof(RSE.PhotoMetadata_Patch), "PostToDiscord")]
	public static class OverrideDiscordUrlPatch {
		static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
			MethodInfo targetGetValue = AccessTools.Method(typeof(ModConfiguration), nameof(ModConfiguration.GetValue))
												   .MakeGenericMethod(typeof(string));

			MethodInfo myMiddleman = AccessTools.Method(typeof(OverrideDiscordUrlPatch), nameof(CustomConfigInterceptor));

			foreach (var instruction in instructions) {
				if (instruction.Calls(targetGetValue)) {
					// add filepath to the stack then intercept
					yield return new CodeInstruction(OpCodes.Ldarg_1);
					yield return new CodeInstruction(OpCodes.Call, myMiddleman);
				} else {
					yield return instruction;
				}
			}
		}

		public static string? CustomConfigInterceptor(ModConfiguration configInstance, ModConfigurationKey<string> key, string filePath) {
			if (Config!.GetValue(Enabled) && key == RSE.DiscordWebhookUrlKey) {
				if (_urlMappings.TryRemove(filePath, out Uri? webhookUri)) {
					return webhookUri.ToString();
				}
				Warn("Couldn't find webhook URI mapping!! ABORT!!!");
				NotificationMessage.SpawnTextMessage("[RSEPostExtensions] Failed to post to Discord, please try again!", colorX.Red, 0.7f, 5f);
				return "";
			}
			return configInstance.GetValue(key);
		}
	}
}
