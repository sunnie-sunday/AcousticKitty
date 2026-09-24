// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using AcousticKitty.Lodestone;

namespace AcousticKitty.Echo;

public enum ProxyMode
{
	Direct,
	Socks5,
}

public enum EchoSaveOutcome
{
	Saved,
	AlreadyPinned,
}

public enum EchoQueueMode
{
	Auto,
	Manual,
}

public sealed record PendingMatch(
	ulong ContentId, string CharacterName, uint HomeWorldId, ulong LodestoneId);

public sealed record PinnedCharacter(
	ulong LodestoneId,
	string Name,
	uint HomeWorldId,
	LodestoneProfile? Profile,
	DateTime? FetchedAtUtc,
	DateTime PinnedAtUtc);

public sealed record PinnedProfileSnapshot(LodestoneProfile Profile, DateTime? FetchedAtUtc);

public sealed record EchoRegistration(string UploaderId, string ApiKey, string HmacSecretBase64);

public sealed record EchoSession(string Token, DateTime ExpiresAtUtc, string Tier);

public sealed class EchoRateLimitedException(TimeSpan? retryAfter)
	: Exception("The Echo API returned 429 (rate limited).")
{
	public TimeSpan? RetryAfter { get; } = retryAfter;
}

public sealed class EchoException(string message) : Exception(message);
