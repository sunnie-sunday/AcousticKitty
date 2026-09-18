// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace AcousticKitty.Echo;

#region Interface

public interface IEchoClient : IDisposable
{
	Task<EchoRegistration> RegisterAsync(
		CancellationToken cancellationToken, StringBuilder? transcript = null);

	Task<EchoSession> CreateSessionAsync(
		EchoRegistration registration, CancellationToken cancellationToken);

	Task<EchoSaveOutcome> VerifySavedAsync(
		EchoRegistration registration,
		ulong contentId,
		string characterName,
		string homeWorldName,
		ulong lodestoneId,
		CancellationToken cancellationToken,
		StringBuilder? transcript = null);
}

#endregion

#region Factory

public interface IEchoClientFactory
{
	IEchoClient Create();
}

public sealed class EchoClientFactory(Configuration configuration, IPluginLog log) : IEchoClientFactory
{
	public IEchoClient Create() => new EchoClient(configuration, log);
}

#endregion

public sealed class EchoClient : IEchoClient
{
	internal const string ApiHost = "https://echovault.gg";

	internal const string PluginVersion = "0.8.4";

	internal const int ProtocolVersion = 2;

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	private readonly Configuration configuration;
	private readonly IPluginLog log;
	private readonly HttpClient httpClient;

	public EchoClient(Configuration configuration, IPluginLog log)
	{
		this.configuration = configuration;
		this.log = log;
		this.httpClient = ProxyHttpClientFactory.Create(
			configuration.EchoProxyMode,
			configuration,
			userAgent: null,
			TimeSpan.FromSeconds(20),
			enableAutomaticDecompression: false);
	}

	#region Endpoints

	public async Task<EchoRegistration> RegisterAsync(
		CancellationToken cancellationToken, StringBuilder? transcript = null)
	{
		var body = new RegisterRequest(EchoClient.ProtocolVersion, EchoClient.PluginVersion);
		var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);

		using var request = new HttpRequestMessage(HttpMethod.Post, this.BuildUri("/v1/auth/register"))
		{
			Content = EchoClient.CreateJsonContent(bodyBytes),
		};

		using var response = await this.httpClient
			.SendAsync(request, cancellationToken)
			.ConfigureAwait(false);
		await EchoClient
			.CaptureExchangeAsync(this.log, transcript, request, bodyBytes, response, cancellationToken)
			.ConfigureAwait(false);

		var payload = await EchoClient.ReadSuccessJsonAsync<RegisterResponse>(
			response, JsonOptions, cancellationToken).ConfigureAwait(false);
		return new EchoRegistration(payload.UploaderId, payload.ApiKey, payload.HmacSecret);
	}

	public async Task<EchoSession> CreateSessionAsync(
		EchoRegistration registration, CancellationToken cancellationToken)
	{
		var body = new SessionRequest(
			EchoClient.ProtocolVersion, registration.UploaderId, registration.ApiKey);
		using var response = await this
			.SendSignedAsync("/v1/auth/session", registration, body, cancellationToken)
			.ConfigureAwait(false);
		var payload = await EchoClient.ReadSuccessJsonAsync<SessionResponse>(
			response, JsonOptions, cancellationToken).ConfigureAwait(false);
		return new EchoSession(payload.Token, payload.ExpiresAt.UtcDateTime, payload.Tier);
	}

	public async Task<EchoSaveOutcome> VerifySavedAsync(
		EchoRegistration registration,
		ulong contentId,
		string characterName,
		string homeWorldName,
		ulong lodestoneId,
		CancellationToken cancellationToken,
		StringBuilder? transcript = null)
	{
		var body = new SavedRequest(
			EchoClient.ProtocolVersion,
			lodestoneId.ToString(CultureInfo.InvariantCulture),
			characterName,
			homeWorldName,
			contentId.ToString(CultureInfo.InvariantCulture));
		using var response = await this
			.SendSignedAsync("/v1/auth/verify/start", registration, body, cancellationToken, transcript)
			.ConfigureAwait(false);

		if (response.StatusCode == HttpStatusCode.Conflict)
		{
			return EchoSaveOutcome.AlreadyPinned;
		}

		if (!response.IsSuccessStatusCode)
		{
			var reason = await EchoClient.ReadErrorAsync(response, cancellationToken)
				.ConfigureAwait(false);
			throw new EchoException($"Echo \"saved\" request failed: {reason}");
		}

		return EchoSaveOutcome.Saved;
	}

	#endregion

	#region Signing

	private async Task<HttpResponseMessage> SendSignedAsync<TBody>(
		string path,
		EchoRegistration registration,
		TBody body,
		CancellationToken cancellationToken,
		StringBuilder? transcript = null)
	{
		var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
		var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
		var nonce = Guid.NewGuid().ToString("N");
		var secret = Convert.FromBase64String(registration.HmacSecretBase64);
		var signature = EchoClient.ComputeSignature("POST", path, bodyBytes, timestamp, nonce, secret);

		using var request = new HttpRequestMessage(HttpMethod.Post, this.BuildUri(path))
		{
			Content = EchoClient.CreateJsonContent(bodyBytes),
		};
		request.Headers.Add("X-Echo-KeyId", registration.UploaderId);
		request.Headers.Add("X-Echo-Timestamp", timestamp);
		request.Headers.Add("X-Echo-Nonce", nonce);
		request.Headers.Add("X-Echo-Signature", signature);

		var response = await this.httpClient
			.SendAsync(request, cancellationToken)
			.ConfigureAwait(false);
		await EchoClient
			.CaptureExchangeAsync(this.log, transcript, request, bodyBytes, response, cancellationToken)
			.ConfigureAwait(false);

		if (response.StatusCode == HttpStatusCode.TooManyRequests)
		{
			var retryAfter = EchoClient.ReadRetryAfter(response);
			response.Dispose();
			throw new EchoRateLimitedException(retryAfter);
		}

		return response;
	}

	private static string ComputeSignature(
		string method, string path, byte[] payload, string timestamp, string nonce, byte[] secret)
	{
		var payloadHash = Convert.ToHexStringLower(SHA256.HashData(payload));
		var canonical = string.Join('\n', method, path, payloadHash, timestamp, nonce);
		var hash = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(canonical));
		return Convert.ToHexStringLower(hash);
	}

	#endregion

	#region Response Handling

	private static async Task<T> ReadSuccessJsonAsync<T>(
		HttpResponseMessage response, JsonSerializerOptions options, CancellationToken cancellationToken)
	{
		if (response.StatusCode == HttpStatusCode.TooManyRequests)
		{
			throw new EchoRateLimitedException(EchoClient.ReadRetryAfter(response));
		}

		if (!response.IsSuccessStatusCode)
		{
			var reason = await EchoClient.ReadErrorAsync(response, cancellationToken)
				.ConfigureAwait(false);
			throw new EchoException($"Echo request failed ({(int)response.StatusCode}): {reason}");
		}

		var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		var result = await JsonSerializer.DeserializeAsync<T>(stream, options, cancellationToken)
			.ConfigureAwait(false);
		return result ?? throw new EchoException("Echo response body was empty.");
	}

	private static async Task<string> ReadErrorAsync(
		HttpResponseMessage response, CancellationToken cancellationToken)
	{
		try
		{
			var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			using var document = JsonDocument.Parse(text);
			if (document.RootElement.TryGetProperty("description", out var description))
			{
				return description.GetString() ?? response.ReasonPhrase ??
					EchoClient.DescribeNonStandardStatus(response.StatusCode) ?? "unknown error";
			}

			if (document.RootElement.TryGetProperty("error", out var error))
			{
				return error.GetString() ?? response.ReasonPhrase ??
					EchoClient.DescribeNonStandardStatus(response.StatusCode) ?? "unknown error";
			}

			return text;
		}
		catch (JsonException)
		{
			return response.ReasonPhrase ??
				EchoClient.DescribeNonStandardStatus(response.StatusCode) ??
				$"HTTP {(int)response.StatusCode}";
		}
	}

	private static string? DescribeNonStandardStatus(HttpStatusCode statusCode) =>
		(int)statusCode switch
		{
			520 => "Cloudflare: Web Server Returned an Unknown Error",
			521 => "Cloudflare: Web Server Is Down",
			522 => "Cloudflare: Connection Timed Out",
			523 => "Cloudflare: Origin Is Unreachable",
			524 => "Cloudflare: A Timeout Occurred",
			525 => "Cloudflare: SSL Handshake Failed",
			526 => "Cloudflare: Invalid SSL Certificate",
			530 => "Cloudflare: Origin Unavailable",
			_ => null,
		};

	private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
	{
		var retryAfter = response.Headers.RetryAfter;
		if (retryAfter == null)
		{
			return null;
		}

		if (retryAfter.Delta is { } delta)
		{
			return delta;
		}

		if (retryAfter.Date is { } date)
		{
			var remaining = date - DateTimeOffset.UtcNow;
			return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
		}

		return null;
	}

	#endregion

	#region Exchange Logging

	private static readonly string[] ApprovedResponseHeaderNames =
		{ "Date", "Content-Type", "Transfer-Encoding", "Connection", "Server" };

	private static async Task CaptureExchangeAsync(
		IPluginLog log,
		StringBuilder? transcript,
		HttpRequestMessage request,
		byte[] requestBodyBytes,
		HttpResponseMessage response,
		CancellationToken cancellationToken)
	{
		var responseBodyBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken)
			.ConfigureAwait(false);

		EchoClient.LogExchange(log, request, requestBodyBytes, response, responseBodyBytes);

		if (transcript != null)
		{
			if (transcript.Length > 0)
			{
				transcript.Append('\n');
			}

			EchoClient.AppendRequestBlock(transcript, request, requestBodyBytes);
			transcript.Append('\n');
			EchoClient.AppendResponseBlock(transcript, response, responseBodyBytes);
		}

		var originalContentType = response.Content.Headers.ContentType;
		response.Content = new ByteArrayContent(responseBodyBytes);
		if (originalContentType != null)
		{
			response.Content.Headers.ContentType = originalContentType;
		}
	}

	private static void LogExchange(
		IPluginLog log,
		HttpRequestMessage request,
		byte[] requestBodyBytes,
		HttpResponseMessage response,
		byte[] responseBodyBytes) =>
		log.Verbose(
			$"Echo {request.Method} {request.RequestUri!.PathAndQuery} -> " +
			$"{(int)response.StatusCode} {response.ReasonPhrase}\n" +
			$"Request body: {Encoding.UTF8.GetString(requestBodyBytes)}\n" +
			$"Response body: {Encoding.UTF8.GetString(responseBodyBytes)}");

	private static void AppendRequestBlock(
		StringBuilder sb, HttpRequestMessage request, byte[] bodyBytes)
	{
		sb.Append(request.Method).Append(' ').Append(request.RequestUri!.PathAndQuery)
			.Append(" HTTP/1.1\n");
		sb.Append("Host: ").Append(request.RequestUri!.Authority).Append('\n');

		foreach (var header in request.Headers)
		{
			sb.Append(header.Key).Append(": ").Append(string.Join(", ", header.Value)).Append('\n');
		}

		if (request.Content?.Headers.ContentType is { } contentType)
		{
			sb.Append("Content-Type: ").Append(contentType).Append('\n');
		}

		sb.Append("Content-Length: ").Append(bodyBytes.Length).Append('\n');
		sb.Append('\n');
		sb.Append(EchoClient.PrettyPrintFlatJson(bodyBytes)).Append('\n');
	}

	private static void AppendResponseBlock(
		StringBuilder sb, HttpResponseMessage response, byte[] bodyBytes)
	{
		sb.Append("HTTP/1.1 ").Append((int)response.StatusCode).Append(' ')
			.Append(response.ReasonPhrase).Append('\n');

		foreach (var name in EchoClient.ApprovedResponseHeaderNames)
		{
			if (!response.Headers.TryGetValues(name, out var values))
			{
				response.Content.Headers.TryGetValues(name, out values);
			}

			if (values != null)
			{
				sb.Append(name).Append(": ").Append(string.Join(", ", values)).Append('\n');
			}
		}

		sb.Append('\n');
		if (bodyBytes.Length > 0)
		{
			sb.Append(EchoClient.PrettyPrintFlatJson(bodyBytes)).Append('\n');
		}
	}

	private static string PrettyPrintFlatJson(byte[] utf8Json)
	{
		using var document = JsonDocument.Parse(utf8Json);
		var properties = new List<JsonProperty>();
		foreach (var property in document.RootElement.EnumerateObject())
		{
			properties.Add(property);
		}

		if (properties.Count == 0)
		{
			return "{}";
		}

		var sb = new StringBuilder();
		sb.Append("{\n");
		for (var i = 0; i < properties.Count; i++)
		{
			sb.Append('\t').Append('"').Append(properties[i].Name).Append("\": ")
				.Append(properties[i].Value.GetRawText());
			sb.Append(i < properties.Count - 1 ? ",\n" : "\n");
		}

		sb.Append('}');
		return sb.ToString();
	}

	#endregion

	#region Plumbing

	private Uri BuildUri(string path)
	{
		var host = EchoClient.ApiHost;
		var baseUrl = host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
			|| host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
			? host
			: $"http://{host}";
		return new Uri($"{baseUrl.TrimEnd('/')}{path}");
	}

	private static ByteArrayContent CreateJsonContent(byte[] bodyBytes)
	{
		var content = new ByteArrayContent(bodyBytes);
		content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
		return content;
	}

	public void Dispose() => this.httpClient.Dispose();

	#endregion

	#region Wire Types

	private sealed record RegisterRequest(int ProtocolVersion, string PluginVersion);

	private sealed record RegisterResponse(string UploaderId, string ApiKey, string HmacSecret);

	private sealed record SessionRequest(int ProtocolVersion, string UploaderId, string ApiKey);

	private sealed record SessionResponse(string Token, DateTimeOffset ExpiresAt, string Tier);

	private sealed record SavedRequest(
		int ProtocolVersion,
		string LodestoneId,
		string CharacterName,
		string HomeWorldName,
		string ContentId);

	#endregion
}

#region Proxy HTTP Client Factory

internal static class ProxyHttpClientFactory
{
	public static HttpClient Create(
		ProxyMode mode,
		Configuration configuration,
		string? userAgent,
		TimeSpan timeout,
		bool enableAutomaticDecompression = true)
	{
		var handler = new SocketsHttpHandler
		{
			AutomaticDecompression = enableAutomaticDecompression
				? DecompressionMethods.All
				: DecompressionMethods.None,
			ConnectTimeout = TimeSpan.FromSeconds(15),
		};

		if (mode == ProxyMode.Socks5 && !string.IsNullOrWhiteSpace(configuration.EchoProxyHost))
		{
			var proxyUri =
				new Uri($"socks5://{configuration.EchoProxyHost}:{configuration.EchoProxyPort}");
			handler.Proxy = new WebProxy(proxyUri)
			{
				Credentials = string.IsNullOrWhiteSpace(configuration.EchoProxyUsername)
					? null
					: new NetworkCredential(
						configuration.EchoProxyUsername,
						configuration.EchoProxyPassword),
			};
		}

		var client = new HttpClient(handler) { Timeout = timeout };
		if (!string.IsNullOrWhiteSpace(userAgent))
		{
			client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
		}

		return client;
	}
}

#endregion
