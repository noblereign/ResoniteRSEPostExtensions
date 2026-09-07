using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Web;
using Elements.Core;
using FrooxEngine;
using HarmonyLib;
using ResoniteModLoader;
using SkyFrost.Base;

using Metadata = ResoniteScreenshotExtensions.Metadata;
using RSE = ResoniteScreenshotExtensions.ResoniteScreenshotExtensions;


#if DEBUG
using ResoniteHotReloadLib;
#endif

namespace RSEPostExtensions;

public class RSEPostExtensions : ResoniteMod {
	internal const string VERSION_CONSTANT = "3.0.0"; //Changing the version here updates it in all locations needed
	public override string Name => "RSEPostExtensions";
	public override string Author => "Noble";
	public override string Version => VERSION_CONSTANT;
	public override string Link => "https://github.com/noblereign/ResoniteRSEPostExtensions/";

	const string harmonyId = "dog.glacier.RSEPostExtensions";

	public static ModConfiguration? Config;

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<bool> Enabled = new("Enabled", "Enables the mod.", () => true);

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<bool> DoubleClickConfirm = new("Double click to confirm", "Double click to confirm upload?", () => false);

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<bool> FetchMetadataFromWeb = new("Fetch metadata from web", "For certain posting types (e.g. Misskey), should the mod populate names and button icons using external sources?", () => false);

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<string> DiscordURLs = new("Discord URLs", "Comma-seperated list of Discord webhook urls.\n\nYou can nickname them with a 'query parameter', e.g.\n'https://discord.com/api/webhooks/1234/key<color=hero.yellow>?<LABEL GOES HERE></color>'", () => "");

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<string> MisskeyURLs = new("Misskey access tokens", "Comma-seperated list of Misskey URLs and access tokens.\n\nThe access token should only have the <b>'Access your Drive files and folders'</b> and <b>'Edit or delete your Drive files and folders'</b> permissions.\n\nFormat entries like this:\n<color=hero.yellow>https://example.com</color>?<color=hero.purple><token></color>", () => "");

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<string> MisskeyFolderName = new("Misskey Folder Name", "What should the photos folder in the Misskey Drive be called?", () => "Resonite");

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<string> MisskeyPostBody = new("Misskey Post Body", "What should be written by default when sharing from Misskey?\n\nAvailable tokens:\n<Location>\n<Host>\n<Photographer>\n<Time>\n<AppVersion>\n<RendererName>\n<CameraManufacturer>\n<CameraModel>\n<CameraFOV>", () => "#Resonite");

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<string> GalleVRToken = new("GalleVR Token", "GalleVR login token.\n\nThis can be obtained (scuffedly) by opening the Developer Tools in your browser, going to the Network tab, and looking for a request header containing 'Authorization: Bearer'. You'll only paste in the random characters that come after Bearer.", () => "");

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<bool> Multiposting = new("Multiposting", "Keep the context menu open after choosing an option?", () => true);


	static readonly Uri POST_TO_URI = new Uri("resdb:///b5dc11709108e26d9e9788401111a15000813a262e2c7ebee2109c4321a92ad1");
	static readonly Uri FEDIVERSE_URI = new Uri("resdb:///87b9aee416b10948cab768e3ada9f5537d453f78971d6537ab4ad818e90a5daf.webp");
	static readonly Uri GALLEVR_URI = new Uri("resdb:///33070a028930ac3e520443134d5afd04bfcc57dc7a2b17e612147c52ca0f73c7.png");
	static readonly Uri ERROR_URI = new Uri("resdb:///92a0b1cf9536b1e675e3e1c4db52133c5dc0596d128d7bb91582ce75bfb6a9da");

	const string MENU_ITEM_TAG = "RSE_POST_TO_DISCORD";
	private static readonly ConcurrentDictionary<string, Uri> _urlMappings = new();
	private static readonly ConcurrentDictionary<string, string> _misskeyFolderCache = new();
	enum ButtonState { Idle, Confirming, Uploading, Success, Error }

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
		Harmony harmony = new Harmony(harmonyId);
		harmony.PatchAll();
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

	static void GenerateDiscordButtons(ContextMenu menu, PhotoMetadata metadata) {
		string? PEUrls = Config!.GetValue(DiscordURLs) is string s && !string.IsNullOrWhiteSpace(s) ? s : null;
		ModConfiguration? RSEConfig = ModLoader.Mods().FirstOrDefault(m => m.Name == "ResoniteScreenshotExtensions")?.GetConfiguration();
		ModConfigurationKey? RSEDiscordUrlKey = RSEConfig?.ConfigurationItemDefinitions.FirstOrDefault(m => m.Name == "DiscordWebhookUrl");
		string discordWebhookUrlStringList = PEUrls ??
			(RSEDiscordUrlKey != null && RSEConfig!.TryGetValue(RSEDiscordUrlKey, out object? RSEFallbackUrl)
			? RSEFallbackUrl as string
			: null) ?? "";
		string[] discordWebhookUrls = discordWebhookUrlStringList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		int webhookCount = 0;
		foreach (string url in discordWebhookUrls) {
			webhookCount++;
			string[] urlParts = url.Split('?');
			string baseUrl = urlParts[0];
			string webhookName = urlParts.Length > 1 ? urlParts[1] : $"#{webhookCount}<alpha=#77><size=75%> aka </alpha></size>{Util.GetMemorableName(baseUrl)}";

			ContextMenuItem menuItem = menu.AddItem(webhookName, RSE.PhotoMetadata_Patch.DISCORD_ICON_URI, null);

			bool isConfirming = false;

			menuItem.Button.LocalPressed += (button, eventData) => {
				if (!menuItem.Button.Enabled) return;

				if (Config!.GetValue(DoubleClickConfirm) && !isConfirming) {
					isConfirming = true;
					menuItem.Label.Target.Value = $"<color=hero.red>Really post to </color><b>{webhookName}</b>?";

					menuItem.RunInSeconds(3f, () => {
						if (isConfirming) {
							isConfirming = false;
							if (menuItem != null && !menuItem.IsRemoved && menuItem.Button.Enabled) {
								menuItem.Label.Target.Value = webhookName;
							}
						}
					});
					return;
				}
				menuItem.Button.Enabled = false;
				menuItem.Label.Target.Value = $"<i>Sent to {webhookName}";
				

				PostToDiscord(metadata, new Uri(baseUrl));
			};
		}
	}

	public static Uri BuildMkShareUri(string baseUrl, string relativePath, string text, string fileId) {
		Uri baseUri = new Uri(baseUrl.TrimEnd('/') + "/");
		Uri fullEndpoint = new Uri(baseUri, relativePath.TrimStart('/'));
		UriBuilder builder = new UriBuilder(fullEndpoint);
		var query = HttpUtility.ParseQueryString(builder.Query);
		query["text"] = text;
		query["fileIds"] = fileId;
		builder.Query = query.ToString();

		return builder.Uri;
	}

	static async Task<string> FormatMisskeyPostBody(string template, PhotoMetadata photo, string photographerName) {
		if (string.IsNullOrWhiteSpace(template)) return "";

		var time = photo.TimeTaken.Value.Kind == DateTimeKind.Utc ? photo.TimeTaken.Value.ToLocalTime() : photo.TimeTaken.Value;

		string Fallback(string? val) => string.IsNullOrWhiteSpace(val) ? "Unknown" : val;

		string hostName = await Util.GetUsernameFromUserId(photo.LocationHost._userId.Value, photo.World);

		return template
			.Replace("<Location>", Fallback(RSE.PhotoMetadata_Patch.SanitizeText(photo.LocationName)))
			.Replace("<Host>", Fallback(hostName))
			.Replace("<Photographer>", Fallback(photographerName))
			.Replace("<Time>", time.ToString("yyyy-MM-dd HH:mm"))
			.Replace("<AppVersion>", Fallback(photo.AppVersion.Value))
			.Replace("<RendererName>", Fallback(photo.RendererName.Value))
			.Replace("<CameraManufacturer>", Fallback(photo.CameraManufacturer.Value))
			.Replace("<CameraModel>", Fallback(photo.CameraModel.Value))
			.Replace("<CameraFOV>", photo.CameraFOV.Value.ToString("0.##"));
	}

	static void GenerateMisskeyButtons(ContextMenu menu, PhotoMetadata metadata) {
		string? urlsStringList = Config!.GetValue(MisskeyURLs) is string s && !string.IsNullOrWhiteSpace(s) ? s : null;
		if (urlsStringList == null) {
			return;
		}
		string[] urls = urlsStringList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		foreach (string url in urls) {
			string[] urlParts = url.Split('?');
			string baseUrl = urlParts[0];
			if (urlParts.Length <= 1) {
				menu.AddItem($"Missing access token for {baseUrl}", ERROR_URI, null);
				continue;
			}
			string token = urlParts[1];

			string currentInstanceName = baseUrl;
			string currentDriveStats = "<alpha=#50>...</alpha>";

			ContextMenuItem menuItem = menu.AddItem(currentInstanceName, FEDIVERSE_URI, null);
			ButtonState currentState = ButtonState.Idle;
			colorX currentColor = menuItem.Color.Value;
			menuItem.Color.ActiveLink.ReleaseLink();

			void UpdateButtonVisuals() {
				if (menuItem == null || menuItem.IsRemoved) return;
				menuItem.Button.Enabled = currentState != ButtonState.Uploading;

				switch (currentState) {
					case ButtonState.Idle:
						menuItem.Label.Target.Value = currentInstanceName + $"\n<size=60%><color=#bcbcbc>{currentDriveStats}</color></size>";
						menuItem.Color.Value = currentColor;
						break;
					case ButtonState.Confirming:
						menuItem.Label.Target.Value = $"<color=hero.red>Really upload to </color><b>{currentInstanceName}?</b>\n<size=60%><color=#bcbcbc>{currentDriveStats}</color></size>";
						menuItem.Color.Value = currentColor;
						break;
					case ButtonState.Uploading:
						menuItem.Label.Target.Value = "Uploading...";
						menuItem.Color.Value = currentColor;
						break;
					case ButtonState.Success:
						menuItem.Label.Target.Value = $"Uploaded!\n<size=60%><color=#bcbcbc>Click to share on {currentInstanceName}</color></size>";
						menuItem.Color.Value = RadiantUI_Constants.Hero.GREEN;
						break;
					case ButtonState.Error:
						menuItem.Label.Target.Value = $"<color=hero.red>Upload Failed</color>\n<size=60%><color=#bcbcbc>Click to try again</color></size>";
						menuItem.Color.Value = RadiantUI_Constants.Hero.RED;
						break;
				}
			}

			menuItem.Button.LocalPressed += async (button, eventData) => {
				if (currentState == ButtonState.Uploading || !menuItem.Button.Enabled) return;

				if (currentState == ButtonState.Success) {
					return;
				}

				if (Config!.GetValue(DoubleClickConfirm) && currentState == ButtonState.Idle) {
					currentState = ButtonState.Confirming;
					UpdateButtonVisuals();

					menuItem.RunInSeconds(3f, () => {
						if (currentState == ButtonState.Confirming) {
							currentState = ButtonState.Idle;
							UpdateButtonVisuals();
						}
					});
					return;
				}

				currentState = ButtonState.Uploading;
				UpdateButtonVisuals();

				(string? fileId, string? postBody) uploadResult = await PostToMisskeyAsync(metadata, new Uri(baseUrl), token);

				if (menuItem == null || menuItem.IsRemoved) return;

				if (uploadResult.fileId != null) {
					menuItem.RunSynchronously(() => {
						Hyperlink linkComponent = menuItem.Slot.AttachComponent<Hyperlink>();
						linkComponent.URL.Value = BuildMkShareUri(baseUrl, "/share", uploadResult.postBody ?? "", uploadResult.fileId);
						linkComponent.Reason.Value = $"Share this photo on {currentInstanceName}";
						currentState = ButtonState.Success;
					});
				} else {
					currentState = ButtonState.Error;
				}
				menuItem.RunSynchronously(() => {
					UpdateButtonVisuals();
				});
			};

			if (!Config!.GetValue(FetchMetadataFromWeb)) return;

			_ = UpdateManifestUIAsync();
			_ = UpdateDriveUsageUIAsync();

			async Task UpdateManifestUIAsync() {
				try {
					WebManifest? manifest = await Util.GetManifestAsync(baseUrl);
					if (manifest == null) return;

					menuItem.RunSynchronously(() => {
						if (menuItem.IsRemoved) return;

						string newName = manifest.Name ?? baseUrl;
						currentInstanceName = currentInstanceName.Replace(baseUrl, newName);
						
						if (manifest.ThemeColor != null) {
							currentColor = colorX.FromHexCode(manifest.ThemeColor);
						}

						var iconSrc = manifest.Icons?.GetFirst()?.Src;
						if (iconSrc != null && Uri.TryCreate(new Uri(baseUrl), iconSrc, out Uri? absoluteUri)) {
							menuItem.SpriteURL = absoluteUri;
						}
						UpdateButtonVisuals();
					});
				} catch {}
			}

			async Task UpdateDriveUsageUIAsync() {
				try {
					string? driveUsage = await MisskeyDriveClient.GetHumanReadableDriveUsageAsync(new Uri(baseUrl), token);
					if (driveUsage == null) return;

					menuItem.RunSynchronously(() => {
						if (menuItem.IsRemoved) return;
						currentDriveStats = driveUsage;
						UpdateButtonVisuals();
					});
				} catch {}
			}
		}
	}

	static async Task<(string? fileId, string? postBody)> PostToMisskeyAsync(PhotoMetadata photo, Uri instanceUrl, string accessToken) {
		if (!Config!.GetValue(Multiposting)) {
			photo.LocalUser.CloseContextMenu(null!);
		}
		Msg("Posting to Misskey...");

		var tex = photo.Slot.GetComponent<StaticTexture2D>();
		var url = tex?.URL.Value;
		if (url is null) return (null, null);

		Slot? extendedTagSlot = photo.Slot.FindChild("PhotoMetadata_Tags");

		string photographerName = await Util.GetUsernameFromUserId(photo.TakenBy._userId, photo.World);
		string altText = $"A Resonite photo taken by {photographerName} in {RSE.PhotoMetadata_Patch.SanitizeText(photo.LocationName)}.";
		string postBodyText = await FormatMisskeyPostBody(Config!.GetValue(MisskeyPostBody) ?? "", photo, photographerName);

		if (extendedTagSlot != null) {
			DynamicVariableSpace extendedTagSpace = extendedTagSlot.FindSpace("Avatar");
			if (extendedTagSpace != null) {
				List<(string username, float screenX)> usersData = new List<(string, float)>();
				bool isSelfie = false;

				foreach (AssetMetadata.UserInfo userInfo in photo.UserInfos) {
					extendedTagSpace.TryReadValue<bool>($"PhotoMetadata/{userInfo.User._userId}/isInView", out bool isInView);

					if (isInView) {
						string username = await Util.GetUsernameFromUserId(userInfo.User._userId, photo.World);

						if (userInfo.User._userId == photo.TakenBy._userId) {
							isSelfie = true;
						}

						float3 userPos = userInfo.HeadPosition;
						float3 toUser = userPos - photo.TakenGlobalPosition.Value;

						floatQ inverseCamRot = photo.TakenGlobalRotation.Value.Inverted;
						float3 localPos = inverseCamRot * toUser;

						float screenX = localPos.x / localPos.z;

						usersData.Add((username, screenX));
					}
				}

				// sort by left to right
				string[] visibleUsers = usersData
					.OrderBy(u => u.screenX)
					.Select(u => u.username)
					.ToArray();

				string baseText = $"A {(isSelfie ? "selfie" : "photo")} taken by {photographerName} on Resonite. Captured in {RSE.PhotoMetadata_Patch.SanitizeText(photo.LocationName)}";
				string formattedUsers = ".";

				if (visibleUsers.Length == 1) {
					if (!isSelfie) {
						formattedUsers = $", featuring {visibleUsers[0]}.";
					}
				} else if (visibleUsers.Length == 2) {
					formattedUsers = $", with {visibleUsers[0]} and {visibleUsers[1]} together in the {(isSelfie ? "shot" : "photo")}.";
				} else if (visibleUsers.Length >= 3) {
					formattedUsers = $". {visibleUsers.Length} users are visible.\n Listed from left to right, they are: {string.Join(", ", visibleUsers.Take(visibleUsers.Length - 1))}, and {visibleUsers.Last()}.";
				}

				altText = $"{baseText}{formattedUsers}";
			}
		}

		await new ToBackground();

		try {
			string folderName = Config?.GetValue(MisskeyFolderName) ?? "Resonite";
			string fileName = (photo.TimeTaken.Value.Kind == DateTimeKind.Utc ? photo.TimeTaken.Value.ToLocalTime() : photo.TimeTaken.Value).ToString("yyyy-MM-dd HH.mm.ss");

			var gatherTask = photo.Engine.AssetManager.GatherAssetFile(url, 100f).AsTask();
			var folderTask = GetCachedFolderIdAsync(instanceUrl, accessToken, folderName);

			await Task.WhenAll((Task)gatherTask, folderTask);

			string? tmpPath = await gatherTask;
			string? folderId = await folderTask;

			if (tmpPath is null) {
				return (null, null);
			}

			fileName = $"{fileName}{Path.GetExtension(tmpPath)}";

			MisskeyDriveClient.DriveFile? file = await MisskeyDriveClient.UploadFileAsync(instanceUrl, accessToken, tmpPath, folderId, fileName, altText);

			Msg($"Upload finished!");
			return (file?.Id, postBodyText);

		} catch (Exception ex) {
			Msg($"Misskey upload failed: {ex.Message}");

			NotificationMessage.SpawnTextMessage("[RSEPostExtensions] Failed to post to Misskey!", colorX.Red, 0.7f, 5f);

			return (null, null);
		}
	}

	private static async Task<string?> GetCachedFolderIdAsync(Uri instanceUrl, string token, string folderName) {
		string cacheKey = $"{instanceUrl.AbsoluteUri}|{folderName}";

		if (_misskeyFolderCache.TryGetValue(cacheKey, out string? cachedId))
			return cachedId;

		var folder = await MisskeyDriveClient.GetOrCreateFolderAsync(instanceUrl, token, folderName);

		if (folder?.Id != null)
			_misskeyFolderCache[cacheKey] = folder.Id;

		return folder?.Id;
	}

	static void GenerateGalleVRButton(ContextMenu menu, PhotoMetadata metadata) {
		string? gvrToken = Config!.GetValue(GalleVRToken) is string s && !string.IsNullOrWhiteSpace(s) ? s : null;
		if (gvrToken == null) {
			return;
		}

		colorX currentColor = colorX.FromHexCode("4E1D89");
		ContextMenuItem menuItem = menu.AddItem("GalleVR", GALLEVR_URI, currentColor);

		if (!CheckGalleValidity(metadata)) {
			menuItem.Button.Enabled = false;
			return;
		}

		ButtonState currentState = ButtonState.Idle;
		

		void UpdateButtonVisuals() {
			if (menuItem == null || menuItem.IsRemoved) return;
			menuItem.Button.Enabled = currentState != ButtonState.Uploading;

			switch (currentState) {
				case ButtonState.Idle:
					menuItem.Label.Target.Value = "GalleVR";
					menuItem.Color.Value = currentColor;
					break;
				case ButtonState.Confirming:
					menuItem.Label.Target.Value = $"<color=hero.red>Really upload to </color><b>GalleVR?</b>";
					menuItem.Color.Value = currentColor;
					break;
				case ButtonState.Uploading:
					menuItem.Label.Target.Value = "Uploading...";
					menuItem.Color.Value = currentColor;
					break;
				case ButtonState.Success:
					menuItem.Label.Target.Value = $"Uploaded!\n<size=60%><color=#bcbcbc>Click to open your gallery</color></size>";
					menuItem.Color.Value = RadiantUI_Constants.Hero.GREEN;
					break;
				case ButtonState.Error:
					menuItem.Label.Target.Value = $"<color=hero.red>Upload Failed</color>\n<size=60%><color=#bcbcbc>Click to try again</color></size>";
					menuItem.Color.Value = RadiantUI_Constants.Hero.RED;
					break;
			}
		}

		menuItem.Button.LocalPressed += async (button, eventData) => {
			if (currentState == ButtonState.Uploading || !menuItem.Button.Enabled) return;

			if (currentState == ButtonState.Success) {
				return;
			}

			if (Config!.GetValue(DoubleClickConfirm) && currentState == ButtonState.Idle) {
				currentState = ButtonState.Confirming;
				UpdateButtonVisuals();

				menuItem.RunInSeconds(3f, () => {
					if (currentState == ButtonState.Confirming) {
						currentState = ButtonState.Idle;
						UpdateButtonVisuals();
					}
				});
				return;
			}

			currentState = ButtonState.Uploading;
			UpdateButtonVisuals();

			string? uploadResult = await PostToGalleVRAsync(metadata, gvrToken);

			if (menuItem == null || menuItem.IsRemoved) return;

			if (uploadResult != null) {
				menuItem.RunSynchronously(() => {
					Hyperlink linkComponent = menuItem.Slot.AttachComponent<Hyperlink>();
					linkComponent.URL.Value = new Uri("https://gallevr.app/gallery");
					linkComponent.Reason.Value = "Manage your GalleVR photos";
					currentState = ButtonState.Success;
				});
			} else {
				currentState = ButtonState.Error;
			}
			menuItem.RunSynchronously(() => {
				UpdateButtonVisuals();
			});
		};
	}
	static bool CheckGalleValidity(PhotoMetadata photo) {
		if (photo == null) return false;
		if (string.IsNullOrWhiteSpace(photo.CameraManufacturer.Value)) return false;
		if (photo.TakenBy._userId.Value != Engine.Current.Cloud.CurrentUserID) return false;

		return true;
	}

	static async Task<string?> PostToGalleVRAsync(PhotoMetadata photo, string token) {
		if (!Config!.GetValue(Multiposting)) {
			photo.LocalUser.CloseContextMenu(null!);
		}
		Msg("Posting to GalleVR...");

		var tex = photo.Slot.GetComponent<StaticTexture2D>();
		var url = tex?.URL.Value;
		if (url is null) return null;

		List<GalleVRClient.Player> galleVRPlayers = new();
		foreach (AssetMetadata.UserInfo userInfo in photo.UserInfos) {
			galleVRPlayers.Add(new GalleVRClient.Player {
				Id = userInfo.User._userId,
				Name = await Util.GetUsernameFromUserId(userInfo.User._userId, photo.World)
			});
		}

		await new ToBackground();

		try {
			
			var gatherTask = photo.Engine.AssetManager.GatherAssetFile(url, 100f).AsTask();
			await Task.WhenAll((Task)gatherTask);

			string? tmpPath = await gatherTask;

			if (tmpPath is null) {
				return null;
			}

			GalleVRClient.GalleVRPhotoMetadata formattedMetadata = new() {
				TakenById = photo.TakenBy._userId.Value,
				TakenDate = new DateTimeOffset(photo.TimeTaken.Value).ToUnixTimeMilliseconds(),
				Filename = (photo.TimeTaken.Value.Kind == DateTimeKind.Utc ? photo.TimeTaken.Value.ToLocalTime() : photo.TimeTaken.Value).ToString("yyyy-MM-dd HH.mm.ss") + ".webp",
				LocalPath = tmpPath,
				CameraManufacturer = photo.CameraManufacturer.Value,
				Application = "Resonite",
				IsNonVrcx = false,
				CameraFov = photo.CameraFOV.Value,
				TakenGlobalPosition = GalleVRClient.SpatialData.FromFloat3(photo.TakenGlobalPosition.Value),
				TakenGlobalRotation = GalleVRClient.SpatialData.FromFloatQ(photo.TakenGlobalRotation.Value),
				TakenGlobalScale = GalleVRClient.SpatialData.FromFloat3(photo.TakenGlobalScale.Value),
				World = new GalleVRClient.WorldMetadata() {
					Id = photo.LocationURL.Value.OriginalString,
					Name = photo.LocationName.Value
				},
				Players = galleVRPlayers
			};

			string? photoUrl = await GalleVRClient.UploadFileAsync(photo.TakenBy._userId, token, tmpPath, formattedMetadata, photo);
			// GalleVR returns the photo link (yay!) but going to it just redirects to an empty profile (bruh)
			Msg($"Upload finished!");
			return photoUrl;

		} catch (Exception ex) {
			Msg($"Misskey upload failed: {ex.Message}");

			NotificationMessage.SpawnTextMessage("[RSEPostExtensions] Failed to post to GalleVR!", colorX.Red, 0.7f, 5f);

			return null;
		}
	}

	static bool ContextMenuHook([HarmonyArgument(0)] PhotoMetadata hookedInstance, ContextMenu menu) {
		if (!hookedInstance.Enabled) return false;
		if (!Config!.GetValue(Enabled)) return true;

		var item = menu.Slot.GetComponentInChildren<ContextMenuItem>((i) => i.Slot.Tag == MENU_ITEM_TAG);
		if (item == null) {
			item = menu.AddItem("Post to...", POST_TO_URI, null);
			item.Slot.Tag = MENU_ITEM_TAG;

			item.Button.LocalPressed += (button, eventData) =>
			{
				// render like a submenu
				_ = button.World.Coroutines.StartTask(async delegate {
					ContextMenu newMenu = await hookedInstance.LocalUser.OpenContextMenu(menu.CurrentSummoner, menu.Pointer.Target, options: new ContextMenuOptions { speedOverride = 10 });
					GenerateDiscordButtons(newMenu, hookedInstance);
					GenerateMisskeyButtons(newMenu, hookedInstance);
					GenerateGalleVRButton(newMenu, hookedInstance);
				});
			};
		}

		return false;
	}

	static void PostToDiscord(PhotoMetadata photo, Uri webhookUri) {
		if (!Config!.GetValue(Multiposting)) {
			photo.LocalUser.CloseContextMenu(null!);
		}
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
			MethodInfo myMiddleman = AccessTools.Method(typeof(OverrideDiscordUrlPatch), nameof(CustomConfigInterceptor));

			foreach (var instruction in instructions) {
				if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) &&
					instruction.operand is MethodInfo mi &&
					mi.Name == nameof(ModConfiguration.GetValue) &&
					mi.ReturnType == typeof(string)) {
					// Inject file path and hijack the call
					yield return new CodeInstruction(OpCodes.Ldarg_1);
					yield return new CodeInstruction(OpCodes.Call, myMiddleman);
				} else {
					yield return instruction;
				}
			}
		}

		public static string? CustomConfigInterceptor(ModConfiguration configInstance, ModConfigurationKey<string> key, string filePath) {
			if (Config!.GetValue(Enabled) && key == RSE.DiscordWebhookUrlKey) {
				if (_urlMappings.TryGetValue(filePath, out Uri? webhookUri)) {
					return webhookUri.ToString();
				}
				Warn("Couldn't find webhook URI mapping!! ABORT!!!");
				NotificationMessage.SpawnTextMessage("[RSEPostExtensions] Failed to post to Discord, please try again!", colorX.Red, 0.7f, 5f);
				return "";
			}
			return configInstance.GetValue(key);
		}

		[HarmonyFinalizer]
		public static void Postfix(string filePath) {
			_urlMappings.TryRemove(filePath, out _);
			Msg("Completed!");
		}
	}
}
