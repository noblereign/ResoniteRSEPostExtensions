using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SixLabors.ImageSharp.Processing;

using static RSEPostExtensions.BlueskyClient;

namespace RSEPostExtensions;
public class BlueskyClient {
	private static readonly HttpClient _http = new();

	public record SessionRequest(
		[property: JsonPropertyName("identifier")] string Identifier,
		[property: JsonPropertyName("password")] string Password
	);

	public record SessionResponse(
		[property: JsonPropertyName("did")] string Did,
		[property: JsonPropertyName("handle")] string Handle,
		[property: JsonPropertyName("accessJwt")] string AccessJwt,
		[property: JsonPropertyName("refreshJwt")] string RefreshJwt
	);

	public record BlobResponse([property: JsonPropertyName("blob")] BlobData Blob);

	public record BlobData(
		[property: JsonPropertyName("$type")] string Type,
		[property: JsonPropertyName("ref")] BlobRefLink Ref,
		[property: JsonPropertyName("mimeType")] string MimeType,
		[property: JsonPropertyName("size")] long Size
	);

	public record BlobRefLink([property: JsonPropertyName("$link")] string Link);

	public record CreateRecordResponse(
		[property: JsonPropertyName("uri")] string Uri,
		[property: JsonPropertyName("cid")] string Cid
	);

	public static async Task<Uri> DiscoverPdsAsync(string identifier) {
		identifier = identifier.TrimStart('@');

		if (identifier.Contains('@')) {
			return new Uri("https://bsky.social/");
		}

		try {
			var resolveUrl = $"https://public.api.bsky.app/xrpc/com.atproto.identity.resolveHandle?handle={identifier}";
			var handleData = await _http.GetFromJsonAsync<JsonDocument>(resolveUrl);

			if (handleData == null || !handleData.RootElement.TryGetProperty("did", out var didProp))
				return new Uri("https://bsky.social/");

			string did = didProp.GetString()!;

			JsonDocument? didDoc = null;
			if (did.StartsWith("did:plc:")) {
				didDoc = await _http.GetFromJsonAsync<JsonDocument>($"https://plc.directory/{did}");
			} else if (did.StartsWith("did:web:")) {
				string domain = did.Substring(8);
				didDoc = await _http.GetFromJsonAsync<JsonDocument>($"https://{domain}/.well-known/did.json");
			}

			if (didDoc != null && didDoc.RootElement.TryGetProperty("service", out var services)) {
				foreach (var service in services.EnumerateArray()) {
					if (service.TryGetProperty("id", out var idVal) && idVal.GetString() == "#atproto_pds") {
						if (service.TryGetProperty("serviceEndpoint", out var endpoint)) {
							string endpointStr = endpoint.GetString()!;
							if (!endpointStr.EndsWith("/")) endpointStr += "/";
							return new Uri(endpointStr);
						}
					}
				}
			}
		} catch {
		}

		return new Uri("https://bsky.social/");
	}

	public static async Task<SessionResponse> CreateSessionAsync(Uri pdsUrl, string identifier, string password) {
		Uri authEndpoint = new Uri(pdsUrl, "xrpc/com.atproto.server.createSession");

		var payload = new SessionRequest(identifier, password);
		using var request = new HttpRequestMessage(HttpMethod.Post, authEndpoint);
		request.Content = JsonContent.Create(payload);

		using var response = await _http.SendAsync(request);
		await EnsureSuccessWithBodyAsync(response);

		return (await response.Content.ReadFromJsonAsync<SessionResponse>())!;
	}

	public static async Task<SessionResponse> RefreshSessionAsync(Uri pdsUrl, string refreshJwt) {
		Uri refreshEndpoint = new Uri(pdsUrl, "xrpc/com.atproto.server.refreshSession");

		using var request = new HttpRequestMessage(HttpMethod.Post, refreshEndpoint);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshJwt);

		using var response = await _http.SendAsync(request);
		await EnsureSuccessWithBodyAsync(response);

		return (await response.Content.ReadFromJsonAsync<SessionResponse>())!;
	}
	private static async Task<(byte[] Bytes, string MimeType)> EnsureImageSizeLimitAsync(string localFilePath, int maxBytes) {
		string mimeType;
		using (var fs = File.OpenRead(localFilePath)) {
			mimeType = Util.GuessImageMimeType(fs);
		}

		byte[] imageBytes = await File.ReadAllBytesAsync(localFilePath);

		if (imageBytes.Length <= maxBytes) return (imageBytes, mimeType);

		using var inStream = new MemoryStream(imageBytes);
		using var image = await SixLabors.ImageSharp.Image.LoadAsync(inStream);

		if (image.Width > 1920 || image.Height > 1920) {
			image.Mutate(x => x.Resize(new SixLabors.ImageSharp.Processing.ResizeOptions {
				Size = new SixLabors.ImageSharp.Size(1920, 1920),
				Mode = SixLabors.ImageSharp.Processing.ResizeMode.Max
			}));
		}

		using var ms = new MemoryStream();
		int quality = 90;

		do {
			ms.SetLength(0);
			var encoder = new SixLabors.ImageSharp.Formats.Webp.WebpEncoder {
				FileFormat = SixLabors.ImageSharp.Formats.Webp.WebpFileFormatType.Lossy,
				Quality = quality
			};

			await image.SaveAsync(ms, encoder);
			quality -= 15;
		} while (ms.Length > maxBytes && quality > 10);

		if (ms.Length > maxBytes) {
			throw new InvalidOperationException($"Image is too complex to compress below {maxBytes / 1000000.0:0.##}MB.");
		}

		return (ms.ToArray(), "image/webp");
	}

	public static async Task<BlobData> UploadBlobAsync(Uri pdsUrl, string token, string localFilePath, int maxBytes) {
		Uri uploadEndpoint = new Uri(pdsUrl, "xrpc/com.atproto.repo.uploadBlob");
		using var request = new HttpRequestMessage(HttpMethod.Post, uploadEndpoint);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

		var (fileBytes, mimeType) = await EnsureImageSizeLimitAsync(localFilePath, maxBytes);
		var content = new ByteArrayContent(fileBytes);
		content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
		request.Content = content;

		using var response = await _http.SendAsync(request);
		await EnsureSuccessWithBodyAsync(response);

		var result = await response.Content.ReadFromJsonAsync<BlobResponse>();
		return result!.Blob;
	}

	private static async Task<(string cleanText, List<Dictionary<string, object>> facets)> ParseFacetsAsync(string text) {
		var facets = new List<Dictionary<string, object>>();
		var sb = new System.Text.StringBuilder();
		int lastIndex = 0;

		var markdownRegex = new System.Text.RegularExpressions.Regex(@"\[(.*?)\]\((.*?)\)");
		foreach (System.Text.RegularExpressions.Match match in markdownRegex.Matches(text)) {
			sb.Append(text.Substring(lastIndex, match.Index - lastIndex));

			string linkText = match.Groups[1].Value;
			string linkUrl = match.Groups[2].Value;

			int byteStart = System.Text.Encoding.UTF8.GetByteCount(sb.ToString());
			int byteEnd = byteStart + System.Text.Encoding.UTF8.GetByteCount(linkText);

			facets.Add(new Dictionary<string, object> {
				{ "index", new Dictionary<string, object> { { "byteStart", byteStart }, { "byteEnd", byteEnd } } },
				{ "features", new[] {
					new Dictionary<string, object> {
						{ "$type", "app.bsky.richtext.facet#link" },
						{ "uri", linkUrl }
					}
				}}
			});

			sb.Append(linkText);
			lastIndex = match.Index + match.Length;
		}

		sb.Append(text.Substring(lastIndex));
		string cleanText = sb.ToString();

		var tagRegex = new System.Text.RegularExpressions.Regex(@"(?<=^|\s)(#[\p{L}\p{N}_]+)");
		var urlRegex = new System.Text.RegularExpressions.Regex(@"(?<=^|\s)(https?://[^\s<]+[^<.,;:!?\'\""\s])");
		var mentionRegex = new System.Text.RegularExpressions.Regex(@"(?<=^|\s)(@([a-zA-Z0-9-]+\.)+[a-zA-Z0-9-]+)");

		foreach (System.Text.RegularExpressions.Match match in tagRegex.Matches(cleanText)) {
			int byteStart = System.Text.Encoding.UTF8.GetByteCount(cleanText.Substring(0, match.Index));
			int byteEnd = byteStart + System.Text.Encoding.UTF8.GetByteCount(match.Value);

			facets.Add(new Dictionary<string, object> {
				{ "index", new Dictionary<string, object> { { "byteStart", byteStart }, { "byteEnd", byteEnd } } },
				{ "features", new[] {
					new Dictionary<string, object> {
						{ "$type", "app.bsky.richtext.facet#tag" },
						{ "tag", match.Value.TrimStart('#') }
					}
				}}
			});
		}

		foreach (System.Text.RegularExpressions.Match match in urlRegex.Matches(cleanText)) {
			int byteStart = System.Text.Encoding.UTF8.GetByteCount(cleanText.Substring(0, match.Index));
			int byteEnd = byteStart + System.Text.Encoding.UTF8.GetByteCount(match.Value);

			facets.Add(new Dictionary<string, object> {
				{ "index", new Dictionary<string, object> { { "byteStart", byteStart }, { "byteEnd", byteEnd } } },
				{ "features", new[] {
					new Dictionary<string, object> {
						{ "$type", "app.bsky.richtext.facet#link" },
						{ "uri", match.Value }
					}
				}}
			});
		}

		foreach (System.Text.RegularExpressions.Match match in mentionRegex.Matches(cleanText)) {
			string handle = match.Value.TrimStart('@');
			string? did = null;

			try {
				var resolveUrl = $"https://public.api.bsky.app/xrpc/com.atproto.identity.resolveHandle?handle={handle}";
				var handleData = await _http.GetFromJsonAsync<JsonDocument>(resolveUrl);
				if (handleData != null && handleData.RootElement.TryGetProperty("did", out var didProp)) {
					did = didProp.GetString();
				}
			} catch {
			}

			if (!string.IsNullOrEmpty(did)) {
				int byteStart = System.Text.Encoding.UTF8.GetByteCount(cleanText.Substring(0, match.Index));
				int byteEnd = byteStart + System.Text.Encoding.UTF8.GetByteCount(match.Value);

				facets.Add(new Dictionary<string, object> {
					{ "index", new Dictionary<string, object> { { "byteStart", byteStart }, { "byteEnd", byteEnd } } },
					{ "features", new[] {
						new Dictionary<string, object> {
							{ "$type", "app.bsky.richtext.facet#mention" },
							{ "did", did }
						}
					}}
				});
			}
		}

		facets.Sort((a, b) => {
			var aIndex = (Dictionary<string, object>)a["index"];
			var bIndex = (Dictionary<string, object>)b["index"];
			return ((int)aIndex["byteStart"]).CompareTo((int)bIndex["byteStart"]);
		});

		return (cleanText, facets);
	}

	public static async Task<CreateRecordResponse> CreatePostWithImageAsync(Uri pdsUrl, string token, string did, string text, string altText, BlobData uploadedBlob) {
		Uri recordEndpoint = new Uri(pdsUrl, "xrpc/com.atproto.repo.createRecord");

		var record = new Dictionary<string, object> {
			{ "$type", "app.bsky.feed.post" },
			{ "text", text },
			{ "createdAt", DateTime.UtcNow.ToString("O") },
			{ "embed", new Dictionary<string, object> {
				{ "$type", "app.bsky.embed.images" },
				{ "images", new[] {
					new Dictionary<string, object> {
						{ "alt", altText },
						{ "image", uploadedBlob }
					}
				}}
			}}
		};

		var facetResult = await ParseFacetsAsync(text);

		record["text"] = facetResult.cleanText;

		if (facetResult.facets.Count > 0) {
			record["facets"] = facetResult.facets;
		}

		var payload = new {
			repo = did,
			collection = "app.bsky.feed.post",
			record
		};

		using var request = new HttpRequestMessage(HttpMethod.Post, recordEndpoint);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

		string jsonPayload = JsonSerializer.Serialize(payload);
		request.Content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

		using var response = await _http.SendAsync(request);
		await EnsureSuccessWithBodyAsync(response);

		return (await response.Content.ReadFromJsonAsync<CreateRecordResponse>())!;
	}

	public static async Task<CreateRecordResponse> CreatePostWithExternalCardAsync(
		Uri pdsUrl,
		string token,
		string did,
		string text,
		string linkUrl,
		string cardTitle,
		string cardDescription,
		BlobData thumbBlob) {
		Uri recordEndpoint = new Uri(pdsUrl, "xrpc/com.atproto.repo.createRecord");

		var record = new Dictionary<string, object> {
			{ "$type", "app.bsky.feed.post" },
			{ "text", text },
			{ "createdAt", DateTime.UtcNow.ToString("O") },
			{ "embed", new Dictionary<string, object> {
				{ "$type", "app.bsky.embed.external" },
				{ "external", new Dictionary<string, object> {
					{ "uri", linkUrl },
					{ "title", cardTitle },
					{ "description", cardDescription },
					{ "thumb", thumbBlob }
				}}
			}}
		};

		var facetResult = await ParseFacetsAsync(text);

		record["text"] = facetResult.cleanText;

		if (facetResult.facets.Count > 0) {
			record["facets"] = facetResult.facets;
		}

		var payload = new { repo = did, collection = "app.bsky.feed.post", record = record };

		using var request = new HttpRequestMessage(HttpMethod.Post, recordEndpoint);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		request.Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

		using var response = await _http.SendAsync(request);
		await EnsureSuccessWithBodyAsync(response);

		return (await response.Content.ReadFromJsonAsync<CreateRecordResponse>())!;
	}

	private static async Task EnsureSuccessWithBodyAsync(HttpResponseMessage response) {
		if (!response.IsSuccessStatusCode) {
			string errorBody = await response.Content.ReadAsStringAsync();
			throw new HttpRequestException($"Bluesky API Error ({response.StatusCode}): {errorBody}");
		}
	}
}
public class BlueskySessionState {
	public Uri PdsUrl { get; set; } = new Uri("https://bsky.social/");
	public string Did { get; set; } = string.Empty;
	public string AccessJwt { get; set; } = string.Empty;
	public string RefreshJwt { get; set; } = string.Empty;
	public SemaphoreSlim AuthLock { get; } = new SemaphoreSlim(1, 1);
}

public class BlueskySessionManager {
	private readonly ConcurrentDictionary<string, BlueskySessionState> _sessions = new(StringComparer.OrdinalIgnoreCase);

	public async Task LoginAsync(string identifier, string appPassword) {
		string normalizedId = identifier.TrimStart('@');
		var session = _sessions.GetOrAdd(normalizedId, _ => new BlueskySessionState());

		if (!string.IsNullOrEmpty(session.AccessJwt)) return;

		await session.AuthLock.WaitAsync();
		try {
			if (!string.IsNullOrEmpty(session.AccessJwt)) return;

			session.PdsUrl = await DiscoverPdsAsync(normalizedId);
			var response = await CreateSessionAsync(session.PdsUrl, normalizedId, appPassword);

			session.Did = response.Did;
			session.AccessJwt = response.AccessJwt;
			session.RefreshJwt = response.RefreshJwt;
		} finally {
			session.AuthLock.Release();
		}
	}

	public async Task<Uri> UploadAndPostPhotoAsync(string identifier, string localPath, string text, string altText, string? worldUrl, string cardTitle, string cardDescription, bool useLinkCard) {
		string normalizedId = identifier.TrimStart('@');

		if (!_sessions.TryGetValue(normalizedId, out var session) || string.IsNullOrEmpty(session.AccessJwt)) {
			throw new InvalidOperationException($"{normalizedId} not logged in! Call LoginAsync please!!");
		}

		try {
			return await ExecutePostLogicAsync(session, localPath, text, altText, worldUrl, cardTitle, cardDescription, useLinkCard);
		} catch (HttpRequestException ex) when (ex.Message.Contains("401") || ex.Message.Contains("ExpiredToken")) {
			var newTokens = await RefreshSessionAsync(session.PdsUrl, session.RefreshJwt);

			session.Did = newTokens.Did;
			session.AccessJwt = newTokens.AccessJwt;
			session.RefreshJwt = newTokens.RefreshJwt;

			return await ExecutePostLogicAsync(session, localPath, text, altText, worldUrl, cardTitle, cardDescription, useLinkCard);
		}
	}

	private static async Task<Uri> ExecutePostLogicAsync(BlueskySessionState session, string localPath, string text, string altText, string? worldUrl, string cardTitle, string cardDescription, bool useLinkCard) {
		int targetBytes = useLinkCard ? 950000 : 1900000;
		var blob = await UploadBlobAsync(session.PdsUrl, session.AccessJwt, localPath, targetBytes);

		CreateRecordResponse record;

		if (useLinkCard && !string.IsNullOrEmpty(worldUrl)) {
			record = await CreatePostWithExternalCardAsync(session.PdsUrl, session.AccessJwt, session.Did, text, worldUrl, cardTitle, cardDescription, blob);
		}
		else {
			record = await CreatePostWithImageAsync(session.PdsUrl, session.AccessJwt, session.Did, text, altText, blob);
		}

		string[] parts = record.Uri.Replace("at://", "").Split('/');
		if (parts.Length >= 3) {
			return new Uri($"https://bsky.app/profile/{parts[0]}/post/{parts[2]}");
		}

		return new Uri(record.Uri);
	}
}
