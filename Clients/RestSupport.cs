using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VersionControlManager.Migration;

namespace VersionControlManager.Clients;

/// <summary>Shared plumbing for the two REST clients.</summary>
internal static class RestSupport
{
	#region Constants
	public const string USER_AGENT = "VersionControlManager/1.0";
	#endregion

	#region Publics
	/// <summary>
	/// Builds an HTTP Basic credential. Both GitHub and Azure DevOps accept a personal
	/// access token in the password position of Basic auth.
	/// </summary>
	public static string BasicCredential(string strUserName, string strSecret)
	{
		return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{strUserName}:{strSecret}"));
	}

	public static string BasicHeaderValue(string strUserName, string strSecret)
	{
		return $"Basic {BasicCredential(strUserName, strSecret)}";
	}

	public static HttpClient CreateClient(string strUserName, string strSecret)
	{
		HttpClient client = new(new HttpClientHandler
		{
			// We authenticate explicitly on every request; following a redirect that strips
			// or forwards our header would only make failures harder to read.
			AllowAutoRedirect = true,
		})
		{
			Timeout = TimeSpan.FromSeconds(100),
		};

		client.DefaultRequestHeaders.Authorization =
			new AuthenticationHeaderValue("Basic", BasicCredential(strUserName, strSecret));
		client.DefaultRequestHeaders.UserAgent.ParseAdd(USER_AGENT);

		return client;
	}

	/// <summary>
	/// Sends a request and returns the parsed JSON body, mapping the failure modes each
	/// service actually produces onto explainable errors.
	/// </summary>
	public static async Task<JsonDocument> SendAsync(
		HttpClient client,
		HttpRequestMessage request,
		string strServiceName,
		ExitCode failureCode,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync(client, request, strServiceName, failureCode, false, cancellationToken)
			   ?? throw new MigrationException(failureCode, $"{strServiceName} returned no content.");
	}

	/// <summary>As <see cref="SendAsync"/>, but returns null for 404 instead of failing.</summary>
	public static Task<JsonDocument?> SendAllowingNotFoundAsync(
		HttpClient client,
		HttpRequestMessage request,
		string strServiceName,
		ExitCode failureCode,
		CancellationToken cancellationToken)
	{
		return ExecuteAsync(client, request, strServiceName, failureCode, true, cancellationToken);
	}

	public static HttpRequestMessage Json(HttpMethod method, string strUrl, object? oPayload = null)
	{
		HttpRequestMessage request = new(method, strUrl);

		if(oPayload is not null)
		{
			request.Content = new StringContent(
				JsonSerializer.Serialize(oPayload),
				Encoding.UTF8,
				"application/json");
		}

		return request;
	}

	public static string? StringOrNull(JsonElement element, string strPropertyName)
	{
		return element.TryGetProperty(strPropertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;
	}

	public static string RequiredString(JsonElement element, string strPropertyName, string strServiceName)
	{
		return StringOrNull(element, strPropertyName)
		?? throw new MigrationException(
			ExitCode.TargetError,
			$"{strServiceName} response did not include '{strPropertyName}'.");
	}
	#endregion

	#region Privates
	/// <summary>
	/// The status code is inspected directly rather than matched against the error text:
	/// Azure DevOps can answer a missing resource with an HTML page, and string-matching
	/// "404" in a message would confuse that with a genuine absence.
	/// </summary>
	private static async Task<JsonDocument?> ExecuteAsync(
		HttpClient client,
		HttpRequestMessage request,
		string strServiceName,
		ExitCode failureCode,
		bool bAllowNotFound,
		CancellationToken cancellationToken)
	{
		HttpResponseMessage response;

		try
		{
			response = await client.SendAsync(request, cancellationToken);
		}
		catch(HttpRequestException ex)
		{
			throw new MigrationException(
				failureCode,
				$"Could not reach {strServiceName}: {ex.Message}",
				"Check the URL, your network connection, and any proxy settings.");
		}
		catch(TaskCanceledException) when(!cancellationToken.IsCancellationRequested)
		{
			throw new MigrationException(failureCode, $"The request to {strServiceName} timed out.");
		}

		using(response)
		{
			string strBody = await response.Content.ReadAsStringAsync(cancellationToken);

			if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
			{
				throw new MigrationException(
					ExitCode.AuthenticationError,
					$"{strServiceName} rejected the credentials ({(int)response.StatusCode} {response.StatusCode}).",
					DescribeAuthFailure(strServiceName, strBody));
			}

			// Checked before the JSON test below, so an HTML 404 is still read as "absent"
			// rather than mistaken for a sign-in page.
			if(bAllowNotFound && response.StatusCode == HttpStatusCode.NotFound)
			{
				return null;
			}

			// Azure DevOps answers an unauthenticated API call with a sign-in page and a 2xx
			// status instead of a 401, so a non-JSON body is the real signal.
			if(!LooksLikeJson(strBody))
			{
				throw new MigrationException(
					ExitCode.AuthenticationError,
					$"{strServiceName} returned a sign-in page instead of data ({(int)response.StatusCode}).",
					DescribeAuthFailure(strServiceName, strBody));
			}

			if(!response.IsSuccessStatusCode)
			{
				throw new MigrationException(
					failureCode,
					$"{strServiceName} returned {(int)response.StatusCode} {response.StatusCode}: {ExtractMessage(strBody)}");
			}

			try
			{
				return JsonDocument.Parse(strBody);
			}
			catch(JsonException ex)
			{
				throw new MigrationException(
					failureCode,
					$"Could not read the response from {strServiceName}: {ex.Message}");
			}
		}
	}

	private static bool LooksLikeJson(string strBody)
	{
		ReadOnlySpan<char> trimmed = strBody.AsSpan().TrimStart();

		return trimmed.Length == 0 || trimmed[0] is '{' or '[';
	}

	/// <summary>Pulls the human-readable message out of a GitHub or Azure DevOps error body.</summary>
	private static string ExtractMessage(string strBody)
	{
		if(!LooksLikeJson(strBody) || strBody.AsSpan().TrimStart().Length == 0) return "no details returned";

		try
		{
			using JsonDocument document = JsonDocument.Parse(strBody);

			return StringOrNull(document.RootElement, "message")
				?? StringOrNull(document.RootElement, "value")
				?? strBody.Trim();
		}
		catch(JsonException)
		{
			return strBody.Trim();
		}
	}

	private static string DescribeAuthFailure(string strServiceName, string strBody)
	{
		if(strBody.Contains("SAML", StringComparison.OrdinalIgnoreCase)
			|| strBody.Contains("sso", StringComparison.OrdinalIgnoreCase))
		{
			return "The token may need to be authorised for your organisation's SSO.";
		}

		return strServiceName.StartsWith("GitHub", StringComparison.OrdinalIgnoreCase)
			? "GitHub requires a personal access token (not an account password), with 'repo' scope."
			: "Azure DevOps requires a personal access token with Code (read, write, and manage) scope.";
	}
	#endregion
}
