using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Elements.Core;
using FrooxEngine;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Xmp;
using SixLabors.ImageSharp.Processing;

namespace RSEPostExtensions;

public class GalleVRClient {
	private static readonly HttpClient _http = new();
	private static readonly Uri apiUri = new Uri("https://api.gallevr.app/");
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

	// Nested Objects

	public record Player {
		[JsonPropertyName("id")]
		public required string Id { get; init; }

		[JsonPropertyName("displayName")]
		public required string DisplayName { get; init; }

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

	public static async Task<string?> UploadFileAsync(
		string userId,
		string token,
		string localFilePath,
		GalleVRPhotoMetadata metadata,
		PhotoMetadata photo) {

		var queryParams = new Dictionary<string, string> {
			{ "user", userId },
			{ "type", "webp" }
		};

		string queryString = string.Join("&", queryParams.Select(kvp =>
			$"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"
		));

		Uri uploadEndpoint = new Uri(apiUri, $"/vrchat/photo/upload?{queryString}");

		using var request = new HttpRequestMessage(HttpMethod.Post, uploadEndpoint);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		request.Headers.Add("metadata", metadata.ToBase64Header());

		var fileStream = await CompressAndInjectXmpAsync(photo, localFilePath);

		var fileContent = new StreamContent(fileStream);
		fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

		request.Content = fileContent;

		using var response = await _http.SendAsync(request);
		await EnsureSuccessWithBodyAsync(response);

		return await response.Content.ReadAsStringAsync();
	}

	private static async Task<System.IO.Stream> CompressAndInjectXmpAsync(PhotoMetadata photo, string localFilePath) {
		using var image = await Image.LoadAsync(localFilePath);

		int width = image.Width;
		int height = image.Height;
		bool isTransparent = true;

		if (width > 1920) {
			image.Mutate(x => x.Resize(1920, 0));
			width = image.Width;
			height = image.Height;
		}

		using var compressedStream = new MemoryStream();
		var encoder = new WebpEncoder {
			FileFormat = WebpFileFormatType.Lossy,
			Quality = 80
		};

		await image.SaveAsync(compressedStream, encoder);
		byte[] compressedBytes = compressedStream.ToArray();

		var rseMetadata = new ResoniteScreenshotExtensions.Metadata(photo);

		byte[] finalBytes = ResoniteScreenshotExtensions.MetadataInjector.InjectXmp(
			compressedBytes,
			ResoniteScreenshotExtensions.ResoniteScreenshotExtensions.ImageFormat.WEBP,
			rseMetadata,
			width,
			height,
			isTransparent
		);

		return new MemoryStream(finalBytes);
	}

	private static async Task EnsureSuccessWithBodyAsync(HttpResponseMessage response) {
		if (!response.IsSuccessStatusCode) {
			string errorBody = await response.Content.ReadAsStringAsync();
			throw new HttpRequestException($"GalleVR API Error ({response.StatusCode}): {errorBody}");
		}
	}
}
