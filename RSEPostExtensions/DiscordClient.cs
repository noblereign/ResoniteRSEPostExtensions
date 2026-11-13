using System;
using System.Collections.Generic;
using System.Text;

using FrooxEngine;
using ResoniteModLoader;
using ResoniteScreenshotExtensions;
using static ResoniteScreenshotExtensions.DiscordWebhookClient;
using RSE = ResoniteScreenshotExtensions.ResoniteScreenshotExtensions;

namespace RSEPostExtensions;


public class DiscordClient {
	// referenced from https://github.com/hantabaru1014/ResoniteScreenshotExtensions/blob/main/Patches/PhotoMetadataPatch.cs
	private static void PostVideoToDiscord(VideoMetadata metadata, string filePath, Uri webhookUrl) {
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
			fields.Add(new EmbedField("LocationName", RSE.PhotoMetadata_Patch.SanitizeText(metadata.LocationName)));
		}
		if ((bool)RSEConfig!.GetValue(DiscordWebhookEmbedLocationHostKey)) {
			fields.Add(new EmbedField("LocationHost", RSE.PhotoMetadata_Patch.SanitizeText(metadata.LocationHost.Name ?? metadata.LocationHost._userId)));
		}
		fields.Add(new EmbedField("TakenBy", RSE.PhotoMetadata_Patch.SanitizeText(metadata.TakenBy.Name ?? metadata.TakenBy._userId)));
		var unixTimestamp = (int)metadata.TimeTaken.Value.ToUniversalTime().Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
		fields.Add(new EmbedField("TimeTaken", $"<t:{unixTimestamp}>"));
		if ((bool)RSEConfig!.GetValue(DiscordWebhookEmbedUsersKey)) {
			fields.Add(new EmbedField("Users", metadata.UserInfos.Select(u => RSE.PhotoMetadata_Patch.SanitizeText(u.User.Name ?? u.User._userId)).Aggregate((acc, curr) => $"{acc}, {curr}")));
		}

		var client = new DiscordWebhookClient(webhookUrl.OriginalString);
		try {
			_ = client.SendImageWithMetadata(filePath, fields).ContinueWith(t =>
			{
				if (t.IsFaulted) {
					RSEPostExtensions.Error("Failed to send video to Discord: " + t.Exception);
				} else {
					if (t.Result) {
						RSEPostExtensions.Msg("Video sent to Discord successfully.");
					} else {
						RSEPostExtensions.Error("Failed to send video to Discord");
					}
				}
			});
		} catch (Exception ex) {
			RSEPostExtensions.Error(ex);
		}
	}
}
