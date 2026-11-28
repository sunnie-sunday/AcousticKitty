// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using Dalamud.Game.ClientState.Objects.Enums;
using NativeCustomizeData = FFXIVClientStructs.FFXIV.Client.Game.Character.CustomizeData;

namespace AcousticKitty.Common;

internal static class CharacterKey
{
	public static string Build(string name, uint homeWorldId) => $"{name}@{homeWorldId}";
}

internal static class FreeCompanyIds
{
	public static uint HomeWorldId(ulong freeCompanyId) => (uint)((freeCompanyId >> 47) & 0xFFFF);
}

internal static class CustomizeEncoding
{
	public static string Encode(byte[] customize) => Convert.ToBase64String(customize);

	public static string? EncodeBlankWithGender(string? gender)
	{
		if (gender is not ("M" or "F"))
		{
			return null;
		}

		var bytes = new byte[Unsafe.SizeOf<NativeCustomizeData>()];
		bytes[(int)CustomizeIndex.Gender] = gender == "F" ? (byte)1 : (byte)0;
		return CustomizeEncoding.Encode(bytes);
	}

	public static string DecodeGender(string? customizeBase64)
	{
		if (string.IsNullOrEmpty(customizeBase64))
		{
			return string.Empty;
		}

		byte[] bytes;
		try
		{
			bytes = Convert.FromBase64String(customizeBase64);
		}
		catch (FormatException)
		{
			return string.Empty;
		}

		if (bytes.Length <= (int)CustomizeIndex.Gender)
		{
			return string.Empty;
		}

		return bytes[(int)CustomizeIndex.Gender] == 0 ? "M" : "F";
	}
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
