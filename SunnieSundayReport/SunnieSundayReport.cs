// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using SQLite;

namespace AcousticKitty.Echo;

internal static class SunnieSundayReport
{
	private const string FrontMatterFence = "---";

	public static int Main(string[] args)
	{
		if (args.Length != 2)
		{
			Console.Error.WriteLine(
				"Usage: SunnieSundayReport <characters directory> <output .db3>");
			return 2;
		}

		var rows = Directory.EnumerateFiles(args[0], "*.md")
			.Where(path => !Path.GetFileName(path).StartsWith('_'))
			.Select(SunnieSundayReport.ReadCharacter)
			.OrderBy(row => row.LodestoneId)
			.ToList();
		SunnieSundayReport.Write(rows, args[1]);
		Console.WriteLine($"SunnieSundayReport: wrote {rows.Count} pin(s) to {args[1]}.");
		return 0;
	}

	private static PinRow ReadCharacter(string path)
	{
		var fields = SunnieSundayReport.ReadFrontMatter(path, File.ReadAllText(path));

		return new PinRow
		{
			LodestoneId = SunnieSundayReport.ReadNumber<long>(
				path,
				SunnieSundayReport.ReadString(path, fields, "lodestone_id"),
				"a Lodestone ID"),
			ContentId =
				SunnieSundayReport.ReadOptionalString(path, fields, "content_id") is { } contentId
					? SunnieSundayReport.ReadNumber<long>(path, contentId, "a ContentID")
					: null,
			Name = SunnieSundayReport.ReadString(path, fields, "name"),
			HomeWorld = SunnieSundayReport.ReadString(path, fields, "world"),
			AvatarUrlHash = SunnieSundayReport.ReadString(path, fields, "avatar"),
			FreeCompanyId =
				SunnieSundayReport.ReadOptionalString(path, fields, "fc_lodestone_id") is { } fcId
					? unchecked((long)SunnieSundayReport.ReadNumber<ulong>(
						path, fcId, "a Free Company ID"))
					: null,
			FreeCompanyName = SunnieSundayReport.ReadOptionalString(path, fields, "fc_name"),
		};
	}

	private static T ReadNumber<T>(string path, string text, string description)
		where T : INumber<T>
	{
		if (!T.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
			number <= T.Zero)
		{
			throw new InvalidDataException($"{path}: '{text}' isn't {description}.");
		}

		return number;
	}

	private static Dictionary<string, string> ReadFrontMatter(string path, string text)
	{
		using var reader = new StringReader(text);
		if (reader.ReadLine()?.Trim() != FrontMatterFence)
		{
			throw new InvalidDataException($"{path}: missing front matter.");
		}

		var fields = new Dictionary<string, string>(StringComparer.Ordinal);
		while (reader.ReadLine() is { } line)
		{
			if (line.Trim() == FrontMatterFence)
			{
				return fields;
			}

			var separator = line.IndexOf(':');
			if (separator > 0)
			{
				fields[line[..separator].Trim()] = line[(separator + 1)..].Trim();
			}
		}

		throw new InvalidDataException($"{path}: unterminated front matter.");
	}

	private static string ReadString(
		string path, IReadOnlyDictionary<string, string> fields, string key) =>
		SunnieSundayReport.ReadOptionalString(path, fields, key) ??
		throw new InvalidDataException($"{path}: missing '{key}'.");

	private static string? ReadOptionalString(
		string path, IReadOnlyDictionary<string, string> fields, string key)
	{
		if (!fields.TryGetValue(key, out var raw))
		{
			return null;
		}

		var value = raw.Trim('"');
		if (string.IsNullOrWhiteSpace(value))
		{
			throw new InvalidDataException($"{path}: '{key}' is empty.");
		}

		return value;
	}

	private static void Write(IEnumerable<PinRow> rows, string databasePath)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
		File.Delete(databasePath);
		using var connection = new SQLiteConnection(databasePath);
		connection.CreateTable<PinRow>();
		connection.InsertAll(rows);
	}
}
