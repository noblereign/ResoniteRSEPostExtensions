using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Web;
using Elements.Core;
using FrooxEngine;
using HarmonyLib;
using ResoniteModLoader;
using Metadata = ResoniteScreenshotExtensions.Metadata;
using RSE = ResoniteScreenshotExtensions.ResoniteScreenshotExtensions;


#if DEBUG
using ResoniteHotReloadLib;
#endif

namespace RSEPostExtensions;

public class RSEPostExtensions : ResoniteMod {
	internal const string VERSION_CONSTANT = "4.0.0"; //Changing the version here updates it in all locations needed
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
	public static readonly ModConfigurationKey<bool> Multiposting = new("Multiposting", "Keep the context menu open after choosing an option?", () => true);

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<string> UnpublishedWorldFallback = new("Text Fallback for Unpublished Worlds", "If a world isn't published, what should the 'WorldLink' token be replaced by?", () => "🔐 World Not Published");

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<string> DiscordURLs = new("Discord URLs", "Comma-seperated list of Discord webhook urls.\n\nYou can nickname them with a 'query parameter', e.g.\n'https://discord.com/api/webhooks/1234/key<color=hero.yellow>?<LABEL GOES HERE></color>'", () => "");

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<string> MisskeyURLs = new("Misskey access tokens", "Comma-seperated list of Misskey URLs and access tokens.\n\nThe access token should only have the <b>'Access your Drive files and folders'</b> and <b>'Edit or delete your Drive files and folders'</b> permissions.\n\nFormat entries like this:\n<color=hero.yellow>https://example.com</color>?<color=hero.purple><token></color>", () => "");

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<string> MisskeyFolderName = new("Misskey Folder Name", "What should the photos folder in the Misskey Drive be called?", () => "Resonite");

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<string> MisskeyPostBody = new("Misskey Post Body", "What should be written by default when sharing from Misskey?\n\nAvailable tokens:\n<Location>\n<Host>\n<WorldName>\n<WorldOwner>\n<WorldLink:OptionalText>\n<Photographer>\n<Time>\n<AppVersion>\n<RendererName>\n<CameraManufacturer>\n<CameraModel>\n<CameraFOV>", () => "#Resonite");

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<string> BlueskyLogins = new("Bluesky logins", "Comma-seperated list of Bluesky identities and app passwords.\n\n<b>Always</b> use an app password!\n\nFormat entries like this:\n<color=hero.yellow>your.handle.here</color>?<color=hero.purple><app password></color>", () => "");

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<string> BlueskyPostBody = new("Bluesky Post Body", "What should be written by default when sharing from Bluesky?\n\n<color=hero.red>Note that Bluesky has a <b>300 character limit.</b></color>\n\nAvailable tokens:\n<Location>\n<Host>\n<WorldName>\n<WorldOwner>\n<WorldLink:OptionalText>\n<Photographer>\n<Time>\n<AppVersion>\n<RendererName>\n<CameraManufacturer>\n<CameraModel>\n<CameraFOV>", () => "#Resonite");

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<bool> BlueskyLinkCard = new("Bluesky link cards", "When sharing to Bluesky, should the post use a link card to the world (if published) instead of an image?\n\n<color=hero.yellow>Note: Compression will be heavier with this feature on.", () => false);

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<string> GalleVRToken = new("GalleVR Token", "GalleVR login token.\n\nThis can be obtained (scuffedly) by opening the Developer Tools in your browser, going to the Network tab, and looking for a request header containing 'Authorization: Bearer'. You'll only paste in the random characters that come after Bearer.", () => "");

	static readonly Uri POST_TO_URI = new Uri("resdb:///b5dc11709108e26d9e9788401111a15000813a262e2c7ebee2109c4321a92ad1");
	static readonly Uri FEDIVERSE_URI = new Uri("resdb:///87b9aee416b10948cab768e3ada9f5537d453f78971d6537ab4ad818e90a5daf.webp");
	static readonly Uri GALLEVR_URI = new Uri("resdb:///33070a028930ac3e520443134d5afd04bfcc57dc7a2b17e612147c52ca0f73c7.png");
	static readonly Uri BLUESKY_URI = new Uri("resdb:///ea11f4de18ccdf349bb0b896d24244adcb048979b1705cd82c851d1299792fcc.png");
	static readonly Uri ERROR_URI = new Uri("resdb:///92a0b1cf9536b1e675e3e1c4db52133c5dc0596d128d7bb91582ce75bfb6a9da");

	const string MENU_ITEM_TAG = "RSE_POST_TO_DISCORD";

	private static readonly ConcurrentDictionary<string, Uri> _urlMappings = new();
	private static readonly ConcurrentDictionary<string, string> _misskeyFolderCache = new();

	private static readonly BlueskySessionManager bskyManager = new();

	enum ButtonState { Idle, Confirming, Uploading, Success, Warning, Error }

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

	static async Task<string> FormatPostBody(string template, PhotoMetadata photo, string? apiWorldName, string? apiWorldOwner, string? worldUrl) {
		if (string.IsNullOrWhiteSpace(template)) return "";

		var time = photo.TimeTaken.Value.Kind == DateTimeKind.Utc ? photo.TimeTaken.Value.ToLocalTime() : photo.TimeTaken.Value;

		string Fallback(string? val) => string.IsNullOrWhiteSpace(val) ? "Unknown" : val;

		string photographerName = await Util.GetUsernameFromUserId(photo.TakenBy._userId, photo.World);
		string hostName = await Util.GetUsernameFromUserId(photo.LocationHost._userId.Value, photo.World);
		string locationName = Fallback(RSE.PhotoMetadata_Patch.SanitizeText(photo.LocationName));

		string resolvedWorldName = !string.IsNullOrWhiteSpace(apiWorldName) ? apiWorldName : locationName;
		string resolvedWorldOwner = !string.IsNullOrWhiteSpace(apiWorldOwner) ? apiWorldOwner : Fallback(hostName);

		template = template
			.Replace("<Location>", locationName)
			.Replace("<Host>", Fallback(hostName))
			.Replace("<Photographer>", Fallback(photographerName))
			.Replace("<WorldName>", resolvedWorldName)
			.Replace("<WorldOwner>", resolvedWorldOwner)
			.Replace("<Time>", time.ToString("yyyy-MM-dd HH:mm"))
			.Replace("<AppVersion>", Fallback(photo.AppVersion.Value))
			.Replace("<RendererName>", Fallback(photo.RendererName.Value))
			.Replace("<CameraManufacturer>", Fallback(photo.CameraManufacturer.Value))
			.Replace("<CameraModel>", Fallback(photo.CameraModel.Value))
			.Replace("<CameraFOV>", photo.CameraFOV.Value.ToString("0.##"));

		template = System.Text.RegularExpressions.Regex.Replace(template, @"<WorldLink(?:\:(.*?))?>", match => {
			if (string.IsNullOrEmpty(worldUrl)) {
				return Config?.GetValue(UnpublishedWorldFallback) ?? "";
			}

			string linkText = match.Groups[1].Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value)
				? match.Groups[1].Value
				: resolvedWorldName;

			return $"[{linkText}]({worldUrl})";
		});

		return template;
	}

	static async Task<string> AltTextFromMetadata(PhotoMetadata photo) {
		Slot? extendedTagSlot = photo.Slot.FindChild("PhotoMetadata_Tags");

		string photographerName = await Util.GetUsernameFromUserId(photo.TakenBy._userId, photo.World);

		var (article, lensType, standardType, selfieType) = photo.CameraFOV.Value switch {
			>= 179f => ("A", "360°", "capture ", "capture"),
			> 85f => ("An", "ultra-wide ", "photo", "selfie"),
			> 65f => ("A", "wide-angle ", "photo", "selfie"),
			< 40f => ("A", "telephoto ", "shot", "portrait"),
			_ => ("A", "", "photo", "selfie")
		};

		float fovRad = photo.CameraFOV.Value * (MathF.PI / 180f);
		float tanHalfFov = MathF.Tan(fovRad / 2f);

		string altText = $"{article} {lensType}{standardType} from Resonite, taken by {photographerName} in {RSE.PhotoMetadata_Patch.SanitizeText(photo.LocationName)}.";
		
		if (extendedTagSlot != null) {
			DynamicVariableSpace extendedTagSpace = extendedTagSlot.FindSpace("PhotoMetadata");
			if (extendedTagSpace != null) {

				List<(string uid, string username, float screenX, float distance)> usersData = new();
				bool isSelfie = false;

				Msg("Processing extended tags...");
				foreach (AssetMetadata.UserInfo userInfo in photo.UserInfos) {
					string uid = userInfo.User._userId.Value;
					bool isInView = true; // we would rather tag people if FixPhotoMetadata isnt installed
					float headScale = 1.0f;

					if (extendedTagSlot != null && extendedTagSpace != null) {
						bool viewTagFound = extendedTagSpace.TryReadValue<bool>($"{uid}.isInView", out isInView);
						if (!viewTagFound) {
							// user might be using older version of FixPhotoMetadata... (https://github.com/BlueberryWolf/FixPhotoMetadata/issues/1)
							// work around by scanning components directly instead :')
							Slot? userTagSlot = extendedTagSlot.FindChild(uid);

							if (userTagSlot != null) {
								DynamicValueVariable<bool>? isInViewComponent = userTagSlot.GetComponents<DynamicValueVariable<bool>>().FirstOrDefault(v => v.VariableName.Value.Contains("isInView"));
								if (isInViewComponent != null) {
									isInView = isInViewComponent.Value.Value;
								}
							}
						}

						bool scaleTagFound = extendedTagSpace.TryReadValue<float>($"{uid}.headScale", out headScale);
						if (!scaleTagFound) {
							// user might be using older version of FixPhotoMetadata... (https://github.com/BlueberryWolf/FixPhotoMetadata/issues/1)
							// work around by scanning components directly instead :')
							Slot? userTagSlot = extendedTagSlot.FindChild(uid);

							if (userTagSlot != null) {
								DynamicValueVariable<float>? headScaleComponent = userTagSlot.GetComponents<DynamicValueVariable<float>>().FirstOrDefault(v => v.VariableName.Value.Contains("headScale"));
								if (headScaleComponent != null) {
									headScale = headScaleComponent.Value.Value;
								}
							}
						}
					}

					float3 userPos = userInfo.HeadPosition;
					float3 toUser = userPos - photo.TakenGlobalPosition.Value;
					floatQ inverseCamRot = photo.TakenGlobalRotation.Value.Inverted;
					float3 localPos = inverseCamRot * toUser;

					// discard users who are definitely not visible in the photo
					if (localPos.z > 0.05f) {
						// assuming heads are at most .3 meters
						float actualHeadSize = 0.3f * headScale;

						float viewHeightAtDepth = 2f * localPos.z * MathF.Tan(fovRad / 2f);
						float screenCoverage = actualHeadSize / viewHeightAtDepth;

						if (screenCoverage < 0.015f) {
							isInView = false;
						}
					}

					if (isInView) {
						string username = await Util.GetUsernameFromUserId(uid, photo.World);

						if (uid == photo.TakenBy._userId.Value) {
							isSelfie = true;
						}

						float tanTheta = localPos.x / localPos.z;
						float screenX = tanTheta / tanHalfFov;

						usersData.Add((uid, username, screenX, localPos.z));
					}
				}

				string JoinSegments(List<string> segs) {
					if (segs.Count == 0) return "";
					if (segs.Count == 1) return segs[0];
					if (segs.Count == 2) return $"{segs[0]} and {segs[1]}";
					return $"{string.Join(", ", segs.Take(segs.Count - 1))}, and {segs.Last()}";
				}

				string FormatPositions(List<(string uid, string username, float screenX, float distance)> users) {
					var left = users.Where(u => u.screenX < -0.3f).Select(u => u.username).ToList();
					var center = users.Where(u => u.screenX >= -0.3f && u.screenX <= 0.3f).Select(u => u.username).ToList();
					var right = users.Where(u => u.screenX > 0.3f).Select(u => u.username).ToList();

					List<string> segments = new List<string>();
					if (left.Count > 0) segments.Add($"{JoinSegments(left)} on the left");
					if (center.Count > 0) segments.Add($"{JoinSegments(center)} in the center");
					if (right.Count > 0) segments.Add($"{JoinSegments(right)} on the right");

					return JoinSegments(segments);
				}

				// if it's a solo selfie then don't bother mentioning where they are
				if (usersData.Count == 1 && usersData[0].uid == photo.TakenBy._userId) {
					usersData.Clear();
				}

				int totalUsers = usersData.Count;
				string crowdText = totalUsers >= 5 ? $". {totalUsers} users are visible in the photo" : "";
				string baseText = $"{article} {lensType}{(isSelfie ? selfieType : standardType)} from Resonite, taken by {photographerName} in {RSE.PhotoMetadata_Patch.SanitizeText(photo.LocationName)}{crowdText}.";

				float depthThreshold = photo.CameraFOV.Value switch {
					>= 179f => 2.0f,
					> 85f => 2.5f,
					> 65f => 3.0f,
					< 40f => 6.0f,
					_ => 4.0f
				};

				var foregroundUsers = usersData.Where(u => u.distance < depthThreshold).ToList();
				var backgroundUsers = usersData.Where(u => u.distance >= depthThreshold).ToList();

				string subjectText = "";
				float togetherThreshold = 1f;
				string shotWord = isSelfie ? "shot" : "photo";

				int fgLimit = 6;
				int totalLimit = 10;

				if (totalUsers == 0) {
					subjectText = "";
				} else if (foregroundUsers.Count > fgLimit) {
					// theres absolutely too many people in this photo for alt text
					subjectText = $"That's too many to list everyone individually.";
				} else if (totalUsers > totalLimit) {
					// list out foreground but not background because of crowding
					if (foregroundUsers.Count > 0) {
						bool fgTogether = foregroundUsers.Count == 2 && Math.Abs(foregroundUsers[0].screenX - foregroundUsers[1].screenX) < togetherThreshold;
						string fgText = fgTogether
							? $"{foregroundUsers[0].username} and {foregroundUsers[1].username} together"
							: FormatPositions(foregroundUsers);

						subjectText = $" It features {fgText}. In the background, there's a crowd of {backgroundUsers.Count} other users.";
					} else {
						subjectText = $" In the distance, there's a crowd of {backgroundUsers.Count} users.";
					}
				} else if (foregroundUsers.Count > 0 && backgroundUsers.Count == 0) {
					// only foreground
					bool fgTogether = foregroundUsers.Count == 2 && Math.Abs(foregroundUsers[0].screenX - foregroundUsers[1].screenX) < togetherThreshold;
					subjectText = fgTogether
						? $" {foregroundUsers[0].username} and {foregroundUsers[1].username} are together in the {shotWord}."
						: $" It features {FormatPositions(foregroundUsers)}.";
				} else if (foregroundUsers.Count == 0 && backgroundUsers.Count > 0) {
					// only background
					bool bgTogether = backgroundUsers.Count == 2 && Math.Abs(backgroundUsers[0].screenX - backgroundUsers[1].screenX) < togetherThreshold;
					subjectText = bgTogether
						? $" You can see {backgroundUsers[0].username} and {backgroundUsers[1].username} in the distance together."
						: $" In the distance, you can see {FormatPositions(backgroundUsers)}.";
				} else {
					// mixed depth
					bool fgTogether = foregroundUsers.Count == 2 && Math.Abs(foregroundUsers[0].screenX - foregroundUsers[1].screenX) < togetherThreshold;
					string fgText = fgTogether
						? $"{foregroundUsers[0].username} and {foregroundUsers[1].username} together"
						: FormatPositions(foregroundUsers);

					bool bgTogether = backgroundUsers.Count == 2 && Math.Abs(backgroundUsers[0].screenX - backgroundUsers[1].screenX) < togetherThreshold;
					string bgText = bgTogether
						? $"{backgroundUsers[0].username} and {backgroundUsers[1].username} together"
						: FormatPositions(backgroundUsers);

					subjectText = $" It features {fgText}. In the background, you can spot {bgText}.";
				}

				altText = $"{baseText}{subjectText}";
			}
		}

		return altText;
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

			if (!Config!.GetValue(FetchMetadataFromWeb)) continue;

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

		(string worldName, string worldOwner, Uri? worldUri) = await Util.GetWorldDetailsFromMetadataAsync(photo);
		string? worldUrl = worldUri?.OriginalString;

		string altText = await AltTextFromMetadata(photo);
		string postBodyText = await FormatPostBody(Config!.GetValue(MisskeyPostBody) ?? "", photo, worldName, worldOwner, worldUrl);

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
			Warn($"Misskey upload failed: {ex.Message}");

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
		Slot? extendedTagSlot = photo.Slot.FindChild("PhotoMetadata_Tags");
		DynamicVariableSpace? extendedTagSpace = extendedTagSlot?.FindSpace("PhotoMetadata");

		foreach (AssetMetadata.UserInfo userInfo in photo.UserInfos) {
			string? uid = userInfo.User._userId.Value;
			if (uid is null) continue;

			bool isInView = false;
			float headScale = 1.0f;

			if (extendedTagSlot != null && extendedTagSpace != null) {
				bool viewTagFound = extendedTagSpace.TryReadValue<bool>($"{uid}.isInView", out isInView);
				if (!viewTagFound) {
					// user might be using older version of FixPhotoMetadata... (https://github.com/BlueberryWolf/FixPhotoMetadata/issues/1)
					// work around by scanning components directly instead :')
					Slot? userTagSlot = extendedTagSlot.FindChild(uid);

					if (userTagSlot != null) {
						DynamicValueVariable<bool>? isInViewComponent = userTagSlot.GetComponents<DynamicValueVariable<bool>>().FirstOrDefault(v => v.VariableName.Value.Contains("isInView"));
						if (isInViewComponent != null) {
							isInView = isInViewComponent.Value.Value;
						}
					}
				}

				bool scaleTagFound = extendedTagSpace.TryReadValue<float>($"{uid}.headScale", out headScale);
				if (!scaleTagFound) {
					// user might be using older version of FixPhotoMetadata... (https://github.com/BlueberryWolf/FixPhotoMetadata/issues/1)
					// work around by scanning components directly instead :')
					Slot? userTagSlot = extendedTagSlot.FindChild(uid);

					if (userTagSlot != null) {
						DynamicValueVariable<float>? headScaleComponent = userTagSlot.GetComponents<DynamicValueVariable<float>>().FirstOrDefault(v => v.VariableName.Value.Contains("headScale"));
						if (headScaleComponent != null) {
							headScale = headScaleComponent.Value.Value;
						}
					}
				}
			}

			galleVRPlayers.Add(new GalleVRClient.Player {
				Id = uid,
				Name = await Util.GetUsernameFromUserId(uid, photo.World),
				HeadPosition = GalleVRClient.Player.PackPosition(userInfo.HeadPosition.Value, headScale, isInView),
				HeadOrientation = GalleVRClient.Player.PackOrientation(userInfo.HeadOrientation.Value)
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
				CameraManufacturer = photo.CameraManufacturer.Value ?? "Resonite",
				Application = "Resonite",
				IsNonVrcx = false,
				CameraFov = photo.CameraFOV.Value,
				TakenGlobalPosition = GalleVRClient.SpatialData.FromFloat3(photo.TakenGlobalPosition.Value),
				TakenGlobalRotation = GalleVRClient.SpatialData.FromFloatQ(photo.TakenGlobalRotation.Value),
				TakenGlobalScale = GalleVRClient.SpatialData.FromFloat3(photo.TakenGlobalScale.Value),
				World = new GalleVRClient.WorldMetadata() {
					Id = photo.LocationURL.Value?.OriginalString,
					Name = photo.LocationName.Value
				},
				Players = galleVRPlayers
			};

			string? photoUrl = await GalleVRClient.UploadFileAsync(photo.TakenBy._userId.Value, token, tmpPath, formattedMetadata, photo);
			// GalleVR returns the photo link (yay!) but going to it just redirects to an empty profile (bruh)
			Msg($"Upload finished!");
			return photoUrl;

		} catch (Exception ex) {
			Warn($"GalleVR upload failed: {ex.Message}");

			NotificationMessage.SpawnTextMessage("[RSEPostExtensions] Failed to post to GalleVR!", colorX.Red, 0.7f, 5f);

			return null;
		}
	}

	static void GenerateBlueskyButtons(ContextMenu menu, PhotoMetadata metadata) {
		string? identitiesStringList = Config!.GetValue(BlueskyLogins) is string s && !string.IsNullOrWhiteSpace(s) ? s : null;
		if (identitiesStringList == null) {
			return;
		}
		string[] identities = identitiesStringList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		foreach (string identity in identities) {
			string[] identityParts = identity.Split('?');
			string handle = identityParts[0];
			if (identityParts.Length <= 1) {
				menu.AddItem($"Missing app password for {handle}", ERROR_URI, null);
				continue;
			}
			string password = identityParts[1];
			string? currentWarning = null;


			colorX currentColor = colorX.FromHexCode("1185fe");
			ContextMenuItem menuItem = menu.AddItem($"@{handle}", BLUESKY_URI, currentColor);
			ButtonState currentState = ButtonState.Idle;

			void UpdateButtonVisuals() {
				if (menuItem == null || menuItem.IsRemoved) return;
				menuItem.Button.Enabled = currentState != ButtonState.Uploading;

				switch (currentState) {
					case ButtonState.Idle:
						menuItem.Label.Target.Value = "@{handle}";
						menuItem.Color.Value = currentColor;
						break;
					case ButtonState.Confirming:
						menuItem.Label.Target.Value = $"<color=hero.red>Really post to </color><b>@{handle}?</b>";
						menuItem.Color.Value = currentColor;
						break;
					case ButtonState.Warning:
						menuItem.Label.Target.Value = $"{currentWarning ?? "Something malicious is brewing..."}\n<color=hero.red>Continue posting to </color><b>@{handle}?</b>";
						menuItem.Color.Value = RadiantUI_Constants.Hero.ORANGE;
						break;
					case ButtonState.Uploading:
						menuItem.Label.Target.Value = "Posting...";
						menuItem.Color.Value = currentColor;
						break;
					case ButtonState.Success:
						menuItem.Label.Target.Value = $"Posted!\n<size=60%><color=#bcbcbc>Click to view post</color></size>";
						menuItem.Color.Value = RadiantUI_Constants.Hero.GREEN;
						break;
					case ButtonState.Error:
						menuItem.Label.Target.Value = $"<color=hero.red>Post Failed</color>\n<size=60%><color=#bcbcbc>Click to try again</color></size>";
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

				bool postAnyway = currentState == ButtonState.Warning;
				currentState = ButtonState.Uploading;
				UpdateButtonVisuals();

				(Uri? postUrl, currentWarning) = await PostToBlueskyAsync(metadata, handle, password, postAnyway);

				if (menuItem == null || menuItem.IsRemoved) return;

				if (postUrl != null) {
					menuItem.RunSynchronously(() => {
						Hyperlink linkComponent = menuItem.Slot.AttachComponent<Hyperlink>();
						linkComponent.URL.Value = postUrl;
						linkComponent.Reason.Value = $"View your Bluesky post";
						currentState = ButtonState.Success;
					});
				} else {
					if (currentWarning != null) {
						currentState = ButtonState.Warning;
					} else {
						currentState = ButtonState.Error;
					}
				}
				menuItem.RunSynchronously(() => {
					UpdateButtonVisuals();
				});
			};
		}
	}

	static async Task<(Uri? uri, string? warning)> PostToBlueskyAsync(PhotoMetadata photo, string handle, string password, bool forceTruncate = false) {
		if (!Config!.GetValue(Multiposting)) {
			photo.LocalUser.CloseContextMenu(null!);
		}
		Msg("Posting to Bluesky...");

		try {
			if (string.IsNullOrWhiteSpace(handle) || string.IsNullOrWhiteSpace(password)) {
				NotificationMessage.SpawnTextMessage("[RSEPostExtensions] No app password specified!", colorX.Red, 0.7f, 5f);
				return (null, null);
			}

			await bskyManager.LoginAsync(handle, password);
		} catch (Exception ex) {
			Warn($"Failed to login to Bluesky: {ex.Message}");
			NotificationMessage.SpawnTextMessage("[RSEPostExtensions] Bluesky Login Failed!", colorX.Red, 0.7f, 5f);
			return (null, null);
		}

		var tex = photo.Slot.GetComponent<StaticTexture2D>();
		var url = tex?.URL.Value;
		if (url is null) return (null, null);

		await new ToBackground();

		string altText = await AltTextFromMetadata(photo);

		(string worldName, string worldOwner, Uri? worldUri) = await Util.GetWorldDetailsFromMetadataAsync(photo);
		string? worldUrl = worldUri?.OriginalString;

		string postBodyText = await FormatPostBody(Config!.GetValue(BlueskyPostBody) ?? "", photo, worldName, worldOwner, worldUrl);

		int charCount = new System.Globalization.StringInfo(postBodyText).LengthInTextElements;

		if (charCount > 300) {
			if (forceTruncate) {
				Msg($"Posting a truncated variant to bluesky!");

				var stringInfo = new System.Globalization.StringInfo(postBodyText);
				postBodyText = stringInfo.SubstringByTextElements(0, 299) + "…";
			} else {
				Warn($"Bluesky post body is {charCount}/300 characters! Cancelling!");
				return (null, $"<color=hero.orange>Post is over character limit and will be truncated.</color> (<color=hero.red>{charCount}</color>/300)");
			}
		}

		try {
			string? tmpPath = await photo.Engine.AssetManager.GatherAssetFile(url, 100f).AsTask();

			if (tmpPath is null) return (null, null);

			string cardTitle = !string.IsNullOrWhiteSpace(worldName) ? RSE.PhotoMetadata_Patch.SanitizeText(worldName) : "Resonite World";
			string cardDesc = $"Made by {worldOwner} on Resonite.";

			bool useLinkCard = Config!.GetValue(BlueskyLinkCard);

			Uri postUri = await bskyManager.UploadAndPostPhotoAsync(handle, tmpPath, postBodyText, altText, worldUrl, cardTitle, cardDesc, useLinkCard);

			Msg($"Bluesky upload finished!");
			return (postUri, null);

		} catch (Exception ex) {
			Warn($"Bluesky upload failed: {ex.Message}");
			string notifMsg = ex is InvalidOperationException
				? $"[RSEPostExtensions] {ex.Message}"
				: "[RSEPostExtensions] Failed to post to Bluesky!";
			NotificationMessage.SpawnTextMessage(notifMsg, colorX.Red, 0.7f, 5f);
			return (null, null);
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
					GenerateBlueskyButtons(newMenu, hookedInstance);
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
