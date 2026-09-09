using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FrooxEngine;

namespace RSEPostExtensions;
internal class Util {
	private static readonly HttpClient _http = new();
	private static readonly string[] Adjectives = {
		"Swift", "Clever", "Brave", "Bright", "Calm", "Eager", "Kind", "Lively",
		"Silly", "Wild", "Gentle", "Fancy", "Proud", "Jolly", "Vast", "Sharp",
		"Creative", "Carefree", "Antsy", "Alert", "Tired", "Sneaky", "Crafty",
		"Poised", "Hungry", "Busy"
	};

	private static readonly string[] Colors = {
		"Red", "Orange", "Yellow", "Green", "Blue", "Purple", "Gold", "Silver",
		"Amber", "Ruby", "Neon", "Azure", "Jade", "Coral", "Indigo", "Onyx",
		"Plum", "Teal", "Violet", "Cyan", "Maroon", "Brown", "White", "Black", "Tan"
	};

	private static readonly string[] Critters = {
		"Badger", "Falcon", "Otter", "Panda", "Fox", "Wolf", "Eagle", "Tiger",
		"Dolphin", "Koala", "Lynx", "Owl", "Panther", "Rabbit", "Bear", "Hawk",
		"Beaver", "Aardvark", "Marten", "Dog", "Dragon", "Cat", "Avali", "Robot",
		"Beetle", "Expie", "Dinosaur", "Bird", "Snake", "Protogen"
	};

	public static string GetMemorableName(string input) {
		if (string.IsNullOrWhiteSpace(input))
			return "???";

		int seed = GetDeterministicHashCode(input);

		Random rand = new Random(seed);

		string adjective = Adjectives[rand.Next(Adjectives.Length)];
		string color = Colors[rand.Next(Colors.Length)];
		string animal = Critters[rand.Next(Critters.Length)];

		return $"{adjective} {color} {animal}";
	}

	private static int GetDeterministicHashCode(string str) {
		using (SHA256 sha256 = SHA256.Create()) {
			byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(str));
			return BitConverter.ToInt32(hashBytes, 0);
		}
	}

    private static readonly HttpClient _httpClient = new HttpClient();
	private static readonly ConcurrentDictionary<string, WebManifest> _manifestCache = new();
	public static async Task<WebManifest?> GetManifestAsync(string baseUrl) {
		string url = $"{baseUrl.TrimEnd('/')}/manifest.json";

		if (_manifestCache.TryGetValue(url, out var cachedManifest)) {
			return cachedManifest;
		}

		string jsonString = await _httpClient.GetStringAsync(url);

		var manifest = JsonSerializer.Deserialize<WebManifest>(jsonString);

		if (manifest != null) {
			_manifestCache.TryAdd(url, manifest);
		}

		return manifest;
	}

	public static string GuessImageMimeType(FileStream stream) {
		if (stream.Length < 12) return "application/octet-stream";

		byte[] header = new byte[12];
		stream.ReadExactly(header, 0, 12);

		stream.Position = 0;

		if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
			return "image/png";

		if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
			return "image/jpeg";

		if (header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x38)
			return "image/gif";

		if (header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
			header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
			return "image/webp";

		return "application/octet-stream";
	}

	public static async Task<string> GetUsernameFromUserId(string userId, World? world) {
		if (world != null) {
			User ingameUser = world.GetUserByUserId(userId);
			if (ingameUser != null) {
				return ingameUser.UserName;
			}
		}
		var user = await Engine.Current.Cloud.Users.GetUser(userId);
		if (user.IsOK)
			return user.Entity.Username;
		return userId;
	}

	public static Uri? ResrecToGoResonite(Uri? locationUrl) {
		string? urlStr = locationUrl?.ToString();

		if (string.IsNullOrEmpty(urlStr)) return null;

		if (urlStr.StartsWith("resrec://", StringComparison.OrdinalIgnoreCase)) {
			string cleanPath = urlStr.Substring(9).TrimStart('/');
			return new Uri($"https://go.resonite.com/world/{cleanPath}");
		}

		return locationUrl;
	}

	public static async Task<(string WorldName, string OwnerName, bool IsPublic)?> FetchWorldDetailsFromUrlAsync(Uri worldUrl) {
		try {
			string[] parts = worldUrl.OriginalString.TrimEnd('/').Split('/');
			if (parts.Length < 2) return null;

			string recordId = parts[^1];
			string ownerId = parts[^2];
			bool isGroup = ownerId.StartsWith("G-");
			string basePath = isGroup ? "groups" : "users";

			string recordEndpoint = $"https://api.resonite.com/{basePath}/{ownerId}/records/{recordId}";
			var recordData = await _http.GetFromJsonAsync<JsonElement>(recordEndpoint);

			bool isPublic = false;
			if (recordData.TryGetProperty("isPublic", out var pubProp)) {
				isPublic = pubProp.GetBoolean();
			}

			string worldName = recordData.GetProperty("name").GetString() ?? "A Resonite World";

			string ownerEndpoint = $"https://api.resonite.com/{basePath}/{ownerId}";
			var ownerData = await _http.GetFromJsonAsync<JsonElement>(ownerEndpoint);

			string ownerProp = isGroup ? "name" : "username";
			string ownerName = ownerData.GetProperty(ownerProp).GetString() ?? "Unknown Creator";

			return (worldName, ownerName, isPublic);
		} catch (Exception ex) {
			RSEPostExtensions.Warn($"Failed to fetch world metadata: {ex.Message}");
			return null;
		}
	}

	public static async Task<(string worldName, string worldOwner, Uri? worldUrl)> GetWorldDetailsFromMetadataAsync(PhotoMetadata photo) {
		string worldName = "A Resonite World";
		string worldOwner = "Unknown Creator";
		Uri? worldUrl = ResrecToGoResonite(photo.LocationURL.Value);

		if (photo.LocationURL.Value != null) {
			var apiResult = await FetchWorldDetailsFromUrlAsync(photo.LocationURL.Value);

			if (apiResult != null && apiResult.Value.IsPublic) {
				worldName = apiResult.Value.WorldName;
				worldOwner = apiResult.Value.OwnerName;
			} else {
				worldName = photo.LocationName?.Value ?? "Unknown Location";
				worldOwner = (photo.LocationHost?._userId != null) ? await GetUsernameFromUserId(photo.LocationHost._userId, photo.World) : "Unknown Creator";
				worldUrl = null;
			}
		}
		return (worldName, worldOwner, worldUrl);
	}
}
