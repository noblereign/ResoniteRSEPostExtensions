using System.Net.Http.Headers;
using FrooxEngine;
using ResoniteModLoader;
using static ResoniteScreenshotExtensions.DiscordWebhookClient;
using RSE = ResoniteScreenshotExtensions.ResoniteScreenshotExtensions;

namespace RSEPostExtensions;


public class DiscordClient {
	// referenced from https://github.com/hantabaru1014/ResoniteScreenshotExtensions/blob/main/Patches/PhotoMetadataPatch.cs
	private static readonly HttpClient _client = new();

	public static async void PostVideoToDiscord(VideoMetadata metadata, string filePath, Uri webhookUrl) {
		ModConfiguration? RSEConfig = ModLoader.Mods().FirstOrDefault(m => m.Name == "ResoniteScreenshotExtensions")?.GetConfiguration();
		if (RSEConfig == null) return;
		ModConfigurationKey? DiscordWebhookEmbedLocationNameKey = RSEConfig?.ConfigurationItemDefinitions.FirstOrDefault(m => m.Name == "DiscordWebhookEmbedLocationName");
		ModConfigurationKey? DiscordWebhookEmbedLocationHostKey = RSEConfig?.ConfigurationItemDefinitions.FirstOrDefault(m => m.Name == "DiscordWebhookEmbedLocationHost");
		ModConfigurationKey? DiscordWebhookEmbedUsersKey = RSEConfig?.ConfigurationItemDefinitions.FirstOrDefault(m => m.Name == "DiscordWebhookEmbedUsers");
		if (DiscordWebhookEmbedLocationHostKey == null) return;
		if (DiscordWebhookEmbedLocationNameKey == null) return;
		if (DiscordWebhookEmbedUsersKey == null) return;
		

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
		

		var json = $"{{\"embeds\": [{{\"fields\": [{fields.Select(f => f.ToJson()).Aggregate((acc, curr) => $"{acc}, {curr}")}]}}]}}";

		_ = SendVideo(filePath, webhookUrl, json);
	}

	private static async Task<bool> SendVideo(string srcPath, Uri webhookUrl, string? payloadJson = null) {
		using (FileStream fileStream = File.OpenRead(srcPath)) {
			FileInfo fileType = new(srcPath);
			fileStream.Seek(0, SeekOrigin.Begin);
			var fileContent = new StreamContent(fileStream);
			fileContent.Headers.ContentType = new MediaTypeHeaderValue(Util.GuessVideoMimeType(fileStream));
			fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") {
				FileName = $"video.{fileType.Extension}",
			};

			var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl);
			var form = new MultipartFormDataContent {
				{ fileContent, "file" }
			};
			if (payloadJson != null) {
				form.Add(new StringContent(payloadJson), "payload_json");
			}
			request.Content = form;

			var response = await _client.SendAsync(request);
			return response.IsSuccessStatusCode;
		}
	}
}
