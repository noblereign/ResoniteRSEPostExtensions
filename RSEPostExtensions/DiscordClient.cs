using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Elements.Core;

using FrooxEngine;

using Newtonsoft.Json;

using ResoniteModLoader;

using static ResoniteScreenshotExtensions.DiscordWebhookClient;
using static RSEPostExtensions.RSEPostExtensions;
using static RSEPostExtensions.Util;

using RSE = ResoniteScreenshotExtensions.ResoniteScreenshotExtensions;
using StringContent = System.Net.Http.StringContent;


namespace RSEPostExtensions;

public class DiscordClient {
	// referenced from https://github.com/hantabaru1014/ResoniteScreenshotExtensions/blob/main/Patches/PhotoMetadataPatch.cs
	private static readonly HttpClient _client = new();
	private static readonly ConcurrentDictionary<string, string> _webhookGuildCache = new();

	private static Uri AppendWaitQuery(Uri webhookUrl) {
		var builder = new UriBuilder(webhookUrl);
		string existingQuery = builder.Query.TrimStart('?');
		if (string.IsNullOrEmpty(existingQuery)) {
			builder.Query = "wait=true";
		} else if (!existingQuery.Split('&').Any(p => p.StartsWith("wait=", StringComparison.OrdinalIgnoreCase))) {
			builder.Query = $"{existingQuery}&wait=true";
		}
		return builder.Uri;
	}

	private static async Task<Uri?> BuildDiscordMessageUrlAsync(
		string responseBody,
		Uri webhookUrl,
		HttpClient client,
		CancellationToken ct = default) {
		using var messageDoc = System.Text.Json.JsonDocument.Parse(responseBody);
		var root = messageDoc.RootElement;

		string? messageId = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
		string? channelId = root.TryGetProperty("channel_id", out var chEl) ? chEl.GetString() : null;
		if (string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(channelId))
			return null;

		string? guildId = root.TryGetProperty("guild_id", out var gEl) ? gEl.GetString() : null;
		if (string.IsNullOrEmpty(guildId)) {
			string baseWebhookUrl = webhookUrl.GetLeftPart(UriPartial.Path);
			if (!_webhookGuildCache.TryGetValue(baseWebhookUrl, out guildId)) {
				using HttpResponseMessage infoRes = await client.GetAsync(baseWebhookUrl, ct).ConfigureAwait(false);
				if (infoRes.IsSuccessStatusCode) {
					string infoJson = await infoRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
					using var infoDoc = System.Text.Json.JsonDocument.Parse(infoJson);
					if (infoDoc.RootElement.TryGetProperty("guild_id", out var webhookGuildEl)) {
						guildId = webhookGuildEl.GetString();
						if (!string.IsNullOrEmpty(guildId)) {
							_webhookGuildCache[baseWebhookUrl] = guildId;
						}
					}
				}
			}
		}

		string guildSegment = string.IsNullOrEmpty(guildId) ? "@me" : guildId;
		return new Uri($"https://discord.com/channels/{guildSegment}/{channelId}/{messageId}");
	}

	public static async Task<Uri?> PostVideoToDiscord(VideoMetadata metadata, string filePath, string altText, Uri webhookUrl, IProgress<CompressionProgress>? progress = null) {
		progress?.Report(new CompressionProgress(1.0, "Gathering metadata"));
		ModConfiguration? RSEConfig = ModLoader.Mods().FirstOrDefault(m => m.Name == "ResoniteScreenshotExtensions")?.GetConfiguration();
		if (RSEConfig == null) return null;
		ModConfigurationKey? DiscordWebhookEmbedLocationNameKey = RSEConfig?.ConfigurationItemDefinitions.FirstOrDefault(m => m.Name == "DiscordWebhookEmbedLocationName");
		ModConfigurationKey? DiscordWebhookEmbedLocationHostKey = RSEConfig?.ConfigurationItemDefinitions.FirstOrDefault(m => m.Name == "DiscordWebhookEmbedLocationHost");
		ModConfigurationKey? DiscordWebhookEmbedUsersKey = RSEConfig?.ConfigurationItemDefinitions.FirstOrDefault(m => m.Name == "DiscordWebhookEmbedUsers");
		if (DiscordWebhookEmbedLocationHostKey == null) return null;
		if (DiscordWebhookEmbedLocationNameKey == null) return null;
		if (DiscordWebhookEmbedUsersKey == null) return null;

		var fields = new List<EmbedField>();
		if ((bool)RSEConfig!.GetValue(DiscordWebhookEmbedLocationNameKey)) {
			fields.Add(new EmbedField("LocationName", RSE.PhotoMetadata_Patch.SanitizeText(metadata.LocationName.Value)));
		}
		if ((bool)RSEConfig!.GetValue(DiscordWebhookEmbedLocationHostKey)) {
			fields.Add(new EmbedField("LocationHost", RSE.PhotoMetadata_Patch.SanitizeText(await Util.GetUsernameFromUserId(metadata.LocationHost._userId.Value, metadata.World))));
		}
		fields.Add(new EmbedField("TakenBy", RSE.PhotoMetadata_Patch.SanitizeText(await Util.GetUsernameFromUserId(metadata.TakenBy._userId.Value, metadata.World))));
		var unixTimestamp = (int)metadata.TimeTaken.Value.ToUniversalTime().Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
		fields.Add(new EmbedField("TimeTaken", $"<t:{unixTimestamp}>"));
		if ((bool)RSEConfig!.GetValue(DiscordWebhookEmbedUsersKey)) {
			IEnumerable<Task<string>> userTasks = metadata.UserInfos.Select(async u =>
				RSE.PhotoMetadata_Patch.SanitizeText(await Util.GetUsernameFromUserId(u.User._userId, metadata.World))
			);

			string[] usernames = await Task.WhenAll(userTasks);

			fields.Add(new EmbedField("Users", string.Join(", ", usernames)));
		}

		if (altText.Length > 1024) altText = string.Concat(altText.AsSpan(0, 1021), "...");

		string escapedAltText = JsonConvert.ToString(altText);
		string fieldsJson = string.Join(", ", fields.Select(f => f.ToJson()));

		(string compressedVideoPath, bool autoDelete, (int, int) resolution) = await Util.CompressVideoAsync(filePath, 19, 96, true, progress);

		progress?.Report(new CompressionProgress(1.0, "Reading compressed file..."));

		try {
			using (FileStream fileStream = File.OpenRead(compressedVideoPath)) {
				(string mimeType, string extension) = Util.GuessVideoMimeType(fileStream);
				string fileName = $"video{extension}";

				string json = $"{{\"embeds\": [{{\"fields\": [{fieldsJson}]}}], \"attachments\": [{{\"id\": 0, \"filename\": \"{fileName}\", \"description\": {escapedAltText}}}]}}";

				return await SendVideo(fileStream, mimeType, extension, webhookUrl, json, progress);
			}
		} finally {
			if (autoDelete && File.Exists(compressedVideoPath)) {
				try { File.Delete(compressedVideoPath); } catch { }
			}
		}
	}

	private static async Task<Uri?> SendVideo(FileStream fileStream, string mimeType, string extension, Uri webhookUrl, string payloadJson, IProgress<CompressionProgress>? progress = null, CancellationToken ct = default) {
		progress?.Report(new CompressionProgress(0.0, "Uploading..."));

		using var form = new MultipartFormDataContent();

		var payloadContent = new StringContent(payloadJson, Encoding.UTF8, "application/json");
		form.Add(payloadContent, "payload_json");

		var fileContent = new ProgressiveStreamContent(fileStream, (sent, total) => {
			if (progress == null || total <= 0)
				return;

			if (sent >= total) {
				progress.Report(new CompressionProgress(1.0, "Validating..."));
				return;
			}

			double ratio = Math.Clamp((double)sent / total, 0.0, 0.99);
			progress.Report(new CompressionProgress(ratio, "Uploading..."));
		});
		fileContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
		form.Add(fileContent, "files[0]", $"video{extension}");

		Uri postUri = AppendWaitQuery(webhookUrl);
		using HttpResponseMessage response = await _client.PostAsync(postUri, form, ct).ConfigureAwait(false);
		string responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

		if (!response.IsSuccessStatusCode) {
			RSEPostExtensions.Warn($"Failed to post video to Discord: {response.StatusCode} {responseBody}");
			return null;
		}

		return await BuildDiscordMessageUrlAsync(responseBody, webhookUrl, _client, ct).ConfigureAwait(false);
	}

	public static async Task<Uri?> PostToDiscordWithAltText(
		string payloadJson,
		FileStream fileStream,
		string mimeType,
		string extension,
		string webhookUrl,
		HttpClient client,
		IProgress<CompressionProgress>? progress = null,
		CancellationToken ct = default) {
		fileStream.Seek(0, SeekOrigin.Begin);
		progress?.Report(new CompressionProgress(0.0, "Uploading..."));

		var fileContent = new ProgressiveStreamContent(fileStream, (sent, total) => {
			if (progress == null || total <= 0)
				return;

			if (sent >= total) {
				progress.Report(new CompressionProgress(1.0, "Validating..."));
				return;
			}

			double ratio = Math.Clamp((double)sent / total, 0.0, 0.99);
			progress.Report(new CompressionProgress(ratio, "Uploading..."));
		});
		fileContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
		fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") {
			Name = "files[0]",
			FileName = $"image{extension}",
		};

		Uri baseUri = new(webhookUrl);
		var request = new HttpRequestMessage(HttpMethod.Post, AppendWaitQuery(baseUri));
		var form = new MultipartFormDataContent {
		{ fileContent, "files[0]" },
		{ new System.Net.Http.StringContent(payloadJson), "payload_json" }
	};
		request.Content = form;

		using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
		string responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

		if (!response.IsSuccessStatusCode) {
			RSEPostExtensions.Warn($"Failed to post image to Discord: {response.StatusCode} {responseBody}");
			NotificationMessage.SpawnTextMessage("[RSEPostExtensions] Failed to post image to Discord, check log", RadiantUI_Constants.Hero.RED, 0.7f, 5f);
			return null;
		}

		return await BuildDiscordMessageUrlAsync(responseBody, baseUri, client, ct).ConfigureAwait(false);
	}
}
