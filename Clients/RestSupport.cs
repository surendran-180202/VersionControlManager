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

    public const string UserAgent = "VersionControlManager/1.0";

    #endregion

    #region Public Methods

    /// <summary>
    /// Builds an HTTP Basic credential. Both GitHub and Azure DevOps accept a personal
    /// access token in the password position of Basic auth.
    /// </summary>
    public static string BasicCredential(string userName, string secret) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{secret}"));

    public static string BasicHeaderValue(string userName, string secret) =>
        $"Basic {BasicCredential(userName, secret)}";

    public static HttpClient CreateClient(string userName, string secret)
    {
        HttpClient client = new HttpClient(new HttpClientHandler
        {
            // We authenticate explicitly on every request; following a redirect that strips
            // or forwards our header would only make failures harder to read.
            AllowAutoRedirect = true,
        })
        {
            Timeout = TimeSpan.FromSeconds(100),
        };

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", BasicCredential(userName, secret));
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        return client;
    }

    /// <summary>
    /// Sends a request and returns the parsed JSON body, mapping the failure modes each
    /// service actually produces onto explainable errors.
    /// </summary>
    public static async Task<JsonDocument> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        string serviceName,
        ExitCode failureCode,
        CancellationToken cancellationToken)
    {
        return await ExecuteAsync(client, request, serviceName, failureCode, false, cancellationToken)
               ?? throw new MigrationException(failureCode, $"{serviceName} returned no content.");
    }

    /// <summary>As <see cref="SendAsync"/>, but returns null for 404 instead of failing.</summary>
    public static Task<JsonDocument?> SendAllowingNotFoundAsync(
        HttpClient client,
        HttpRequestMessage request,
        string serviceName,
        ExitCode failureCode,
        CancellationToken cancellationToken) =>
        ExecuteAsync(client, request, serviceName, failureCode, true, cancellationToken);

    public static HttpRequestMessage Json(HttpMethod method, string url, object? payload = null)
    {
        HttpRequestMessage request = new HttpRequestMessage(method, url);

        if (payload is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");
        }

        return request;
    }

    public static string? StringOrNull(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static string RequiredString(JsonElement element, string propertyName, string serviceName) =>
        StringOrNull(element, propertyName)
        ?? throw new MigrationException(
            ExitCode.TargetError,
            $"{serviceName} response did not include '{propertyName}'.");

    #endregion

    #region Private Methods

    /// <summary>
    /// The status code is inspected directly rather than matched against the error text:
    /// Azure DevOps can answer a missing resource with an HTML page, and string-matching
    /// "404" in a message would confuse that with a genuine absence.
    /// </summary>
    private static async Task<JsonDocument?> ExecuteAsync(
        HttpClient client,
        HttpRequestMessage request,
        string serviceName,
        ExitCode failureCode,
        bool allowNotFound,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new MigrationException(
                failureCode,
                $"Could not reach {serviceName}: {ex.Message}",
                "Check the URL, your network connection, and any proxy settings.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MigrationException(failureCode, $"The request to {serviceName} timed out.");
        }

        using (response)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new MigrationException(
                    ExitCode.AuthenticationError,
                    $"{serviceName} rejected the credentials ({(int)response.StatusCode} {response.StatusCode}).",
                    DescribeAuthFailure(serviceName, body));
            }

            // Checked before the JSON test below, so an HTML 404 is still read as "absent"
            // rather than mistaken for a sign-in page.
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            // Azure DevOps answers an unauthenticated API call with a sign-in page and a 2xx
            // status instead of a 401, so a non-JSON body is the real signal.
            if (!LooksLikeJson(body))
            {
                throw new MigrationException(
                    ExitCode.AuthenticationError,
                    $"{serviceName} returned a sign-in page instead of data ({(int)response.StatusCode}).",
                    DescribeAuthFailure(serviceName, body));
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new MigrationException(
                    failureCode,
                    $"{serviceName} returned {(int)response.StatusCode} {response.StatusCode}: {ExtractMessage(body)}");
            }

            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new MigrationException(
                    failureCode,
                    $"Could not read the response from {serviceName}: {ex.Message}");
            }
        }
    }

    private static bool LooksLikeJson(string body)
    {
        ReadOnlySpan<char> trimmed = body.AsSpan().TrimStart();

        return trimmed.Length == 0 || trimmed[0] is '{' or '[';
    }

    /// <summary>Pulls the human-readable message out of a GitHub or Azure DevOps error body.</summary>
    private static string ExtractMessage(string body)
    {
        if (!LooksLikeJson(body) || body.AsSpan().TrimStart().Length == 0)
        {
            return "no details returned";
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);

            return StringOrNull(document.RootElement, "message")
                ?? StringOrNull(document.RootElement, "value")
                ?? body.Trim();
        }
        catch (JsonException)
        {
            return body.Trim();
        }
    }

    private static string DescribeAuthFailure(string serviceName, string body)
    {
        if (body.Contains("SAML", StringComparison.OrdinalIgnoreCase)
            || body.Contains("sso", StringComparison.OrdinalIgnoreCase))
        {
            return "The token may need to be authorised for your organisation's SSO.";
        }

        return serviceName.StartsWith("GitHub", StringComparison.OrdinalIgnoreCase)
            ? "GitHub requires a personal access token (not an account password), with 'repo' scope."
            : "Azure DevOps requires a personal access token with Code (read, write, and manage) scope.";
    }

    #endregion
}
