using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Elements.Core;

using FrooxEngine;

using static RSEPostExtensions.RSEPostExtensions;
using static RSEPostExtensions.Util;

namespace RSEPostExtensions;

public class GalleVRClient {
	private static readonly HttpClient _http = new();
	private static readonly Uri apiUri = new("https://api.gallevr.app/");
	public record GalleVRPhotoMetadata {
		[JsonPropertyName("takenDate")]
		public required long TakenDate { get; init; }

		[JsonPropertyName("filename")]
		public required string Filename { get; init; }

		[JsonPropertyName("views")]
		public int Views { get; init; } = 0;

		[JsonPropertyName("localPath")]
		public required string LocalPath { get; init; }

		[JsonPropertyName("application")]
		public string Application { get; init; } = "Resonite";

		[JsonPropertyName("isNonVrcx")]
		public bool IsNonVrcx { get; init; } = false;

		[JsonPropertyName("cameraManufacturer")]
		public required string CameraManufacturer { get; init; }

		[JsonPropertyName("takenById")]
		public required string TakenById { get; init; }

		[JsonPropertyName("cameraFov")]
		public float? CameraFov { get; init; }

		[JsonPropertyName("takenGlobalPosition")]
		public SpatialData? TakenGlobalPosition { get; init; }

		[JsonPropertyName("takenGlobalRotation")]
		public SpatialData? TakenGlobalRotation { get; init; }

		[JsonPropertyName("takenGlobalScale")]
		public SpatialData? TakenGlobalScale { get; init; }

		[JsonPropertyName("world")]
		public WorldMetadata? World { get; init; }

		[JsonPropertyName("players")]
		public List<Player>? Players { get; init; }

		public string ToBase64Header() {
			string json = JsonSerializer.Serialize(this);
			return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
		}
	}

	public record Player {
		[JsonPropertyName("id")]
		public required string Id { get; init; }

		[JsonPropertyName("name")]
		public required string Name { get; init; }

		[JsonPropertyName("headPosition")]
		public required string HeadPosition { get; init; }

		[JsonPropertyName("headOrientation")]
		public required string HeadOrientation { get; init; }

		public static string PackPosition(float3 pos, float scale, bool isInView) {
			string x = pos.x.ToString(System.Globalization.CultureInfo.InvariantCulture);
			string y = pos.y.ToString(System.Globalization.CultureInfo.InvariantCulture);
			string z = pos.z.ToString(System.Globalization.CultureInfo.InvariantCulture);
			string s = scale.ToString(System.Globalization.CultureInfo.InvariantCulture);
			string v = isInView ? "1" : "0";
			return $"[{x}; {y}; {z}; {s}; {v}]";
		}

		public static string PackOrientation(floatQ rot) {
			string x = rot.x.ToString(System.Globalization.CultureInfo.InvariantCulture);
			string y = rot.y.ToString(System.Globalization.CultureInfo.InvariantCulture);
			string z = rot.z.ToString(System.Globalization.CultureInfo.InvariantCulture);
			string w = rot.w.ToString(System.Globalization.CultureInfo.InvariantCulture);
			return $"[{x}; {y}; {z}; {w}]";
		}
	}

	public record WorldMetadata {
		[JsonPropertyName("id")]
		public string? Id { get; init; }

		[JsonPropertyName("name")]
		public string? Name { get; init; }
	}

	public record SpatialData {
		[JsonPropertyName("x")]
		public float X { get; init; }

		[JsonPropertyName("y")]
		public float Y { get; init; }

		[JsonPropertyName("z")]
		public float Z { get; init; }

		[JsonPropertyName("w")]
		public float? W { get; init; }

		public static SpatialData FromFloat3(float3 vector) {
			return new SpatialData {
				X = vector.x,
				Y = vector.y,
				Z = vector.z,
				W = null
			};
		}

		public static SpatialData FromFloatQ(floatQ quaternion) {
			return new SpatialData {
				X = quaternion.x,
				Y = quaternion.y,
				Z = quaternion.z,
				W = quaternion.w
			};
		}
	}

	public readonly record struct Tier(
		string Name,
		int MaxSize,
		int MaxQuality,
		long MaxBytes,
		long MaxPixels
	);

	private static Tier GetTier(IEnumerable<string>? badges) {
		var badgeSet = badges != null
			? new HashSet<string>(badges, StringComparer.OrdinalIgnoreCase)
			: [];

		if (badgeSet.Contains("mega_supporter") || badgeSet.Contains("mega supporter")) {
			return new Tier(
				Name: "Mega Supporter",
				MaxSize: 7680,     // 8K
				MaxQuality: 95,
				MaxBytes: 5242880,  // 5.0 MiB
				MaxPixels: 33177600 // 7680x4320
			);
		}

		if (badgeSet.Contains("super_supporter") || badgeSet.Contains("super supporter")) {
			return new Tier(
				Name: "Super Supporter",
				MaxSize: 3840,    // 4K
				MaxQuality: 92,
				MaxBytes: 2621440, // 2.5 MiB
				MaxPixels: 8294400 // 3840x2160
			);
		}

		if (badgeSet.Contains("supporter")) {
			return new Tier(
				Name: "Supporter",
				MaxSize: 2560,    // 2K
				MaxQuality: 90,
				MaxBytes: 1048576, // 1.0 MiB
				MaxPixels: 3686400 // 2560x1440
			);
		}

		return new Tier(
			Name: "None",
			MaxSize: 1920,    // 1080p
			MaxQuality: 85,
			MaxBytes: 153600,  // 150 KiB
			MaxPixels: 2073600 // 1920x1080
		);
	}

	private static readonly SemaphoreSlim _cacheLock = new(1, 1);

	private static string? _cachedAccessKey;
	private static IReadOnlyList<string>? _cachedBadges;

	public static async Task<IReadOnlyList<string>?> GetBadgesAsync(
		string accessKey,
		bool forceRefresh = false,
		IProgress<CompressionProgress>? progress = null,
		CancellationToken ct = default) {
		if (string.IsNullOrWhiteSpace(accessKey))
			return null;

		if (!forceRefresh && _cachedBadges != null && _cachedAccessKey == accessKey) {
			return _cachedBadges;
		}

		await _cacheLock.WaitAsync(ct).ConfigureAwait(false);
		try {
			if (!forceRefresh && _cachedBadges != null && _cachedAccessKey == accessKey) {
				return _cachedBadges;
			}

			List<string>? fetched = await FetchBadgesFromApiAsync(accessKey, forceRefresh, progress, ct).ConfigureAwait(false);
			if (fetched != null) {
				_cachedAccessKey = accessKey;
				_cachedBadges = fetched;
			}

			return fetched ?? (_cachedAccessKey == accessKey ? _cachedBadges : null);
		} finally {
			_cacheLock.Release();
		}
	}

	public static void ClearBadgeCache() {
		_cachedAccessKey = null;
		_cachedBadges = null;
	}

	private static async Task<List<string>?> FetchBadgesFromApiAsync(
		string accessKey,
		bool forceRefresh,
		IProgress<CompressionProgress>? progress,
		CancellationToken ct) {
		try {
			progress?.Report(new CompressionProgress(1.0, "Checking tier"));

			string url = forceRefresh
				? "https://api.gallevr.app/vrchat/auth/me?cache=false"
				: "https://api.gallevr.app/vrchat/auth/me";

			using (var request = new HttpRequestMessage(HttpMethod.Get, url)) {
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessKey);
				request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

				using HttpResponseMessage response = await _http
					.SendAsync(request, ct)
					.ConfigureAwait(false);

				if (!response.IsSuccessStatusCode) {
					string errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
					RSEPostExtensions.Warn($"Failed to fetch badges ({(int)response.StatusCode}): {errorBody}");
					return null;
				}

				await using var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
				using JsonDocument doc = await JsonDocument.ParseAsync(responseStream, cancellationToken: ct).ConfigureAwait(false);

				var badges = new List<string>();
				if (doc.RootElement.TryGetProperty("badges", out JsonElement badgesElement) &&
					badgesElement.ValueKind == JsonValueKind.Array) {
					foreach (JsonElement item in badgesElement.EnumerateArray()) {
						string? badge = item.ValueKind == JsonValueKind.String
							? item.GetString()
							: item.ToString();

						if (!string.IsNullOrEmpty(badge))
							badges.Add(badge);
					}
				}

				RSEPostExtensions.Msg($"Cached badges for session: [{string.Join(", ", badges)}]");
				return badges;
			}
			;
		} catch (Exception ex) {
			RSEPostExtensions.Error($"Error fetching user badges:\n{ex}");
			return null;
		}
	}

	public static async Task<string?> UploadFileAsync(
		string userId,
		string token,
		string localFilePath,
		GalleVRPhotoMetadata metadata,
		PhotoMetadata photo,
		IProgress<CompressionProgress>? progress = null,
		CancellationToken ct = default) {

		var queryParams = new Dictionary<string, string> {
			{ "user", userId },
			{ "type", "webp" }
		};

		string queryString = string.Join("&", queryParams.Select(kvp =>
			$"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"
		));

		Uri uploadEndpoint = new(apiUri, $"/vrchat/photo/upload?{queryString}");

		using var request = new HttpRequestMessage(HttpMethod.Post, uploadEndpoint);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		request.Headers.Add("metadata", metadata.ToBase64Header());

		IReadOnlyList<string>? badges = await GetBadgesAsync(token, progress: progress, ct: ct);
		Tier tier = GetTier(badges);
		RSEPostExtensions.Msg($"Compressing GalleVR image with {tier.Name} tier capabilities ({tier.MaxBytes} target size/{tier.MaxSize} longest side/{tier.MaxQuality} quality/{tier.MaxPixels} max pixels)");

		double targetMiB = Math.Max(0, tier.MaxBytes - 4096) / (1024.0 * 1024.0); // subtracting a lil just in case

		using (System.IO.Stream fileStream = await Util.CompressImageAsync(
			localFilePath,
			targetMiB,
			tier.MaxSize,
			tier.MaxQuality,
			tier.MaxPixels,
			postProcess: (bytes, w, h) => Util.InjectXmp(bytes, photo, w, h),
			progress: progress,
			ct: ct
		)) {
			fileStream.Position = 0;

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
			fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

			request.Content = fileContent;

			using (HttpResponseMessage response = await _http.SendAsync(request, ct)) {
				await EnsureSuccessWithBodyAsync(response);
				return await response.Content.ReadAsStringAsync(ct);
			}
			;
		}
		;
	}

	private static async Task EnsureSuccessWithBodyAsync(HttpResponseMessage response) {
		if (!response.IsSuccessStatusCode) {
			string errorBody = await response.Content.ReadAsStringAsync();
			throw new HttpRequestException($"GalleVR API Error ({response.StatusCode}): {errorBody}");
		}
	}
}
