using System.Net.Http.Headers;
using FrooxEngine;
using Newtonsoft.Json;

using ResoniteModLoader;
using static ResoniteScreenshotExtensions.DiscordWebhookClient;
using RSE = ResoniteScreenshotExtensions.ResoniteScreenshotExtensions;

namespace RSEPostExtensions;


public class DiscordClient {
	// referenced from https://github.com/hantabaru1014/ResoniteScreenshotExtensions/blob/main/Patches/PhotoMetadataPatch.cs
	private static readonly HttpClient _client = new();

	public static async Task<bool> PostVideoToDiscord(VideoMetadata metadata, string filePath, string altText, Uri webhookUrl) {
		ModConfiguration? RSEConfig = ModLoader.Mods().FirstOrDefault(m => m.Name == "ResoniteScreenshotExtensions")?.GetConfiguration();
		if (RSEConfig == null) return false;
		ModConfigurationKey? DiscordWebhookEmbedLocationNameKey = RSEConfig?.ConfigurationItemDefinitions.FirstOrDefault(m => m.Name == "DiscordWebhookEmbedLocationName");
		ModConfigurationKey? DiscordWebhookEmbedLocationHostKey = RSEConfig?.ConfigurationItemDefinitions.FirstOrDefault(m => m.Name == "DiscordWebhookEmbedLocationHost");
		ModConfigurationKey? DiscordWebhookEmbedUsersKey = RSEConfig?.ConfigurationItemDefinitions.FirstOrDefault(m => m.Name == "DiscordWebhookEmbedUsers");
		if (DiscordWebhookEmbedLocationHostKey == null) return false;
		if (DiscordWebhookEmbedLocationNameKey == null) return false;
		if (DiscordWebhookEmbedUsersKey == null) return false;
		

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

		(string compressedVideoPath, bool autoDelete, (int, int) resolution) = await Util.CompressVideoAsync(filePath);

		try {
			using (FileStream fileStream = File.OpenRead(compressedVideoPath)) {
				(string mimeType, string extension) = Util.GuessVideoMimeType(fileStream);
				string fileName = $"video{extension}";

				string json = $"{{\"embeds\": [{{\"fields\": [{fieldsJson}]}}], \"attachments\": [{{\"id\": 0, \"filename\": \"{fileName}\", \"description\": {escapedAltText}}}]}}";

				return await SendVideo(fileStream, mimeType, extension, webhookUrl, json);
			}
		} finally {
			if (autoDelete && File.Exists(compressedVideoPath))
			{
				try { File.Delete(compressedVideoPath); } catch { }
			}
		}
	}

	private static async Task<bool> SendVideo(FileStream fileStream, string mimeType, string extension, Uri webhookUrl, string? payloadJson = null) {
		fileStream.Seek(0, SeekOrigin.Begin);
		var fileContent = new StreamContent(fileStream);
		fileContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
		fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") {
			Name = "files[0]",
			FileName = $"video{extension}",
		};

		var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl);
		var form = new MultipartFormDataContent {
			{ fileContent, "files[0]" }
		};
		if (payloadJson != null) {
			form.Add(new StringContent(payloadJson), "payload_json");
		}
		request.Content = form;

		var response = await _client.SendAsync(request);
		if (!response.IsSuccessStatusCode) {
			RSEPostExtensions.Warn($"Failed to post video to Discord: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
		}
		return response.IsSuccessStatusCode;
	}
}
