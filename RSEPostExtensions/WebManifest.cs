using System.Text.Json.Serialization;
public class WebManifest {
	[JsonPropertyName("short_name")]
	public string? ShortName { get; set; }

	[JsonPropertyName("name")]
	public required string Name { get; set; }

	[JsonPropertyName("start_url")]
	public required string StartUrl { get; set; }

	[JsonPropertyName("display")]
	public string? Display { get; set; }

	[JsonPropertyName("background_color")]
	public string? BackgroundColor { get; set; }

	[JsonPropertyName("theme_color")]
	public string? ThemeColor { get; set; }

	[JsonPropertyName("icons")]
	public List<ManifestIcon>? Icons { get; set; }

	[JsonPropertyName("share_target")]
	public ShareTarget? ShareTarget { get; set; }
}
public class ManifestIcon {
	[JsonPropertyName("src")]
	public required string Src { get; set; }

	[JsonPropertyName("sizes")]
	public string? Sizes { get; set; }

	[JsonPropertyName("type")]
	public string? Type { get; set; }

	[JsonPropertyName("purpose")]
	public string? Purpose { get; set; }
}
public class ShareTarget {
	[JsonPropertyName("action")]
	public required string Action { get; set; }

	[JsonPropertyName("method")]
	public string? Method { get; set; }

	[JsonPropertyName("enctype")]
	public string? Enctype { get; set; }

	[JsonPropertyName("params")]
	public ShareTargetParams? Params { get; set; }
}
public class ShareTargetParams {
	[JsonPropertyName("title")]
	public string? Title { get; set; }

	[JsonPropertyName("text")]
	public string? Text { get; set; }

	[JsonPropertyName("url")]
	public string? Url { get; set; }
}
