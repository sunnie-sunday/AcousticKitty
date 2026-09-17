// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AcousticKitty.Common;
using Json.Schema;

namespace AcousticKitty.Character;

internal static class PcsFile
{
	public sealed record ImportedCharacter(PlayerLocalData Data, DateTime TimeSeenUtc);

	private static JsonSchema? cachedSchema;

	public static IReadOnlyList<ImportedCharacter> Load(string path)
	{
		Stream stream;
		try
		{
			stream = File.OpenRead(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			throw new PcsFileException($"Could not read \"{path}\": {ex.Message}");
		}

		using (stream)
		using (var document = ParseJson(stream))
		{
			ValidateAgainstSchema(document);

			var rows = document.RootElement.Deserialize<List<RawRow>>()
				?? throw new PcsFileException("The file did not contain a JSON array of characters.");

			return rows.Select(ToImportedCharacter).ToArray();
		}
	}

	private static JsonDocument ParseJson(Stream stream)
	{
		try
		{
			return JsonDocument.Parse(stream);
		}
		catch (JsonException ex)
		{
			throw new PcsFileException($"Not valid JSON: {ex.Message}");
		}
	}

	private static void ValidateAgainstSchema(JsonDocument document)
	{
		var results = GetSchema().Evaluate(
			document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
		if (!results.IsValid)
		{
			throw new PcsFileException(BuildErrorMessage(results));
		}
	}

	private static string BuildErrorMessage(EvaluationResults results)
	{
		var messages = new List<string>();
		CollectErrors(results, messages);
		return messages.Count == 0
			? "The file does not match pcs.schema.json."
			: string.Join(" ", messages.Distinct().Take(5));
	}

	private static void CollectErrors(EvaluationResults results, List<string> messages)
	{
		if (results.Errors != null)
		{
			messages.AddRange(results.Errors.Values);
		}

		foreach (var detail in results.Details ?? [])
		{
			CollectErrors(detail, messages);
		}
	}

	private static JsonSchema GetSchema() => cachedSchema ??= LoadSchemaFromDisk();

	private static JsonSchema LoadSchemaFromDisk()
	{
		var schemaPath = Path.Combine(
			Plugin.PluginInterface.AssemblyLocation.DirectoryName!, "pcs.schema.json");
		return JsonSchema.FromText(File.ReadAllText(schemaPath));
	}

	private static ImportedCharacter ToImportedCharacter(RawRow row) => new(
		new PlayerLocalData(
			row.Name,
			row.HomeWorldId,
			row.ContentId,
			row.JobId,
			row.Level,
			string.IsNullOrEmpty(row.FCTag) ? null : row.FCTag,
			row.TitleId,
			row.TerritoryId,
			row.CurrentWorldId,
			row.Gender),
		row.TimeSeenUtc.UtcDateTime);

	private sealed record RawRow(
		ulong ContentId,
		string Name,
		uint HomeWorldId,
		uint CurrentWorldId,
		uint TerritoryId,
		uint JobId,
		int Level,
		string FCTag,
		string Gender,
		DateTimeOffset TimeSeenUtc,
		uint TitleId);
}

internal sealed class PcsFileException(string message) : Exception(message);

public sealed partial class CharacterDirectory
{
	public void PrioritizeForLookup(IReadOnlyList<ulong> contentIds, DateTime priorityAtUtc)
	{
		if (contentIds.Count == 0)
		{
			return;
		}

		lock (this.gate)
		{
			this.connection.RunInTransaction(() =>
			{
				foreach (var contentId in contentIds)
				{
					var row = this.connection.Find<KnownCharacterRow>((long)contentId);
					if (row == null)
					{
						continue;
					}

					row.LookupState = (int)NearbyLookupState.Pending;
					row.PriorityAtUtc = UtcTimestamp.Format(priorityAtUtc);
					this.connection.Update(row);
				}
			});
		}
	}
}

public sealed partial class NearbyCharactersService
{
	public void ImportSnapshots(IReadOnlyList<PlayerLocalData> imported)
	{
		if (imported.Count == 0)
		{
			return;
		}

		var importUtc = DateTime.UtcNow;
		var rows = imported.Select(data =>
		{
			this.RecordRenameIfChanged(data);
			var nameWorldKey = CharacterDirectory.BuildNameWorldKey(data.Name, data.HomeWorldId);
			return (data.ContentId, nameWorldKey, data);
		}).ToArray();

		this.characterDirectory.UpsertSnapshots(rows, importUtc);

		var contentIds = imported.Select(data => data.ContentId).ToArray();
		this.characterDirectory.PrioritizeForLookup(contentIds, importUtc);

		var importedViewModels = new List<NearbyMemberViewModel>(imported.Count);
		foreach (var data in imported)
		{
			if (this.characterDirectory.TryGetByContentId(data.ContentId) is { } known)
			{
				importedViewModels.Add(this.BuildViewModel(known));
			}
		}

		var importedIds = new HashSet<ulong>(contentIds);
		this.currentlyNearby = this.currentlyNearby
			.Where(vm => !importedIds.Contains(vm.KnownCharacter.Data.ContentId))
			.Concat(importedViewModels)
			.ToArray();

		this.lastQueueRebuildUtc = DateTime.MinValue;
		this.RebuildQueueSnapshot();
		this.EnforceCapsIfDue();

		if (this.configuration.NearbySearchMode == NearbySearchMode.Auto)
		{
			this.RunSearchOnce();
		}
	}
}
