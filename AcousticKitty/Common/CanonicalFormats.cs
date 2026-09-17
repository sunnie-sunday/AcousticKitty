// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Globalization;

namespace AcousticKitty.Common;

internal static class CharacterKey
{
	public static string Build(string name, uint homeWorldId) => $"{name}@{homeWorldId}";
}

internal static class UtcTimestamp
{
	public static string Format(DateTime utcDateTime) =>
		utcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

	public static DateTime Parse(string text) =>
		DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
			.UtcDateTime;
}

internal static class DisplayFormat
{
	public static string Timestamp(DateTime utc) =>
		utc.ToLocalTime().ToString("MMM d, yyyy, h:mm tt", CultureInfo.InvariantCulture);
}
