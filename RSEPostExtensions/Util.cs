using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using FFMpegCore;
using FFMpegCore.Enums;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Xmp;
using SixLabors.ImageSharp.Processing;

using FrooxEngine;

using Metadata = ResoniteScreenshotExtensions.Metadata;
using RSE = ResoniteScreenshotExtensions.ResoniteScreenshotExtensions;

namespace RSEPostExtensions;
internal class Util {
	private static readonly HttpClient _http = new();
	private static readonly string[] Adjectives = [
		"Swift", "Clever", "Brave", "Bright", "Calm", "Eager", "Kind", "Lively",
		"Silly", "Wild", "Gentle", "Fancy", "Proud", "Jolly", "Vast", "Sharp",
		"Creative", "Carefree", "Antsy", "Alert", "Tired", "Sneaky", "Crafty",
		"Poised", "Hungry", "Busy"
	];

	private static readonly string[] Colors = [
		"Red", "Orange", "Yellow", "Green", "Blue", "Purple", "Gold", "Silver",
		"Amber", "Ruby", "Neon", "Azure", "Jade", "Coral", "Indigo", "Onyx",
		"Plum", "Teal", "Violet", "Cyan", "Maroon", "Brown", "White", "Black", "Tan"
	];

	private static readonly string[] Critters = [
		"Badger", "Falcon", "Otter", "Panda", "Fox", "Wolf", "Eagle", "Tiger",
		"Dolphin", "Koala", "Lynx", "Owl", "Panther", "Rabbit", "Bear", "Hawk",
		"Beaver", "Aardvark", "Marten", "Dog", "Dragon", "Cat", "Avali", "Robot",
		"Beetle", "Expie", "Dinosaur", "Bird", "Snake", "Protogen"
	];

	public static string GetMemorableName(string input) {
		if (string.IsNullOrWhiteSpace(input))
			return "???";

		int seed = GetDeterministicHashCode(input);

		Random rand = new(seed);

		string adjective = Adjectives[rand.Next(Adjectives.Length)];
		string color = Colors[rand.Next(Colors.Length)];
		string animal = Critters[rand.Next(Critters.Length)];

		return $"{adjective} {color} {animal}";
	}

	private static int GetDeterministicHashCode(string str) {
		byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(str));
		return BitConverter.ToInt32(hashBytes, 0);
	}

	private static readonly HttpClient _httpClient = new();
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

	public static (string mime, string extension) GuessImageMimeType(FileStream stream) {
		if (stream.Length < 12) return ("application/octet-stream", ".png");

		byte[] header = new byte[12];
		stream.ReadExactly(header, 0, 12);

		stream.Position = 0;

		if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
			return ("image/png", ".png");

		if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
			return ("image/jpeg", ".jpeg");

		if (header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x38)
			return ("image/gif", ".gif");

		if (header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
			header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
			return ("image/webp", ".webp");

		return ("application/octet-stream", ".png");
	}

	public static (string mime, string extension) GuessVideoMimeType(FileStream stream) {
		if (stream.Length < 16) return ("application/octet-stream", ".mp4");

		byte[] header = new byte[16];
		stream.ReadExactly(header, 0, 16);

		stream.Position = 0;

		if (header[4] == 0x66 && header[5] == 0x74 && header[6] == 0x79 && header[7] == 0x70) {
			if (header[8] == 0x71 && header[9] == 0x74 && header[10] == 0x20 && header[11] == 0x20) {
				return ("video/quicktime", ".mov");
			}
			return ("video/mp4", ".mp4");
		}

		if (header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
			header[8] == 0x41 && header[9] == 0x56 && header[10] == 0x49 && header[11] == 0x20) {
			return ("video/x-msvideo", ".avi");
		}

		if (header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3) {
			return ("video/webm", ".webm"); // could also be mkv, but webm seems more likely in reso
		}

		if (header[0] == 0x46 && header[1] == 0x4C && header[2] == 0x56){
			return ("video/x-flv", ".flv");
		}

		if (header[0] == 0x00 && header[1] == 0x00 && header[2] == 0x01 && (header[3] == 0xBA || header[3] == 0xB3)) {
			return ("video/mpeg", ".mpg");
		}

		if (header[0] == 0x4F && header[1] == 0x67 && header[2] == 0x67 && header[3] == 0x53) {
			return ("video/ogg", ".ogv");
		}

		if (header[0] == 0x30 && header[1] == 0x26 && header[2] == 0xB2 && header[3] == 0x75 &&
			header[4] == 0x8E && header[5] == 0x66 && header[6] == 0xCF && header[7] == 0x11 &&
			header[8] == 0xA6 && header[9] == 0xD9 && header[10] == 0x00 && header[11] == 0xAA &&
			header[12] == 0x00 && header[13] == 0x62 && header[14] == 0xCE && header[15] == 0x6C) {
			return ("video/x-ms-wmv", ".wmv");
		}

		return ("application/octet-stream", ".mp4");
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
			string cleanPath = urlStr[9..].TrimStart('/');
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

	public static async Task<(string worldName, string worldOwner, Uri? worldUrl)> GetWorldDetailsFromMetadataAsync(CameraMediaMetadataBase metadata) {
		string worldName = "A Resonite World";
		string worldOwner = "Unknown Creator";
		Uri? worldUrl = ResrecToGoResonite(metadata.LocationURL.Value);

		if (metadata.LocationURL.Value != null) {
			var apiResult = await FetchWorldDetailsFromUrlAsync(metadata.LocationURL.Value);

			if (apiResult != null && apiResult.Value.IsPublic) {
				worldName = apiResult.Value.WorldName;
				worldOwner = apiResult.Value.OwnerName;
			} else {
				worldName = metadata.LocationName?.Value ?? "Unknown Location";
				worldOwner = (metadata.LocationHost?._userId != null) ? await GetUsernameFromUserId(metadata.LocationHost._userId, metadata.World) : "Unknown Creator";
				worldUrl = null;
			}
		}
		return (worldName, worldOwner, worldUrl);
	}

	private enum EncoderChoice {
		Nvenc,
		Amf,
		X264TwoPass,
		OpenH264
	}

	private static EncoderChoice SelectBestEncoder() {
		string gpu = Engine.Current.SystemInfo.GPU;

		if (gpu.Contains("nvidia", StringComparison.InvariantCultureIgnoreCase) && HasCodec("h264_nvenc"))
			return EncoderChoice.Nvenc;

		if (gpu.Contains("amd", StringComparison.InvariantCultureIgnoreCase) && HasCodec("h264_amf"))
			return EncoderChoice.Amf;

		if (FFMpegHelper.SupportsX264)
			return EncoderChoice.X264TwoPass;

		return EncoderChoice.OpenH264;
	}

	private static bool HasCodec(string codecName) {
		return FFMpegHelper.SupportedVideoCodecs.Any(c =>
			string.Equals(c.Name, codecName, StringComparison.OrdinalIgnoreCase));
	}

	public static async Task<(string outputPath, bool wasCompressed)> CompressVideoAsync(
	string inputPath,
	double targetMiB = 19,
	int audioKbps = 96,
	bool fixColorRange = true,
	CancellationToken ct = default) {
		long maxBytes = (long)(targetMiB * 1024 * 1024);
		var inputInfo = new FileInfo(inputPath);

		if (!fixColorRange && inputInfo.Length <= maxBytes) {
			return (inputPath, false);
		}

		IMediaAnalysis mediaInfo = await FFProbe.AnalyseAsync(inputPath, cancellationToken: ct).ConfigureAwait(false);
		double durationSeconds = mediaInfo.Duration.TotalSeconds;

		if (durationSeconds <= 0.1)
			throw new InvalidOperationException("Invalid video duration");

		EncoderChoice encoder = SelectBestEncoder();
		RSEPostExtensions.Msg($"Determined best encoder is {encoder}");

		double containerFactor = encoder == EncoderChoice.X264TwoPass ? 0.94 : 0.90;
		double totalKbits = targetMiB * 8192.0 * containerFactor;
		int videoKbps = (int)(totalKbits / durationSeconds) - audioKbps;

		if (videoKbps < 150)
			throw new InvalidOperationException($"{durationSeconds:F1}s video is too long to fit within {targetMiB} MiB");

		long sourceBitRate = mediaInfo.PrimaryVideoStream?.BitRate ?? 0;
		if (sourceBitRate > 0) {
			int sourceKbps = (int)(sourceBitRate / 1000);
			if (sourceKbps > 150) {
				videoKbps = Math.Min(videoKbps, sourceKbps);
			}
		}

		int? targetHeight = videoKbps switch {
			< 600 => 480,
			< 1400 => 720,
			_ => null
		};

		string tempOutputPath = Path.Combine(Path.GetTempPath(), $"RSEPostExtensions_compressed_{Guid.NewGuid():N}.mp4");

		try {
			if (encoder == EncoderChoice.X264TwoPass) {
				await EncodeTwoPassX264Async(inputPath, tempOutputPath, videoKbps, audioKbps, targetHeight, fixColorRange, ct).ConfigureAwait(false);
			} else {
				await EncodeSinglePassAsync(inputPath, tempOutputPath, encoder, videoKbps, audioKbps, targetHeight, fixColorRange, ct).ConfigureAwait(false);
			}

			return (tempOutputPath, true);
		} catch {
			if (File.Exists(tempOutputPath)) {
				try { File.Delete(tempOutputPath); } catch { }
			}
			throw;
		}
	}

	private static async Task EncodeSinglePassAsync(
		string inputPath,
		string outputPath,
		EncoderChoice encoder,
		int videoKbps,
		int audioKbps,
		int? targetHeight,
		bool fixColorRange,
		CancellationToken ct) {
		if (encoder == EncoderChoice.Nvenc && !fixColorRange) {
			try {
				await EncodeNvencFullCudaAsync(inputPath, outputPath, videoKbps, audioKbps, targetHeight, ct).ConfigureAwait(false);
				return;
			} catch (Exception e) {
				RSEPostExtensions.Warn($"Full CUDA compression had an exception, falling back to partial NVENC compression: {e.Message}");
			}
		}

		string pixFmt = encoder == EncoderChoice.Nvenc ? "nv12" : "yuv420p";

		string vfFilter = (targetHeight.HasValue, fixColorRange) switch {
			(true, true) => $"scale=-2:{targetHeight!.Value}:in_range=full:out_range=tv,format={pixFmt}",
			(true, false) => $"scale=-2:{targetHeight!.Value},format={pixFmt}",
			(false, true) => $"scale=in_range=full:out_range=tv,format={pixFmt}",
			(false, false) => string.Empty
		};

		await FFMpegArguments
			.FromFileInput(inputPath, verifyExists: true, options => {
				if (encoder == EncoderChoice.Nvenc)
					options.WithCustomArgument("-hwaccel cuda");
			})
			.OutputToFile(outputPath, overwrite: true, options => {
				switch (encoder) {
					case EncoderChoice.Nvenc:
						options.WithVideoCodec("h264_nvenc");
						break;
					case EncoderChoice.Amf:
						options.WithVideoCodec("h264_amf");
						break;
					default:
						options.WithVideoCodec("libopenh264");
						break;
				}

				if (string.IsNullOrEmpty(vfFilter))
					options.WithCustomArgument($"-pix_fmt {pixFmt}");
				else
					options.WithCustomArgument($"-vf \"{vfFilter}\"");

				if (fixColorRange)
					options.WithCustomArgument("-color_range tv -colorspace bt709 -color_primaries bt709 -color_trc bt709");

				options
					.WithVideoBitrate(videoKbps)
					.WithAudioCodec("aac")
					.WithAudioBitrate(audioKbps)
					.WithFastStart()
					.WithCustomArgument($"-threads 2 -maxrate {(int)(videoKbps * 1.15)}k -bufsize {videoKbps * 2}k");
			})
			.CancellableThrough(ct)
			.ProcessAsynchronously()
			.ConfigureAwait(false);
	}

	private static async Task EncodeTwoPassX264Async(
		string inputPath,
		string outputPath,
		int videoKbps,
		int audioKbps,
		int? targetHeight,
		bool fixColorRange,
		CancellationToken ct) {
		string passLogPrefix = Path.Combine(Path.GetTempPath(), $"RSEPostExtensions_x264_{Guid.NewGuid():N}");
		string nullTarget = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "NUL" : "/dev/null"; // hopefully works on linux?? no clue about the like 3 mac users

		string vfFilter = (targetHeight.HasValue, fixColorRange) switch {
			(true, true) => $"scale=-2:{targetHeight!.Value}:in_range=full:out_range=tv,format=yuv420p",
			(true, false) => $"scale=-2:{targetHeight!.Value},format=yuv420p",
			(false, true) => "scale=in_range=full:out_range=tv,format=yuv420p",
			(false, false) => string.Empty
		};

		try {
			await FFMpegArguments
				.FromFileInput(inputPath)
				.OutputToUrl(nullTarget, options => {
					options
						.WithVideoCodec("libx264")
						.WithSpeedPreset(Speed.Medium)
						.WithVideoBitrate(videoKbps)
						.ForceFormat("mp4")
						.WithCustomArgument($"-an -pass 1 -passlogfile \"{passLogPrefix}\"");

					if (!string.IsNullOrEmpty(vfFilter))
						options.WithCustomArgument($"-vf \"{vfFilter}\"");
				})
				.CancellableThrough(ct)
				.ProcessAsynchronously()
				.ConfigureAwait(false);

			await FFMpegArguments
				.FromFileInput(inputPath)
				.OutputToFile(outputPath, overwrite: true, options => {
					options
						.WithVideoCodec("libx264")
						.WithSpeedPreset(Speed.Medium)
						.WithVideoBitrate(videoKbps)
						.WithAudioCodec("aac")
						.WithAudioBitrate(audioKbps)
						.WithFastStart()
						.WithCustomArgument($"-maxrate {(int)(videoKbps * 1.2)}k -bufsize {videoKbps * 2}k -pass 2 -passlogfile \"{passLogPrefix}\"");

					if (string.IsNullOrEmpty(vfFilter))
						options.WithCustomArgument("-pix_fmt yuv420p");
					else
						options.WithCustomArgument($"-vf \"{vfFilter}\"");

					if (fixColorRange)
						options.WithCustomArgument("-color_range tv -colorspace bt709 -color_primaries bt709 -color_trc bt709");
				})
				.CancellableThrough(ct)
				.ProcessAsynchronously()
				.ConfigureAwait(false);
		} finally {
			CleanupPassLogs(passLogPrefix);
		}
	}

	private static async Task EncodeNvencFullCudaAsync(string inputPath, string outputPath, int videoKbps, int audioKbps, int? targetHeight, CancellationToken ct) {
		await FFMpegArguments
			.FromFileInput(inputPath, verifyExists: true, options => {
				options.WithCustomArgument("-hwaccel cuda -hwaccel_output_format cuda");
			})
			.OutputToFile(outputPath, overwrite: true, options => {
				options
					.WithVideoCodec("h264_nvenc")
					.WithVideoBitrate(videoKbps)
					.WithAudioCodec("aac")
					.WithAudioBitrate(audioKbps)
					.WithFastStart()
					.WithCustomArgument($"-threads 2 -maxrate {(int)(videoKbps * 1.15)}k -bufsize {videoKbps * 2}k");

				if (targetHeight.HasValue) {
					options.WithCustomArgument($"-vf \"scale_cuda=-2:{targetHeight.Value}:format=yuv420p\"");
				}
			})
			.CancellableThrough(ct)
			.ProcessAsynchronously()
			.ConfigureAwait(false);
	}

	private static void CleanupPassLogs(string prefix) {
		string? dir = Path.GetDirectoryName(prefix);
		string name = Path.GetFileName(prefix);
		if (dir == null)
			return;

		foreach (string file in Directory.GetFiles(dir, $"{name}*")) {
			try { File.Delete(file); } catch { }
		}
	}


	public static async Task<MemoryStream> CompressImageAsync(string filePath, double targetMiB = 9.5, int maxDimension = int.MaxValue, int webpQuality = 100, long maxPixels = long.MaxValue, Func<byte[], int, int, byte[]>? postProcess = null) {
		long maxBytes = (long)(targetMiB * 1024 * 1024);

		using (Image image = await Image.LoadAsync(filePath)) {
			// max dimension
			int longestSide = Math.Max(image.Width, image.Height);
			if (longestSide > maxDimension) {
				double scale = (double)maxDimension / longestSide;
				int newWidth = Math.Max(1, (int)Math.Round(image.Width * scale));
				int newHeight = Math.Max(1, (int)Math.Round(image.Height * scale));
				image.Mutate(x => x.Resize(newWidth, newHeight));
				RSEPostExtensions.Msg($"Max dimension enforced: {longestSide} --> {Math.Max(newWidth, newHeight)}");
			}

			// max pixels
			long currentPixels = (long)image.Width * image.Height;
			if (currentPixels > maxPixels) {
				double areaScale = Math.Sqrt((double)maxPixels / currentPixels);
				int newWidth = Math.Max(1, (int)Math.Floor(image.Width * areaScale));
				int newHeight = Math.Max(1, (int)Math.Floor(image.Height * areaScale));
				image.Mutate(x => x.Resize(newWidth, newHeight));
				RSEPostExtensions.Msg($"Max pixels enforced: {currentPixels} --> {maxPixels}");
			}

			using (MemoryStream compressedStream = new()) {
				async Task<byte[]> EncodeCandidateAsync(int quality) {
					compressedStream.SetLength(0);
					compressedStream.Position = 0;

					var encoder = new WebpEncoder {
						FileFormat = WebpFileFormatType.Lossy,
						Quality = quality
					};

					await image.SaveAsync(compressedStream, encoder);
					byte[] rawBytes = compressedStream.ToArray();

					return postProcess != null
						? postProcess(rawBytes, image.Width, image.Height)
						: rawBytes;
				}

				// webp quality
				int initialQuality = Math.Clamp(webpQuality, 0, 100);
				byte[] bestBytes = await EncodeCandidateAsync(initialQuality);
				if (bestBytes.Length <= maxBytes) {
					RSEPostExtensions.Msg($"Completing with requested quality: {webpQuality}");
					return new MemoryStream(bestBytes);
				}

				// still too big so thanos method until it isnt
				const int minQuality = 20;
				int low = minQuality;
				int high = initialQuality - 1;
				byte[]? validFit = null;

				while (low <= high) {
					int mid = (low + high) / 2;
					byte[] candidate = await EncodeCandidateAsync(mid);

					if (candidate.Length <= maxBytes) {
						validFit = candidate;
						low = mid + 1;
					} else {
						high = mid - 1;
					}
				}

				if (validFit != null) {
					RSEPostExtensions.Msg($"Completing with reduced quality: {initialQuality} --> {low - 1}");
					return new MemoryStream(validFit);
				}

				// dear god we're cooked, make it tinier?????
				int initialWidth = image.Width;
				int initialHeight = image.Height;

				int stepQuality = Math.Min(45, initialQuality);
				while (Math.Max(image.Width, image.Height) > 480) {
					int nextWidth = Math.Max(1, (int)(image.Width * 0.75));
					int nextHeight = Math.Max(1, (int)(image.Height * 0.75));
					image.Mutate(x => x.Resize(nextWidth, nextHeight));

					byte[] scaledCandidate = await EncodeCandidateAsync(stepQuality);
					if (scaledCandidate.Length <= maxBytes) {
						RSEPostExtensions.Msg($"Completing with reduced quality and resolution. R: {initialWidth}x{initialHeight} --> {nextWidth}x{nextHeight} Q: {initialQuality} --> {stepQuality}");
						return new MemoryStream(scaledCandidate);
					}
				}

				// HEEEELP!!! HELP MEEEE!!! HEEEEEELP
				RSEPostExtensions.Warn($"Completing with a minimum quality image, this still might not be enough to fit within {maxBytes} bytes!!!");
				return new MemoryStream(await EncodeCandidateAsync(minQuality));
			}
		}
	}

	public static byte[] InjectXmp(byte[] webpBytes, PhotoMetadata metadata, int width, int height, bool isTransparent = true) {
		Metadata rseMetadata = new(metadata);
		return ResoniteScreenshotExtensions.MetadataInjector.InjectXmp(
			webpBytes,
			RSE.ImageFormat.WEBP,
			rseMetadata,
			width,
			height,
			isTransparent
		);
	}
}
