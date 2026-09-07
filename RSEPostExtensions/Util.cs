using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using FrooxEngine;

namespace RSEPostExtensions;
internal class Util {
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
}
