using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using SixLabors.ImageSharp.Processing;

using static Elements.Core.FileUtil;
using static RSEPostExtensions.BlueskyClient;

namespace RSEPostExtensions;
public partial class BlueskyClient {
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

	public sealed record ServiceAuthResponse(
	[property: JsonPropertyName("token")] string Token
);

	public sealed record VideoJobStatusResponse(
		[property: JsonPropertyName("jobStatus")] VideoJobStatus JobStatus
	);

	public sealed record VideoJobStatus(
		[property: JsonPropertyName("jobId")] string JobId,
		[property: JsonPropertyName("did")] string Did,
		[property: JsonPropertyName("state")] string State,
		[property: JsonPropertyName("progress")] int? Progress,
		[property: JsonPropertyName("blob")] BlobData? Blob,
		[property: JsonPropertyName("error")] string? Error,
		[property: JsonPropertyName("message")] string? Message
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
				string domain = did[8..];
				didDoc = await _http.GetFromJsonAsync<JsonDocument>($"https://{domain}/.well-known/did.json");
			}

			if (didDoc != null && didDoc.RootElement.TryGetProperty("service", out var services)) {
				foreach (var service in services.EnumerateArray()) {
					if (service.TryGetProperty("id", out var idVal) && idVal.GetString() == "#atproto_pds") {
						if (service.TryGetProperty("serviceEndpoint", out var endpoint)) {
							string endpointStr = endpoint.GetString()!;
							if (!endpointStr.EndsWith('/')) endpointStr += "/";
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
		Uri authEndpoint = new(pdsUrl, "xrpc/com.atproto.server.createSession");

		var payload = new SessionRequest(identifier, password);
		using var request = new HttpRequestMessage(HttpMethod.Post, authEndpoint);
		request.Content = JsonContent.Create(payload);

		using var response = await _http.SendAsync(request);
		await EnsureSuccessWithBodyAsync(response);

		return (await response.Content.ReadFromJsonAsync<SessionResponse>())!;
	}

	public static async Task<SessionResponse> RefreshSessionAsync(Uri pdsUrl, string refreshJwt) {
		Uri refreshEndpoint = new(pdsUrl, "xrpc/com.atproto.server.refreshSession");

		using var request = new HttpRequestMessage(HttpMethod.Post, refreshEndpoint);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshJwt);

		using var response = await _http.SendAsync(request);
		await EnsureSuccessWithBodyAsync(response);

		return (await response.Content.ReadFromJsonAsync<SessionResponse>())!;
	}
	private static async Task<(Stream stream, string mimeType)> EnsureImageSizeLimitAsync(string localFilePath, int maxBytes) {
		var fileInfo = new FileInfo(localFilePath);

		if (fileInfo.Length <= maxBytes) {
			FileStream fs = File.OpenRead(localFilePath);
			try {
				(string mimeType, _) = Util.GuessImageMimeType(fs);
				fs.Position = 0;
				return (fs, mimeType);
			} catch {
				await fs.DisposeAsync();
				throw;
			}
		}

		double targetMiB = maxBytes / (1024.0 * 1024.0);

		MemoryStream compressedStream = await Util.CompressImageAsync(
			localFilePath,
			targetMiB: targetMiB,
			maxDimension: 2000
		);

		if (compressedStream.Length > maxBytes) {
			await compressedStream.DisposeAsync();
			throw new InvalidOperationException($"Image is too complex to compress below {maxBytes / 1000000.0:0.##}MB.");
		}

		compressedStream.Position = 0;
		return (compressedStream, "image/webp");
	}

	public static async Task<BlobData> UploadBlobAsync(Uri pdsUrl, string token, string localFilePath, int maxBytes) {
		Uri uploadEndpoint = new(pdsUrl, "xrpc/com.atproto.repo.uploadBlob");
		using var request = new HttpRequestMessage(HttpMethod.Post, uploadEndpoint);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

		(Stream stream, string mimeType) = await EnsureImageSizeLimitAsync(localFilePath, maxBytes);
		StreamContent content = new(stream);
		content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
		request.Content = content;

		using var response = await _http.SendAsync(request);
		await EnsureSuccessWithBodyAsync(response);

		var result = await response.Content.ReadFromJsonAsync<BlobResponse>();
		return result!.Blob;
	}

	private static readonly Uri VideoServiceBaseUri = new("https://video.bsky.app/");

	public static async Task<(BlobData, (int, int) resolution)> UploadVideoAsync(
		Uri pdsUrl,
		string token,
		string did,
		string filePath,
		CancellationToken cancellationToken = default) {

		(string compressedVideoPath, bool autoDelete, (int, int) resolution) = await Util.CompressVideoAsync(filePath, 299, 96, true, cancellationToken);
		try {
			using (FileStream fileStream = File.OpenRead(compressedVideoPath)) {
				(string mimeType, string extension) = Util.GuessVideoMimeType(fileStream);

				long exp = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
				string audience = Uri.EscapeDataString($"did:web:{pdsUrl.Host}");
				Uri authEndpoint = new(pdsUrl, $"xrpc/com.atproto.server.getServiceAuth?aud={audience}&lxm=com.atproto.repo.uploadBlob&exp={exp}");

				using var authRequest = new HttpRequestMessage(HttpMethod.Get, authEndpoint);
				authRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

				using var authResponse = await _http.SendAsync(authRequest, cancellationToken);
				await EnsureSuccessWithBodyAsync(authResponse);

				var authResult = (await authResponse.Content.ReadFromJsonAsync<ServiceAuthResponse>(cancellationToken))!;

				string encodedDid = Uri.EscapeDataString(did);
				string encodedName = Uri.EscapeDataString($"video{extension}");
				Uri uploadEndpoint = new(VideoServiceBaseUri, $"xrpc/app.bsky.video.uploadVideo?did={encodedDid}&name={encodedName}");

				using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, uploadEndpoint);
				uploadRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authResult.Token);

				var streamContent = new StreamContent(fileStream);
				streamContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
				if (fileStream.CanSeek) {
					streamContent.Headers.ContentLength = fileStream.Length - fileStream.Position;
				}
				uploadRequest.Content = streamContent;

				using var uploadResponse = await _http.SendAsync(uploadRequest, cancellationToken);

				VideoJobStatus? status = null;
				if (uploadResponse.IsSuccessStatusCode || uploadResponse.StatusCode == HttpStatusCode.Conflict) {
					status = await uploadResponse.Content.ReadFromJsonAsync<VideoJobStatus>(cancellationToken);
				}

				if (status is null) {
					await EnsureSuccessWithBodyAsync(uploadResponse);
					throw new InvalidOperationException("Video upload returned an empty job status.");
				}

				Uri statusEndpoint = new(VideoServiceBaseUri, $"xrpc/app.bsky.video.getJobStatus?jobId={Uri.EscapeDataString(status.JobId)}");

				while (status.Blob is null) {
					if (status.State == "JOB_STATE_FAILED") {
						throw new InvalidOperationException($"Bluesky video processing failed ({status.Error}): {status.Message}");
					}

					await Task.Delay(1000, cancellationToken);

					using var statusResponse = await _http.GetAsync(statusEndpoint, cancellationToken);
					await EnsureSuccessWithBodyAsync(statusResponse);

					var wrapper = (await statusResponse.Content.ReadFromJsonAsync<VideoJobStatusResponse>(cancellationToken))!;
					status = wrapper.JobStatus;
				}

				return (status.Blob, resolution);
			}
		} finally {
			if (autoDelete && File.Exists(compressedVideoPath)) {
				try { File.Delete(compressedVideoPath); } catch { }
			}
		}
	}

	private static async Task<(string cleanText, List<Dictionary<string, object>> facets)> ParseFacetsAsync(string text) {
		var facets = new List<Dictionary<string, object>>();
		var sb = new System.Text.StringBuilder();
		int lastIndex = 0;

		var markdownRegex = LinkFacetRegex();
		foreach (System.Text.RegularExpressions.Match match in markdownRegex.Matches(text)) {
			sb.Append(text[lastIndex..match.Index]);

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

		sb.Append(text[lastIndex..]);
		string cleanText = sb.ToString();

		var tagRegex = HashtagFacetRegex();
		var urlRegex = URLFacetRegex();
		var mentionRegex = MentionFacetRegex();

		foreach (System.Text.RegularExpressions.Match match in tagRegex.Matches(cleanText)) {
			int byteStart = System.Text.Encoding.UTF8.GetByteCount(cleanText[..match.Index]);
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
			int byteStart = System.Text.Encoding.UTF8.GetByteCount(cleanText[..match.Index]);
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
				int byteStart = System.Text.Encoding.UTF8.GetByteCount(cleanText[..match.Index]);
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
		Uri recordEndpoint = new(pdsUrl, "xrpc/com.atproto.repo.createRecord");

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

		var (cleanText, facets) = await ParseFacetsAsync(text);

		record["text"] = cleanText;

		if (facets.Count > 0) {
			record["facets"] = facets;
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

	public static async Task<CreateRecordResponse> CreatePostWithVideoAsync(Uri pdsUrl, string token, string did, string text, string altText, BlobData uploadedBlob, (int width, int height) resolution) {
		Uri recordEndpoint = new(pdsUrl, "xrpc/com.atproto.repo.createRecord");

		var record = new Dictionary<string, object> {
			{ "$type", "app.bsky.feed.post" },
			{ "text", text },
			{ "createdAt", DateTime.UtcNow.ToString("O") },
			{ "embed", new Dictionary<string, object> {
				{ "$type", "app.bsky.embed.video" },
				{ "video", uploadedBlob },
				{ "presentation", "default" },
				{ "alt", altText },
				{ "aspectRatio", new Dictionary<string, int> {
					{ "width", resolution.width },
					{ "height", resolution.height }
				}}
			}}
		};

		var (cleanText, facets) = await ParseFacetsAsync(text);

		record["text"] = cleanText;

		if (facets.Count > 0) {
			record["facets"] = facets;
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
		Uri recordEndpoint = new(pdsUrl, "xrpc/com.atproto.repo.createRecord");

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

		var (cleanText, facets) = await ParseFacetsAsync(text);

		record["text"] = cleanText;

		if (facets.Count > 0) {
			record["facets"] = facets;
		}

		var payload = new { repo = did, collection = "app.bsky.feed.post", record };

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

	[System.Text.RegularExpressions.GeneratedRegex(@"\[(.*?)\]\((.*?)\)")]
	private static partial System.Text.RegularExpressions.Regex LinkFacetRegex();
	[System.Text.RegularExpressions.GeneratedRegex(@"(?<=^|\s)(#[\p{L}\p{N}_]+)")]
	private static partial System.Text.RegularExpressions.Regex HashtagFacetRegex();
	[System.Text.RegularExpressions.GeneratedRegex(@"(?<=^|\s)(https?://[^\s<]+[^<.,;:!?\'\""\s])")]
	private static partial System.Text.RegularExpressions.Regex URLFacetRegex();
	[System.Text.RegularExpressions.GeneratedRegex(@"(?<=^|\s)(@([a-zA-Z0-9-]+\.)+[a-zA-Z0-9-]+)")]
	private static partial System.Text.RegularExpressions.Regex MentionFacetRegex();
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

	public async Task<Uri> UploadAndPostMediaAsync(string identifier, string localPath, bool isVideo, string text, string altText, string? worldUrl, string cardTitle, string cardDescription, bool useLinkCard) {
		string normalizedId = identifier.TrimStart('@');

		if (!_sessions.TryGetValue(normalizedId, out var session) || string.IsNullOrEmpty(session.AccessJwt)) {
			throw new InvalidOperationException($"{normalizedId} not logged in! Call LoginAsync please!!");
		}

		try {
			return await ExecutePostLogicAsync(session, localPath, isVideo, text, altText, worldUrl, cardTitle, cardDescription, useLinkCard);
		} catch (HttpRequestException ex) when (ex.Message.Contains("401") || ex.Message.Contains("ExpiredToken")) {
			var newTokens = await RefreshSessionAsync(session.PdsUrl, session.RefreshJwt);

			session.Did = newTokens.Did;
			session.AccessJwt = newTokens.AccessJwt;
			session.RefreshJwt = newTokens.RefreshJwt;

			return await ExecutePostLogicAsync(session, localPath, isVideo, text, altText, worldUrl, cardTitle, cardDescription, useLinkCard);
		}
	}

	private static async Task<Uri> ExecutePostLogicAsync(BlueskySessionState session, string localPath, bool isVideo, string text, string altText, string? worldUrl, string cardTitle, string cardDescription, bool useLinkCard) {
		int targetBytes = useLinkCard ? 950000 : 1900000;
		
		BlobData? blob;
		(int, int)? resolution = null;
		if (isVideo) {
			(blob, resolution) = await UploadVideoAsync(session.PdsUrl, session.AccessJwt, session.Did, localPath);
		} else {
			blob = await UploadBlobAsync(session.PdsUrl, session.AccessJwt, localPath, targetBytes);
		}

		CreateRecordResponse record;

		if (useLinkCard && !isVideo && !string.IsNullOrEmpty(worldUrl)) {
			record = await CreatePostWithExternalCardAsync(session.PdsUrl, session.AccessJwt, session.Did, text, worldUrl, cardTitle, cardDescription, blob);
		}
		else if (isVideo) {
			record = await CreatePostWithVideoAsync(session.PdsUrl, session.AccessJwt, session.Did, text, altText, blob, resolution ?? (128, 128));
		} else {
			record = await CreatePostWithImageAsync(session.PdsUrl, session.AccessJwt, session.Did, text, altText, blob);
		}

			string[] parts = record.Uri.Replace("at://", "").Split('/');
		if (parts.Length >= 3) {
			return new Uri($"https://bsky.app/profile/{parts[0]}/post/{parts[2]}");
		}

		return new Uri(record.Uri);
	}
}
