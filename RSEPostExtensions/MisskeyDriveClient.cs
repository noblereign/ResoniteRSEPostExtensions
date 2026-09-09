using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace RSEPostExtensions;

public class MisskeyDriveClient {
	private static readonly HttpClient _http = new();

	public record DriveFolder(
		[property: JsonPropertyName("id")] string Id,
		[property: JsonPropertyName("createdAt")] string CreatedAt,
		[property: JsonPropertyName("name")] string Name,
		[property: JsonPropertyName("parentId")] string? ParentId
	);

	public record DriveFile(
		[property: JsonPropertyName("id")] string Id,
		[property: JsonPropertyName("createdAt")] string CreatedAt,
		[property: JsonPropertyName("name")] string Name,
		[property: JsonPropertyName("type")] string Type,
		[property: JsonPropertyName("url")] string Url,
		[property: JsonPropertyName("folderId")] string? FolderId
	);
	public record DriveUsageResponse(
		[property: JsonPropertyName("capacity")] long Capacity,
		[property: JsonPropertyName("usage")] long Usage
	) {
		public string ToHumanReadableString() {
			string usageStr = FormatBytes(Usage);
			string capacityStr = FormatBytes(Capacity);

			double percentage = Capacity > 0 ? (Usage / (double)Capacity) * 100 : 0;

			return $"{usageStr} / {capacityStr} used ({percentage:F1}%)";
		}

		private static string FormatBytes(long bytes) {
			string[] sizes = { "B", "KiB", "MiB", "GiB", "TiB" };
			double len = bytes;
			int order = 0;
			while (len >= 1024 && order < sizes.Length - 1) {
				order++;
				len /= 1024;
			}
			return $"{len:0.##} {sizes[order]}";
		}
	};

	public static async Task<DriveFolder?> GetOrCreateFolderAsync(
		Uri instanceUrl,
		string token,
		string folderName,
		string? parentId = null) {

		Uri baseUri = new Uri(instanceUrl.AbsoluteUri.TrimEnd('/') + "/");
		Uri findEndpoint = new Uri(baseUri, "api/drive/folders/find");
		Uri createEndpoint = new Uri(baseUri, "api/drive/folders/create");

		var payload = new { name = folderName, parentId };

		using var findReq = new HttpRequestMessage(HttpMethod.Post, findEndpoint);
		findReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		findReq.Content = JsonContent.Create(payload);

		using var findRes = await _http.SendAsync(findReq);
		await EnsureSuccessWithBodyAsync(findRes);

		var folders = await findRes.Content.ReadFromJsonAsync<DriveFolder[]>();
		if (folders?.FirstOrDefault() is { } existing) return existing;

		using var createReq = new HttpRequestMessage(HttpMethod.Post, createEndpoint);
		createReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		createReq.Content = JsonContent.Create(payload);

		using var createRes = await _http.SendAsync(createReq);
		await EnsureSuccessWithBodyAsync(createRes);

		return await createRes.Content.ReadFromJsonAsync<DriveFolder>();
	}

	public static async Task<DriveFile?> UploadFileAsync(
		Uri instanceUrl,
		string token,
		string localFilePath,
		string? folderId = null,
		string? name = null,
		string? comment = null,
		bool isSensitive = false,
		bool force = false) {

		Uri baseUri = new Uri(instanceUrl.AbsoluteUri.TrimEnd('/') + "/");
		Uri uploadEndpoint = new Uri(baseUri, "api/drive/files/create");

		using var request = new HttpRequestMessage(HttpMethod.Post, uploadEndpoint);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

		using var form = new MultipartFormDataContent();

		var fileStream = File.OpenRead(localFilePath);
		var fileContent = new StreamContent(fileStream);

		string mimeType = Util.GuessImageMimeType(fileStream);
		fileStream.Position = 0;

		fileContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);

		string fileName = name ?? Path.GetFileName(localFilePath);
		form.Add(fileContent, "file", fileName);

		if (!string.IsNullOrEmpty(folderId))
			form.Add(new StringContent(folderId), "folderId");

		if (!string.IsNullOrEmpty(name))
			form.Add(new StringContent(name), "name");

		if (!string.IsNullOrEmpty(comment))
			form.Add(new StringContent(comment), "comment");

		if (isSensitive)
			form.Add(new StringContent("true"), "isSensitive");

		if (force)
			form.Add(new StringContent("true"), "force");

		request.Content = form;

		using var response = await _http.SendAsync(request);
		await EnsureSuccessWithBodyAsync(response);

		return await response.Content.ReadFromJsonAsync<DriveFile>();
	}
	
	public static async Task<string> GetHumanReadableDriveUsageAsync(Uri instanceUrl, string token) {
		Uri baseUri = new Uri(instanceUrl.AbsoluteUri.TrimEnd('/') + "/");
		Uri endpoint = new Uri(baseUri, "api/drive");

		using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		request.Content = JsonContent.Create(new { });

		using var response = await _http.SendAsync(request);
		await EnsureSuccessWithBodyAsync(response);

		var usageData = await response.Content.ReadFromJsonAsync<DriveUsageResponse>();

		return usageData?.ToHumanReadableString() ?? "Unable to parse drive usage.";
	}


	private static async Task EnsureSuccessWithBodyAsync(HttpResponseMessage response) {
		if (!response.IsSuccessStatusCode) {
			string errorBody = await response.Content.ReadAsStringAsync();
			throw new HttpRequestException($"Misskey API Error ({response.StatusCode}): {errorBody}");
		}
	}
}
